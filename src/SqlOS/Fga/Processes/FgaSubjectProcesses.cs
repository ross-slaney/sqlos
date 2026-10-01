using Microsoft.EntityFrameworkCore;
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
