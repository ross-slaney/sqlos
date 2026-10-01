using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SqlOS.Domain;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga;

/// <summary>
/// Keeps the FGA resource of every saved <see cref="ISqlOSResourceEntity"/> in step with it, through
/// the resource's own methods, before a <c>SqlOSDbContext&lt;TContext&gt;</c> save. Every change is
/// validated before any is applied.
/// </summary>
internal static class SqlOSResourceEntitySynchronizer
{
    public static void Sync(DbContext context)
        => SyncAsync(context, CancellationToken.None).GetAwaiter().GetResult();

    public static async Task SyncAsync(DbContext context, CancellationToken cancellationToken)
    {
        var changes = context.ChangeTracker
            .Entries()
            .Where(entry => entry.Entity is ISqlOSResourceEntity
                && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(ResourceEntityChange.From)
            .ToList();
        if (changes.Count == 0)
        {
            return;
        }

        var duplicate = changes.GroupBy(change => change.ResourceId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            throw new InvalidOperationException($"Multiple tracked SqlOS resource entities use resource id '{duplicate.Key}'. Resource ids must be unique in a save operation.");
        }

        var placed = changes.Where(change => change.State != EntityState.Deleted).ToList();
        var deleted = changes.Where(change => change.State == EntityState.Deleted).ToList();
        var deleting = deleted.Select(change => change.ResourceId).ToHashSet(StringComparer.Ordinal);
        var tree = new SqlOSFgaResourceTree(
            context,
            placed.ToDictionary(change => change.ResourceId, change => change.ParentResourceId, StringComparer.Ordinal),
            deleting);

        var ancestries = new Dictionary<string, SqlOSFgaAncestry>(StringComparer.Ordinal);
        foreach (var change in placed)
        {
            if (string.Equals(change.ResourceId, change.ParentResourceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("FGA resource parent cannot be the resource itself.");
            }

            if (!await tree.ResourceTypeExistsAsync(change.ResourceTypeId, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"FGA resource type '{change.ResourceTypeId}' was not found. Seed or create the resource type before saving resource-backed entities.");
            }

            if (change.ParentResourceId != null && !await tree.ExistsAsync(change.ParentResourceId, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"FGA resource '{change.ParentResourceId}' was not found.");
            }

            var ancestry = await tree.AncestryAsync(change.ResourceId, change.ParentResourceId, cancellationToken).ConfigureAwait(false);
            ancestry.EnsureCanHold(change.ResourceId);
            ancestries[change.ResourceId] = ancestry;
        }

        // 7.x's order: every new entity, then every changed one, then every deleted one.
        var resources = new Dictionary<string, SqlOSFgaResource>(StringComparer.Ordinal);
        foreach (var change in changes.OrderBy(change => change.State == EntityState.Added ? 0 : change.State == EntityState.Modified ? 1 : 2))
        {
            var resource = await tree.FindAsync(change.ResourceId, cancellationToken).ConfigureAwait(false);
            switch (change.State)
            {
                case EntityState.Added when resource != null:
                    throw new InvalidOperationException($"FGA resource '{change.ResourceId}' already exists for a new resource-backed entity.");
                case EntityState.Modified when resource == null:
                    throw new InvalidOperationException($"FGA resource '{change.ResourceId}' was not found for a modified resource-backed entity.");
                case EntityState.Deleted when resource == null:
                    throw new InvalidOperationException($"FGA resource '{change.ResourceId}' was not found for a deleted resource-backed entity.");
                case EntityState.Deleted when await tree.HasChildrenAsync(change.ResourceId, deleting, cancellationToken).ConfigureAwait(false):
                    throw new InvalidOperationException($"FGA resource '{change.ResourceId}' has child resources. Delete or reparent child resources before deleting this resource.");
            }

            if (resource != null)
            {
                resources[change.ResourceId] = resource;
            }
        }

        var now = SqlOSFgaWrites.Now(context);
        foreach (var change in placed)
        {
            if (change.State == EntityState.Added)
            {
                context.Set<SqlOSFgaResource>().Add(SqlOSFgaResource.Create(
                    change.ResourceId,
                    change.ResourceName,
                    change.ResourceTypeId,
                    change.ResourceDescription,
                    ancestries[change.ResourceId],
                    now,
                    change.ResourceIsActive));
                continue;
            }

            var resource = resources[change.ResourceId];
            resource.MoveTo(ancestries[change.ResourceId], FgaActor.Host, now);
            resource.Describe(change.ResourceName, change.ResourceTypeId, change.ResourceDescription, now);
            resource.ChangeActivity(change.ResourceIsActive, FgaActor.Host, now);
        }

        foreach (var change in deleted)
        {
            var grants = await context.Set<SqlOSFgaGrant>()
                .Where(grant => grant.ResourceId == change.ResourceId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            SqlOSFgaGrants.Revoke(context, grants, FgaActor.Host, SqlOSFgaGrants.ResourceDeletedReason);
            var resource = resources[change.ResourceId];
            resource.Delete(FgaActor.Host);
            context.Set<SqlOSFgaResource>().Remove(resource);
        }
    }

    private sealed record ResourceEntityChange(
        EntityState State,
        string ResourceId,
        string ResourceTypeId,
        string ResourceName,
        string? ParentResourceId,
        string? ResourceDescription,
        bool ResourceIsActive)
    {
        public static ResourceEntityChange From(EntityEntry entry)
        {
            var entity = (ISqlOSResourceEntity)entry.Entity;
            var resourceId = SqlOSFgaWrites.RequireValue(entity.ResourceId, nameof(ISqlOSResourceEntity.ResourceId));
            return entry.State == EntityState.Deleted
                ? new(entry.State, resourceId, string.Empty, string.Empty, null, null, true)
                : new(
                    entry.State,
                    resourceId,
                    SqlOSFgaWrites.RequireValue(entity.ResourceTypeId, nameof(ISqlOSResourceEntity.ResourceTypeId)),
                    SqlOSFgaWrites.RequireValue(entity.ResourceName, nameof(ISqlOSResourceEntity.ResourceName)),
                    SqlOSFgaWrites.NormalizeOptional(entity.ParentResourceId),
                    SqlOSFgaWrites.NormalizeOptional(entity.ResourceDescription),
                    entity.ResourceIsActive);
        }
    }
}
