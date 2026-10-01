using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.Database;
using SqlOS.Domain;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Processes;

/// <summary>
/// Gives each SCIM-linked SqlOS user one FGA subject, keyed by the user ID (#448). A subject a SCIM
/// user link points at under another ID (SCIM's own <c>subj_…</c> subjects) merges into the
/// user's subject, which is created from it when missing: its group memberships and grants move,
/// those the user's subject already holds are dropped, and the links follow.
/// </summary>
/// <remarks>
/// A user ID that already names a subject of another kind is left alone: nothing can merge into it.
/// </remarks>
internal sealed class MergeDirectoryUserSubjects(DbContext context)
{
    /// <summary>The revocation reason of a merged subject's grant that the user's subject already held.</summary>
    public const string SubjectMergedReason = "subject_merged";

    /// <summary>Whether a SCIM user link still points at a subject other than its user's.</summary>
    public async Task<bool> IsNeededAsync(CancellationToken cancellationToken)
        => IsMapped && await LinksToOtherSubjects().AnyAsync(cancellationToken);

    /// <summary>
    /// Merges at startup: nothing when no link points at another subject, otherwise everything in
    /// one transaction under a database lock, so instances that start together merge once.
    /// </summary>
    public async Task ExecuteAtStartupAsync(CancellationToken cancellationToken)
    {
        if (!await IsNeededAsync(cancellationToken))
        {
            return;
        }

        if (!context.Database.IsRelational())
        {
            await ExecuteAsync(cancellationToken);
            return;
        }

        var attempt = 0;
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
            {
                context.ChangeTracker.Clear();
            }

            await using var transaction = await context.Database.BeginTransactionAsync(SqlOSDatabase.ExclusiveWorkIsolationLevel(context.Database), cancellationToken);
            await SqlOSDatabase.AcquireExclusiveTransactionLockAsync(
                context.Database,
                "SqlOS:ScimUserSubjectMerge",
                TimeSpan.FromSeconds(30),
                "Could not acquire the SqlOS SCIM user subject merge lock.",
                cancellationToken);
            await ExecuteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>Merges the other subjects of every SCIM-linked user, oldest link first, saving after each user.</summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!IsMapped)
        {
            return;
        }

