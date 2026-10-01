using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Processes;

/// <summary>Creates a subject of any kind for host code (<see cref="ISqlOSFgaSubjectService"/>), in one save.</summary>
internal sealed class CreateFgaSubject(ISqlOSFgaDbContext context)
{
    public async Task<SqlOSFgaSubject> ExecuteAsync(Func<DateTime, SqlOSFgaSubject> create, CancellationToken cancellationToken)
    {
        var subject = create(SqlOSFgaWrites.Now(context));
        context.Set<SqlOSFgaSubject>().Add(subject);
        await context.SaveChangesAsync(cancellationToken);
        return subject;
    }
}

/// <summary>Adds a subject to a group, or removes it, for host code (<see cref="ISqlOSFgaSubjectService"/>).</summary>
internal sealed class ChangeFgaGroupMembership(ISqlOSFgaDbContext context)
{
    /// <summary>Returns whether the subject joined; a member already in the group changes nothing.</summary>
    public async Task<bool> AddAsync(string subjectId, string userGroupId, FgaActor actor, CancellationToken cancellationToken)
    {
        var member = await context.Set<SqlOSFgaSubject>().FirstOrDefaultAsync(subject => subject.Id == subjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Subject '{subjectId}' not found");
        if (member.SubjectTypeId == SqlOSFgaSubject.GroupType)
        {
            throw new InvalidOperationException("Groups cannot be members of other groups");
        }

        if (await context.Set<SqlOSFgaUserGroupMembership>().AnyAsync(m => m.SubjectId == subjectId && m.UserGroupId == userGroupId, cancellationToken))
        {
            return false;
        }

        var group = await GroupAsync(context.Set<SqlOSFgaUserGroup>(), userGroupId, cancellationToken)
            ?? throw new InvalidOperationException($"Group '{userGroupId}' not found");
        context.Set<SqlOSFgaUserGroupMembership>().Add(group.AddMember(member, actor, SqlOSFgaWrites.Now(context)));
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Returns whether the subject left; a subject outside the group changes nothing.</summary>
    public async Task<bool> RemoveAsync(string subjectId, string userGroupId, FgaActor actor, CancellationToken cancellationToken)
    {
        var membership = await context.Set<SqlOSFgaUserGroupMembership>()
            .FirstOrDefaultAsync(m => m.SubjectId == subjectId && m.UserGroupId == userGroupId, cancellationToken);
        if (membership == null)
        {
            return false;
        }

        var group = await GroupAsync(context.Set<SqlOSFgaUserGroup>(), userGroupId, cancellationToken)
            ?? throw new InvalidOperationException($"Group '{userGroupId}' not found");
        group.RemoveMember(membership, actor);
        context.Set<SqlOSFgaUserGroupMembership>().Remove(membership);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>The subject of the group with <paramref name="userGroupId"/>, with its group record.</summary>
    internal static async Task<SqlOSFgaSubject?> GroupAsync(IQueryable<SqlOSFgaUserGroup> groups, string userGroupId, CancellationToken cancellationToken)
        => (await groups
            .Include(group => group.Subject)
            .FirstOrDefaultAsync(group => group.Id == userGroupId, cancellationToken))?.Subject;
}

/// <summary>
/// An operator deletes a subject (the FGA dashboard API). Only a subject nothing depends on can go:
/// one that holds no grant, belongs to no group, has no members, and is neither a machine client's
/// service account nor provisioned by a SCIM directory.
/// </summary>
internal sealed class DeleteFgaSubject(ISqlOSFgaDbContext context)
{
    public async Task<DeleteFgaSubjectOutcome> ExecuteAsync(string subjectId, FgaActor actor, CancellationToken cancellationToken)
    {
        var subject = await context.Set<SqlOSFgaSubject>()
            .Include(s => s.User)
            .Include(s => s.Agent)
            .Include(s => s.ServiceAccount)
            .Include(s => s.UserGroup)
            .FirstOrDefaultAsync(s => s.Id == subjectId, cancellationToken);
        if (subject == null)
        {
            return new DeleteFgaSubjectOutcome.NotFound();
        }

        var refusal = await RefusalAsync(subject, cancellationToken);
        if (refusal != null)
        {
            return new DeleteFgaSubjectOutcome.Refused(refusal);
        }

        subject.Delete(actor);
        foreach (var record in new object?[] { subject.User, subject.Agent, subject.ServiceAccount, subject.UserGroup }.OfType<object>())
        {
            ((DbContext)context).Remove(record);
        }

        context.Set<SqlOSFgaSubject>().Remove(subject);
        await context.SaveChangesAsync(cancellationToken);
        return new DeleteFgaSubjectOutcome.Deleted();
    }

    private async Task<string?> RefusalAsync(SqlOSFgaSubject subject, CancellationToken cancellationToken)
    {
        var grants = await context.Set<SqlOSFgaGrant>().CountAsync(g => g.SubjectId == subject.Id, cancellationToken);
        if (grants > 0)
        {
            return $"Subject '{subject.Id}' still holds {grants} grant(s). Revoke them before deleting the subject.";
        }

        var groups = await context.Set<SqlOSFgaUserGroupMembership>().CountAsync(m => m.SubjectId == subject.Id, cancellationToken);
        if (groups > 0)
        {
            return $"Subject '{subject.Id}' belongs to {groups} group(s). Remove it from them before deleting the subject.";
        }

        if (subject.UserGroup != null)
        {
            var groupId = subject.UserGroup.Id;
            var members = await context.Set<SqlOSFgaUserGroupMembership>().CountAsync(m => m.UserGroupId == groupId, cancellationToken);
            if (members > 0)
            {
                return $"Group subject '{subject.Id}' has {members} member(s). Remove them before deleting the group.";
            }
        }

        var model = ((DbContext)context).Model;
        if (subject.ServiceAccount != null
            && model.FindEntityType(typeof(SqlOSClientApplication)) != null
            && await context.Set<SqlOSClientApplication>().AnyAsync(c => c.ClientId == subject.ServiceAccount.ClientId, cancellationToken))
        {
            return $"Subject '{subject.Id}' is the service account of machine client '{subject.ServiceAccount.ClientId}'. Revoke the machine client instead.";
        }

        if (model.FindEntityType(typeof(SqlOSScimExternalId)) != null
            && await context.Set<SqlOSScimExternalId>().AnyAsync(link => link.FgaSubjectId == subject.Id && link.DeletedAt == null, cancellationToken))
        {
            return $"Subject '{subject.Id}' is provisioned by a SCIM directory. Deprovision it in the directory instead.";
        }

        return null;
    }
}

internal abstract record DeleteFgaSubjectOutcome
{
    private DeleteFgaSubjectOutcome()
    {
    }

    public sealed record Deleted : DeleteFgaSubjectOutcome;

    public sealed record NotFound : DeleteFgaSubjectOutcome;

    public sealed record Refused(string Message) : DeleteFgaSubjectOutcome;
}
