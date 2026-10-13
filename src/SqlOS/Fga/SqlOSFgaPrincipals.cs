using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlOS.Fga.Models;

namespace SqlOS.Fga;

/// <summary>The caller of an authorized page: the subject ids (JSON) and the permission id <c>fn_IsResourceAccessible</c> takes.</summary>
internal sealed record SqlOSFgaCaller(string SubjectIds, string PermissionId);

/// <summary>
/// Resolves subjects and permissions for every authorization decision: <c>ToPageAsync</c> and the
/// point checks of <c>SqlOSFgaAuthService</c>.
/// </summary>
internal static class SqlOSFgaPrincipals
{
    /// <summary>
    /// The subject and the subjects of its active groups, or an empty list when the subject does
    /// not exist or is not active.
    /// </summary>
    public static async Task<List<string>> ResolveSubjectIdsAsync(
        DbContext context,
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        var subject = await ActiveSubject(context, subjectId)
            .Select(subject => new
            {
                GroupSubjectIds = context.Set<SqlOSFgaUserGroupMembership>()
                    .Where(membership => membership.SubjectId == subject.Id)
                    .Join(
                        context.Set<SqlOSFgaUserGroup>(),
                        membership => membership.UserGroupId,
                        group => group.Id,
                        (membership, group) => group)
                    .Where(group => group.IsActive)
                    .Select(group => group.SubjectId)
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        return subject == null ? [] : [subjectId, .. subject.GroupSubjectIds];
    }

    /// <summary>Whether the subject exists and is active.</summary>
    public static Task<bool> IsSubjectActiveAsync(
        DbContext context,
        string subjectId,
        CancellationToken cancellationToken = default)
        => ActiveSubject(context, subjectId).AnyAsync(cancellationToken);

    /// <summary>The permission with this key, or <see langword="null"/>.</summary>
    public static Task<SqlOSFgaPermission?> FindPermissionAsync(
        DbContext context,
        string permissionKey,
        CancellationToken cancellationToken = default)
        => context.Set<SqlOSFgaPermission>()
            .AsNoTracking()
            .FirstOrDefaultAsync(permission => permission.Key == permissionKey, cancellationToken);

    /// <summary>
    /// The caller for a page, or <see langword="null"/> with a warning when the subject or the
    /// permission is unknown: such a caller sees nothing.
    /// </summary>
    public static async Task<SqlOSFgaCaller?> ResolveCallerAsync(
        DbContext context,
        string subjectId,
        string permissionKey,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var subjectIds = await ResolveSubjectIdsAsync(context, subjectId, cancellationToken);
        if (subjectIds.Count == 0)
        {
            logger.LogWarning("No subjects found for {SubjectId}", subjectId);
            return null;
        }

        var permission = await FindPermissionAsync(context, permissionKey, cancellationToken);
        if (permission == null)
        {
            logger.LogWarning("Permission {PermissionKey} not found", permissionKey);
            return null;
        }

        return new SqlOSFgaCaller(JsonSerializer.Serialize(subjectIds), permission.Id);
    }

    // An active user, a service account that has not expired, an active group, or an agent.
    private static IQueryable<SqlOSFgaSubject> ActiveSubject(DbContext context, string subjectId)
        => context.Set<SqlOSFgaSubject>()
            .AsNoTracking()
            .Where(subject => subject.Id == subjectId)
            .Where(subject =>
                (subject.SubjectTypeId == "user"
                    && context.Set<SqlOSFgaUser>().Any(user => user.SubjectId == subject.Id && user.IsActive))
                || (subject.SubjectTypeId == "service_account"
                    && context.Set<SqlOSFgaServiceAccount>().Any(account => account.SubjectId == subject.Id
                        && (account.ExpiresAt == null || account.ExpiresAt > DateTime.UtcNow)))
                || (subject.SubjectTypeId == "group"
                    && context.Set<SqlOSFgaUserGroup>().Any(group => group.SubjectId == subject.Id && group.IsActive))
                || (subject.SubjectTypeId == "agent"
                    && context.Set<SqlOSFgaAgent>().Any(agent => agent.SubjectId == subject.Id)));
}