        var userIds = await LinksToOtherSubjects()
            .OrderBy(link => link.CreatedAt)
            .ThenBy(link => link.Id)
            .Select(link => link.EntityId)
            .ToListAsync(cancellationToken);
        foreach (var userId in userIds.Distinct(StringComparer.Ordinal))
        {
            await MergeUserAsync(userId, FgaActor.Upgrade, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Merges the subjects that <paramref name="userId"/>'s SCIM links point at into the user's
    /// subject and returns it, tracked and unsaved; null when no link points at another subject.
    /// </summary>
    public async Task<SqlOSFgaSubject?> MergeUserAsync(string userId, FgaActor actor, CancellationToken cancellationToken)
    {
        if (!IsMapped)
        {
            return null;
        }

        var links = await LinksToOtherSubjects().Where(link => link.EntityId == userId).ToListAsync(cancellationToken);
        if (links.Count == 0)
        {
            return null;
        }

        var mergedIds = links.Select(link => link.FgaSubjectId!).Distinct().ToList();
        var merged = await context.Set<SqlOSFgaSubject>()
            .Include(subject => subject.User)
            .Where(subject => mergedIds.Contains(subject.Id) && subject.SubjectTypeId == SqlOSFgaSubject.UserType)
            .OrderBy(subject => subject.CreatedAt)
            .ThenBy(subject => subject.Id)
            .ToListAsync(cancellationToken);
        var now = SqlOSFgaWrites.Now(context);
        var subject = await context.Set<SqlOSFgaSubject>()
            .Include(candidate => candidate.User)
            .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (subject is { SubjectTypeId: not SqlOSFgaSubject.UserType })
        {
            return null;
        }

        if (merged.Count > 0)
        {
            var isActive = merged.Any(other => other.User is { IsActive: true })
                && await context.Set<SqlOSUser>().AnyAsync(user => user.Id == userId && user.IsActive, cancellationToken);
            subject = WithUserRecord(subject, userId, merged[0], isActive, actor, now);
            foreach (var other in merged)
            {
                await AbsorbAsync(subject, other, actor, now, cancellationToken);
            }
        }

        foreach (var link in links)
        {
            link.FgaSubjectId = userId;
        }

        return subject;
    }

    private bool IsMapped
        => context.Model.FindEntityType(typeof(SqlOSScimExternalId)) != null;

    private IQueryable<SqlOSScimExternalId> LinksToOtherSubjects()
        => context.Set<SqlOSScimExternalId>()
            .Where(link => link.ResourceType == "User" && link.FgaSubjectId != null && link.FgaSubjectId != link.EntityId);

    /// <summary>The user's subject, created from <paramref name="template"/> when missing and given a user record when bare.</summary>
    private SqlOSFgaSubject WithUserRecord(SqlOSFgaSubject? subject, string userId, SqlOSFgaSubject template, bool isActive, FgaActor actor, DateTime now)
    {
        var recordId = SqlOSFgaWrites.TypedRecordId("usr", userId);
        if (subject == null)
        {
            subject = SqlOSFgaSubject.CreateUser(userId, template.DisplayName, template.OrganizationId, userId, recordId, template.User?.Email, isActive, actor, now);
            context.Add(subject);
        }
        else if (subject.User == null)
        {
            subject.AttachUser(recordId, template.User?.Email, isActive, now);
        }

        return subject;
    }

    private async Task AbsorbAsync(SqlOSFgaSubject subject, SqlOSFgaSubject merged, FgaActor actor, DateTime now, CancellationToken cancellationToken)
    {
        var memberships = await context.Set<SqlOSFgaUserGroupMembership>()
            .Where(membership => membership.SubjectId == merged.Id)
            .ToListAsync(cancellationToken);
        foreach (var membership in memberships)
        {
            var group = await ChangeFgaGroupMembership.GroupAsync(context.Set<SqlOSFgaUserGroup>(), membership.UserGroupId, cancellationToken);
            group!.RemoveMember(membership, actor);
            context.Remove(membership);
            if (!await IsMemberAsync(subject.Id, membership.UserGroupId, cancellationToken))
            {
                context.Add(group.AddMember(subject, actor, now));
            }
        }

        var held = await context.Set<SqlOSFgaGrant>()
            .Where(grant => grant.SubjectId == subject.Id)
            .ToListAsync(cancellationToken);
        held.AddRange(context.Set<SqlOSFgaGrant>().Local.Where(grant => grant.SubjectId == subject.Id).Except(held));
        var grants = await context.Set<SqlOSFgaGrant>()
            .Where(grant => grant.SubjectId == merged.Id)
            .OrderBy(grant => grant.CreatedAt)
            .ThenBy(grant => grant.Id)
            .ToListAsync(cancellationToken);
        var authority = new GrantAuthority(actor);
        List<string> moved = [];
        List<string> dropped = [];
        foreach (var grant in grants)
        {
            if (held.Any(own => own.IsEquivalentTo(subject.Id, grant.RoleId, grant.ResourceId, grant.Window)))
            {
                SqlOSFgaGrants.Revoke(context, [grant], actor, SubjectMergedReason);
                dropped.Add(grant.Id);
            }
            else
            {
                grant.MoveTo(subject, authority, now);
                held.Add(grant);
                moved.Add(grant.Id);
            }
        }

        subject.Absorb(merged, memberships.Select(membership => membership.UserGroupId).ToList(), moved, dropped, actor);

        // The moved grants must point at the subject before the merged one is deleted.
        context.ChangeTracker.DetectChanges();
        if (merged.User != null)
        {
            context.Remove(merged.User);
        }

        context.Remove(merged);
    }

    private async Task<bool> IsMemberAsync(string subjectId, string userGroupId, CancellationToken cancellationToken)
        => context.ChangeTracker.Entries<SqlOSFgaUserGroupMembership>().Any(entry =>
                entry.State == EntityState.Added
                && entry.Entity.SubjectId == subjectId
                && entry.Entity.UserGroupId == userGroupId)
            || await context.Set<SqlOSFgaUserGroupMembership>()
                .AnyAsync(membership => membership.SubjectId == subjectId && membership.UserGroupId == userGroupId, cancellationToken);
}
