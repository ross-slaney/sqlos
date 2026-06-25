using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga;

internal static class SqlOSResourceEntitySynchronizer
{
    private const int DefaultMaxResourceHierarchyDepth = 10;

    public static void Sync(DbContext context)
    {
        var changes = GetResourceEntityChanges(context);
        if (changes.Count == 0)
        {
            return;
        }

        ValidateUniqueResourceIds(changes);
        var changesById = changes.ToDictionary(change => change.ResourceId, StringComparer.Ordinal);
        ValidateResourceChanges(context, changes, changesById);
        ApplyResourceChanges(context, changes);
    }

    public static async Task SyncAsync(DbContext context, CancellationToken cancellationToken)
    {
        var changes = GetResourceEntityChanges(context);
        if (changes.Count == 0)
        {
            return;
        }

        ValidateUniqueResourceIds(changes);
        var changesById = changes.ToDictionary(change => change.ResourceId, StringComparer.Ordinal);
        await ValidateResourceChangesAsync(context, changes, changesById, cancellationToken);
        await ApplyResourceChangesAsync(context, changes, cancellationToken);
    }

    private static List<ResourceEntityChange> GetResourceEntityChanges(DbContext context)
        => context.ChangeTracker
            .Entries()
            .Where(entry => entry.Entity is ISqlOSResourceEntity
                && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => ResourceEntityChange.From(entry, (ISqlOSResourceEntity)entry.Entity))
            .ToList();

    private static void ValidateUniqueResourceIds(IReadOnlyCollection<ResourceEntityChange> changes)
    {
        var duplicate = changes
            .GroupBy(change => change.ResourceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            throw new InvalidOperationException($"Multiple tracked SqlOS resource entities use resource id '{duplicate.Key}'. Resource ids must be unique in a save operation.");
        }
    }

    private static void ValidateResourceChanges(
        DbContext context,
        IReadOnlyCollection<ResourceEntityChange> changes,
        IReadOnlyDictionary<string, ResourceEntityChange> changesById)
    {
        foreach (var change in changes.Where(change => change.State != EntityState.Deleted))
        {
            EnsureResourceParentIsNotSelf(change.ResourceId, change.ParentResourceId);

            if (!ResourceTypeExists(context, change.ResourceTypeId))
            {
                throw new InvalidOperationException($"FGA resource type '{change.ResourceTypeId}' was not found. Seed or create the resource type before saving resource-backed entities.");
            }

            if (change.ParentResourceId != null && !ResourceExistsOrIsPending(context, change.ParentResourceId, changesById))
            {
                throw new InvalidOperationException($"FGA resource '{change.ParentResourceId}' was not found.");
            }

            EnsureParentChainDoesNotCreateCycle(context, change, changesById);
        }

        foreach (var added in changes.Where(change => change.State == EntityState.Added))
        {
            if (ResourceExists(context, added.ResourceId))
            {
                throw new InvalidOperationException($"FGA resource '{added.ResourceId}' already exists for a new resource-backed entity.");
            }
        }

        foreach (var modified in changes.Where(change => change.State == EntityState.Modified))
        {
            if (FindResource(context, modified.ResourceId) == null)
            {
                throw new InvalidOperationException($"FGA resource '{modified.ResourceId}' was not found for a modified resource-backed entity.");
            }
        }

        var deletingResourceIds = changes
            .Where(change => change.State == EntityState.Deleted)
            .Select(change => change.ResourceId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var deleted in changes.Where(change => change.State == EntityState.Deleted))
        {
            if (FindResource(context, deleted.ResourceId) == null)
            {
                throw new InvalidOperationException($"FGA resource '{deleted.ResourceId}' was not found for a deleted resource-backed entity.");
            }

            EnsureResourceHasNoRemainingChildren(context, deleted.ResourceId, deletingResourceIds);
        }
    }

    private static async Task ValidateResourceChangesAsync(
        DbContext context,
        IReadOnlyCollection<ResourceEntityChange> changes,
        IReadOnlyDictionary<string, ResourceEntityChange> changesById,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes.Where(change => change.State != EntityState.Deleted))
        {
            EnsureResourceParentIsNotSelf(change.ResourceId, change.ParentResourceId);

            if (!await ResourceTypeExistsAsync(context, change.ResourceTypeId, cancellationToken))
            {
                throw new InvalidOperationException($"FGA resource type '{change.ResourceTypeId}' was not found. Seed or create the resource type before saving resource-backed entities.");
            }

            if (change.ParentResourceId != null && !await ResourceExistsOrIsPendingAsync(context, change.ParentResourceId, changesById, cancellationToken))
            {
                throw new InvalidOperationException($"FGA resource '{change.ParentResourceId}' was not found.");
            }

            await EnsureParentChainDoesNotCreateCycleAsync(context, change, changesById, cancellationToken);
        }

