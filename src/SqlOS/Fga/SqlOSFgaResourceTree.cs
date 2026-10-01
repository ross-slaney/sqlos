using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga;

/// <summary>
/// Reads the resource tree for a write in a unit of work: what the unit already tracks first (a
/// tracked deletion is gone), then the store. Resources the unit is about to place
/// (<paramref name="pending"/>: resource ID to parent ID) or delete (<paramref name="deleting"/>)
/// win over both when deciding whether a resource exists; a walk still reads a deleting one.
/// </summary>
internal sealed class SqlOSFgaResourceTree(
    DbContext context,
    IReadOnlyDictionary<string, string?>? pending = null,
    IReadOnlySet<string>? deleting = null)
{
    public SqlOSFgaResourceTree(ISqlOSFgaDbContext context)
        : this((DbContext)context)
    {
    }

    public int MaxDepth { get; } = SqlOSFgaHierarchyDepth.Resolve(context.Database, context);

    /// <summary>The tracked or stored resource, or null when there is none (or it is being deleted).</summary>
    public async Task<SqlOSFgaResource?> FindAsync(string resourceId, CancellationToken cancellationToken)
    {
        var tracked = context.ChangeTracker.Entries<SqlOSFgaResource>().FirstOrDefault(entry => entry.Entity.Id == resourceId);
        if (tracked != null)
        {
            return tracked.State == EntityState.Deleted ? null : tracked.Entity;
        }

        return await context.Set<SqlOSFgaResource>()
            .FirstOrDefaultAsync(resource => resource.Id == resourceId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string resourceId, CancellationToken cancellationToken)
        => deleting?.Contains(resourceId) != true
            && (pending?.ContainsKey(resourceId) == true
                || await FindAsync(resourceId, cancellationToken).ConfigureAwait(false) != null);

    public async Task<bool> ResourceTypeExistsAsync(string resourceTypeId, CancellationToken cancellationToken)
    {
        var tracked = context.ChangeTracker.Entries<SqlOSFgaResourceType>().FirstOrDefault(entry => entry.Entity.Id == resourceTypeId);
        return tracked != null
            ? tracked.State != EntityState.Deleted
            : await context.Set<SqlOSFgaResourceType>().AnyAsync(type => type.Id == resourceTypeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The chain above <paramref name="parentId"/>, for placing <paramref name="resourceId"/> under it.</summary>
    public Task<SqlOSFgaAncestry> AncestryAsync(string resourceId, string? parentId, CancellationToken cancellationToken)
        => parentId == null
            ? Task.FromResult(SqlOSFgaAncestry.None)
            : SqlOSFgaAncestry.WalkAsync(resourceId, parentId, MaxDepth, id => ParentOfAsync(id, cancellationToken));

    /// <summary>Whether a resource other than those in <paramref name="leaving"/> still has the resource as its parent.</summary>
    public async Task<bool> HasChildrenAsync(string resourceId, IReadOnlySet<string> leaving, CancellationToken cancellationToken)
    {
        if (context.ChangeTracker.Entries<SqlOSFgaResource>().Any(entry => entry.Entity.ParentId == resourceId
                && entry.State != EntityState.Deleted
                && !leaving.Contains(entry.Entity.Id)))
        {
            return true;
        }

        return await context.Set<SqlOSFgaResource>()
            .AsNoTracking()
            .AnyAsync(resource => resource.ParentId == resourceId && !leaving.Contains(resource.Id), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(bool Found, string? ParentId)> ParentOfAsync(string resourceId, CancellationToken cancellationToken)
    {
        if (pending != null && pending.TryGetValue(resourceId, out var pendingParent))
        {
            return (true, pendingParent);
        }

        var tracked = context.ChangeTracker.Entries<SqlOSFgaResource>().FirstOrDefault(entry => entry.Entity.Id == resourceId);
        if (tracked != null)
        {
            return tracked.State == EntityState.Deleted ? (false, null) : (true, tracked.Entity.ParentId);
        }

        var stored = await context.Set<SqlOSFgaResource>()
            .AsNoTracking()
            .Where(resource => resource.Id == resourceId)
            .Select(resource => new { resource.ParentId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return stored == null ? (false, null) : (true, stored.ParentId);
    }
}
