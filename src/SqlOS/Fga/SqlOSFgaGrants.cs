using Microsoft.EntityFrameworkCore;
using SqlOS.Domain;
using SqlOS.Fga.Models;

namespace SqlOS.Fga;

/// <summary>
/// What every grant writer shares: finding the grant equivalent to one it is about to create, so a
/// grant stays unique per subject, role, resource and window, and revoking grants with their rows.
/// </summary>
internal static class SqlOSFgaGrants
{
    /// <summary>The revocation reason of the grants on a resource that is deleted.</summary>
    public const string ResourceDeletedReason = "resource_deleted";

    /// <summary>
    /// The grant equivalent to the one described that this unit adds, or that is stored and this
    /// unit does not delete; null when there is none.
    /// </summary>
    public static async Task<SqlOSFgaGrant?> FindEquivalentAsync(
        DbContext context,
        string subjectId,
        string roleId,
        string resourceId,
        TimeWindow window,
        CancellationToken cancellationToken)
    {
        var tracked = context.ChangeTracker.Entries<SqlOSFgaGrant>()
            .Where(entry => entry.Entity.IsEquivalentTo(subjectId, roleId, resourceId, window))
            .ToList();
        var added = tracked.FirstOrDefault(entry => entry.State == EntityState.Added);
        if (added != null)
        {
            return added.Entity;
        }

        var deleted = tracked.Where(entry => entry.State == EntityState.Deleted).Select(entry => entry.Entity.Id).ToList();
        var (from, to) = (window.EffectiveFrom, window.EffectiveTo);
        return await context.Set<SqlOSFgaGrant>()
            .FirstOrDefaultAsync(
                grant => grant.SubjectId == subjectId
                    && grant.RoleId == roleId
                    && grant.ResourceId == resourceId
                    && grant.EffectiveFrom == from
                    && grant.EffectiveTo == to
                    && !deleted.Contains(grant.Id),
                cancellationToken);
    }

    /// <summary>Revokes <paramref name="grants"/> and deletes their rows in the unit's next save.</summary>
    public static void Revoke(DbContext context, IEnumerable<SqlOSFgaGrant> grants, FgaActor actor, string? reason = null)
    {
        foreach (var grant in grants)
        {
            grant.Revoke(actor, reason);
            context.Remove(grant);
        }
    }
}