        foreach (var added in changes.Where(change => change.State == EntityState.Added))
        {
            if (await ResourceExistsAsync(context, added.ResourceId, cancellationToken))
            {
                throw new InvalidOperationException($"FGA resource '{added.ResourceId}' already exists for a new resource-backed entity.");
            }
        }

        foreach (var modified in changes.Where(change => change.State == EntityState.Modified))
        {
            if (await FindResourceAsync(context, modified.ResourceId, cancellationToken) == null)
            {
                throw new InvalidOperationException($"FGA resource '{modified.ResourceId}' was not found for a modified resource-backed entity.");
            }
        }

        var deletingResourceIds = changes
            .Where(change => change.State == EntityState.Deleted)
            .Select(change => change.ResourceId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var deleted in changes.Where(change => change.State == EntityState.Deleted))
        {
            if (await FindResourceAsync(context, deleted.ResourceId, cancellationToken) == null)
            {
                throw new InvalidOperationException($"FGA resource '{deleted.ResourceId}' was not found for a deleted resource-backed entity.");
            }

            await EnsureResourceHasNoRemainingChildrenAsync(context, deleted.ResourceId, deletingResourceIds, cancellationToken);
        }
    }

    private static void ApplyResourceChanges(DbContext context, IReadOnlyCollection<ResourceEntityChange> changes)
    {
        foreach (var added in changes.Where(change => change.State == EntityState.Added))
        {
            context.Set<SqlOSFgaResource>().Add(CreateResource(added));
        }

        foreach (var modified in changes.Where(change => change.State == EntityState.Modified))
        {
            var resource = FindResource(context, modified.ResourceId)!;
            ApplyResourceValues(resource, modified);
        }

        foreach (var deleted in changes.Where(change => change.State == EntityState.Deleted))
        {
            RemoveResourceAndGrants(context, deleted.ResourceId);
        }
    }

    private static async Task ApplyResourceChangesAsync(
        DbContext context,
        IReadOnlyCollection<ResourceEntityChange> changes,
        CancellationToken cancellationToken)
    {
        foreach (var added in changes.Where(change => change.State == EntityState.Added))
        {
            context.Set<SqlOSFgaResource>().Add(CreateResource(added));
        }

        foreach (var modified in changes.Where(change => change.State == EntityState.Modified))
        {
            var resource = await FindResourceAsync(context, modified.ResourceId, cancellationToken);
            ApplyResourceValues(resource!, modified);
        }

        foreach (var deleted in changes.Where(change => change.State == EntityState.Deleted))
        {
            await RemoveResourceAndGrantsAsync(context, deleted.ResourceId, cancellationToken);
        }
    }

    private static SqlOSFgaResource CreateResource(ResourceEntityChange change)
        => new()
        {
            Id = change.ResourceId,
            ParentId = change.ParentResourceId,
            Name = change.ResourceName,
            ResourceTypeId = change.ResourceTypeId,
            Description = change.ResourceDescription,
            IsActive = change.ResourceIsActive
        };

    private static void ApplyResourceValues(SqlOSFgaResource resource, ResourceEntityChange change)
    {
        resource.ParentId = change.ParentResourceId;
        resource.Name = change.ResourceName;
        resource.ResourceTypeId = change.ResourceTypeId;
        resource.Description = change.ResourceDescription;
        resource.IsActive = change.ResourceIsActive;
        resource.UpdatedAt = DateTime.UtcNow;
    }

    private static void RemoveResourceAndGrants(DbContext context, string resourceId)
    {
        var grants = context.Set<SqlOSFgaGrant>()
            .Where(grant => grant.ResourceId == resourceId)
            .ToList();
        context.Set<SqlOSFgaGrant>().RemoveRange(grants);

        var resource = FindResource(context, resourceId)!;
        context.Set<SqlOSFgaResource>().Remove(resource);
    }

    private static async Task RemoveResourceAndGrantsAsync(
        DbContext context,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var grants = await context.Set<SqlOSFgaGrant>()
            .Where(grant => grant.ResourceId == resourceId)
            .ToListAsync(cancellationToken);
        context.Set<SqlOSFgaGrant>().RemoveRange(grants);

        var resource = await FindResourceAsync(context, resourceId, cancellationToken);
        context.Set<SqlOSFgaResource>().Remove(resource!);
    }

    private static void EnsureParentChainDoesNotCreateCycle(
        DbContext context,
        ResourceEntityChange change,
        IReadOnlyDictionary<string, ResourceEntityChange> changesById)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { change.ResourceId };
        var currentId = change.ParentResourceId;
        var depth = 0;

        while (!string.IsNullOrWhiteSpace(currentId))
        {
            if (!visited.Add(currentId))
            {
                throw new InvalidOperationException("FGA resource hierarchy contains a cycle.");
            }

            if (depth > DefaultMaxResourceHierarchyDepth)
            {
                throw new InvalidOperationException($"FGA resource hierarchy exceeds the maximum depth of {DefaultMaxResourceHierarchyDepth}.");
            }

            currentId = GetParentId(context, currentId, changesById);
            depth++;
        }
    }

    private static async Task EnsureParentChainDoesNotCreateCycleAsync(
        DbContext context,
        ResourceEntityChange change,
        IReadOnlyDictionary<string, ResourceEntityChange> changesById,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { change.ResourceId };
        var currentId = change.ParentResourceId;
        var depth = 0;

        while (!string.IsNullOrWhiteSpace(currentId))
        {
            if (!visited.Add(currentId))
            {
                throw new InvalidOperationException("FGA resource hierarchy contains a cycle.");
            }

            if (depth > DefaultMaxResourceHierarchyDepth)
            {
                throw new InvalidOperationException($"FGA resource hierarchy exceeds the maximum depth of {DefaultMaxResourceHierarchyDepth}.");
            }

            currentId = await GetParentIdAsync(context, currentId, changesById, cancellationToken);
            depth++;
        }
    }

    private static string? GetParentId(
        DbContext context,
        string resourceId,
        IReadOnlyDictionary<string, ResourceEntityChange> changesById)
    {
        if (changesById.TryGetValue(resourceId, out var change) && change.State != EntityState.Deleted)
        {
            return change.ParentResourceId;
        }

        var localEntry = FindLocalResourceEntry(context, resourceId);
        if (localEntry != null)
        {
            return localEntry.State == EntityState.Deleted ? null : localEntry.Entity.ParentId;
        }

        return context.Set<SqlOSFgaResource>()
            .AsNoTracking()
            .Where(resource => resource.Id == resourceId)
            .Select(resource => resource.ParentId)
            .FirstOrDefault();
    }

    private static async Task<string?> GetParentIdAsync(
        DbContext context,
        string resourceId,
        IReadOnlyDictionary<string, ResourceEntityChange> changesById,
        CancellationToken cancellationToken)
    {
        if (changesById.TryGetValue(resourceId, out var change) && change.State != EntityState.Deleted)
        {
            return change.ParentResourceId;
        }

        var localEntry = FindLocalResourceEntry(context, resourceId);
        if (localEntry != null)
        {
            return localEntry.State == EntityState.Deleted ? null : localEntry.Entity.ParentId;
        }

        return await context.Set<SqlOSFgaResource>()
            .AsNoTracking()
            .Where(resource => resource.Id == resourceId)
            .Select(resource => resource.ParentId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static bool ResourceTypeExists(DbContext context, string resourceTypeId)
    {
        var localEntry = context.ChangeTracker
            .Entries<SqlOSFgaResourceType>()
            .FirstOrDefault(entry => entry.Entity.Id == resourceTypeId);
        if (localEntry != null)
        {
            return localEntry.State != EntityState.Deleted;
        }

        return context.Set<SqlOSFgaResourceType>().Any(resourceType => resourceType.Id == resourceTypeId);
    }

    private static async Task<bool> ResourceTypeExistsAsync(
        DbContext context,
        string resourceTypeId,
        CancellationToken cancellationToken)
    {
        var localEntry = context.ChangeTracker
            .Entries<SqlOSFgaResourceType>()
            .FirstOrDefault(entry => entry.Entity.Id == resourceTypeId);
        if (localEntry != null)
        {
            return localEntry.State != EntityState.Deleted;
        }

        return await context.Set<SqlOSFgaResourceType>()
            .AnyAsync(resourceType => resourceType.Id == resourceTypeId, cancellationToken);
    }

    private static bool ResourceExistsOrIsPending(
        DbContext context,
        string resourceId,
        IReadOnlyDictionary<string, ResourceEntityChange> changesById)
    {
        if (changesById.TryGetValue(resourceId, out var change))
        {
            return change.State != EntityState.Deleted;
        }

        return ResourceExists(context, resourceId);
    }

    private static async Task<bool> ResourceExistsOrIsPendingAsync(
        DbContext context,
        string resourceId,
        IReadOnlyDictionary<string, ResourceEntityChange> changesById,
        CancellationToken cancellationToken)
    {
        if (changesById.TryGetValue(resourceId, out var change))
        {
            return change.State != EntityState.Deleted;
        }

        return await ResourceExistsAsync(context, resourceId, cancellationToken);
    }

    private static bool ResourceExists(DbContext context, string resourceId)
        => FindResource(context, resourceId) != null;

    private static async Task<bool> ResourceExistsAsync(
        DbContext context,
        string resourceId,
        CancellationToken cancellationToken)
        => await FindResourceAsync(context, resourceId, cancellationToken) != null;

    private static SqlOSFgaResource? FindResource(DbContext context, string resourceId)
    {
        var localEntry = FindLocalResourceEntry(context, resourceId);
        if (localEntry != null)
        {
            return localEntry.State == EntityState.Deleted ? null : localEntry.Entity;
        }

        return context.Set<SqlOSFgaResource>().FirstOrDefault(resource => resource.Id == resourceId);
    }

    private static async Task<SqlOSFgaResource?> FindResourceAsync(
        DbContext context,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var localEntry = FindLocalResourceEntry(context, resourceId);
        if (localEntry != null)
        {
            return localEntry.State == EntityState.Deleted ? null : localEntry.Entity;
        }

        return await context.Set<SqlOSFgaResource>()
            .FirstOrDefaultAsync(resource => resource.Id == resourceId, cancellationToken);
    }

    private static EntityEntry<SqlOSFgaResource>? FindLocalResourceEntry(DbContext context, string resourceId)
        => context.ChangeTracker
            .Entries<SqlOSFgaResource>()
            .FirstOrDefault(entry => entry.Entity.Id == resourceId);

    private static void EnsureResourceHasNoRemainingChildren(
        DbContext context,
        string resourceId,
        ISet<string> deletingResourceIds)
    {
        var hasLocalChild = context.ChangeTracker
            .Entries<SqlOSFgaResource>()
            .Any(entry => entry.Entity.ParentId == resourceId
                && !deletingResourceIds.Contains(entry.Entity.Id)
                && entry.State != EntityState.Deleted);
        if (hasLocalChild)
        {
            throw ChildResourceException(resourceId);
        }

        var hasStoredChild = context.Set<SqlOSFgaResource>()
            .AsNoTracking()
            .Any(resource => resource.ParentId == resourceId && !deletingResourceIds.Contains(resource.Id));
        if (hasStoredChild)
        {
            throw ChildResourceException(resourceId);
        }
    }

    private static async Task EnsureResourceHasNoRemainingChildrenAsync(
        DbContext context,
        string resourceId,
        ISet<string> deletingResourceIds,
        CancellationToken cancellationToken)
    {
        var hasLocalChild = context.ChangeTracker
            .Entries<SqlOSFgaResource>()
            .Any(entry => entry.Entity.ParentId == resourceId
                && !deletingResourceIds.Contains(entry.Entity.Id)
                && entry.State != EntityState.Deleted);
        if (hasLocalChild)
        {
            throw ChildResourceException(resourceId);
        }

        var hasStoredChild = await context.Set<SqlOSFgaResource>()
            .AsNoTracking()
            .AnyAsync(resource => resource.ParentId == resourceId && !deletingResourceIds.Contains(resource.Id), cancellationToken);
        if (hasStoredChild)
        {
            throw ChildResourceException(resourceId);
        }
    }

    private static InvalidOperationException ChildResourceException(string resourceId)
        => new($"FGA resource '{resourceId}' has child resources. Delete or reparent child resources before deleting this resource.");

    private static void EnsureResourceParentIsNotSelf(string resourceId, string? parentId)
    {
        if (string.Equals(resourceId, parentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FGA resource parent cannot be the resource itself.");
        }
    }

    private static string RequireValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ResourceEntityChange(
        EntityEntry Entry,
        EntityState State,
        string ResourceId,
        string ResourceTypeId,
        string ResourceName,
        string? ParentResourceId,
        string? ResourceDescription,
        bool ResourceIsActive)
    {
        public static ResourceEntityChange From(EntityEntry entry, ISqlOSResourceEntity entity)
            => new(
                entry,
                entry.State,
                RequireValue(entity.ResourceId, nameof(ISqlOSResourceEntity.ResourceId)),
                RequireValue(entity.ResourceTypeId, nameof(ISqlOSResourceEntity.ResourceTypeId)),
                RequireValue(entity.ResourceName, nameof(ISqlOSResourceEntity.ResourceName)),
                NormalizeOptional(entity.ParentResourceId),
                NormalizeOptional(entity.ResourceDescription),
                entity.ResourceIsActive);
    }
}
