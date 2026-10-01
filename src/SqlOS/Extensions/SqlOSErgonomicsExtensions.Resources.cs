using Microsoft.EntityFrameworkCore;
using SqlOS.Domain;
using SqlOS.Fga;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using static SqlOS.Fga.SqlOSFgaWrites;

namespace SqlOS.Extensions;

public static partial class SqlOSErgonomicsExtensions
{
    /// <summary>
    /// Creates a new manually managed FGA resource with a generated identifier and adds it to the context.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="resourceTypeId">The identifier of an existing FGA resource type.</param>
    /// <param name="name">The resource display name.</param>
    /// <param name="parentResourceId">The optional identifier of the resource's parent.</param>
    /// <param name="description">An optional resource description.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The new tracked resource. Call <see cref="ISqlOSFgaDbContext.SaveChangesAsync(CancellationToken)"/> to persist it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, the resource type or parent does not exist, or the requested hierarchy is invalid.
    /// </exception>
    public static Task<SqlOSFgaResource> CreateResourceAsync(
        this ISqlOSFgaDbContext context,
        string resourceTypeId,
        string name,
        string? parentResourceId = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedResourceTypeId = RequireValue(resourceTypeId, nameof(resourceTypeId));
        return context.CreateResourceWithIdAsync(
            $"{normalizedResourceTypeId}::{Guid.NewGuid():N}",
            normalizedResourceTypeId,
            name,
            parentResourceId,
            description,
            cancellationToken);
    }

    /// <summary>
    /// Creates a new manually managed FGA resource with an explicit identifier and adds it to the context.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="resourceId">The stable identifier for the new resource.</param>
    /// <param name="resourceTypeId">The identifier of an existing FGA resource type.</param>
    /// <param name="name">The resource display name.</param>
    /// <param name="parentResourceId">The optional identifier of the resource's parent.</param>
    /// <param name="description">An optional resource description.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The new tracked resource. Call <see cref="ISqlOSFgaDbContext.SaveChangesAsync(CancellationToken)"/> to persist it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A resource with the same identifier already exists, a required value is empty, the resource type
    /// or parent does not exist, or the requested hierarchy is invalid.
    /// </exception>
    public static async Task<SqlOSFgaResource> CreateResourceWithIdAsync(
        this ISqlOSFgaDbContext context,
        string resourceId,
        string resourceTypeId,
        string name,
        string? parentResourceId = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var tree = new SqlOSFgaResourceTree(context);
        if (await tree.FindAsync(normalizedResourceId, cancellationToken) != null)
        {
            throw new InvalidOperationException($"FGA resource '{normalizedResourceId}' already exists.");
        }

        var normalizedResourceTypeId = RequireValue(resourceTypeId, nameof(resourceTypeId));
        var ancestry = await PlaceAsync(context, tree, normalizedResourceId, NormalizeOptional(parentResourceId), normalizedResourceTypeId, cancellationToken);
        var resource = SqlOSFgaResource.Create(
            normalizedResourceId,
            RequireValue(name, nameof(name)),
            normalizedResourceTypeId,
            NormalizeOptional(description),
            ancestry,
            Now(context));
        context.Set<SqlOSFgaResource>().Add(resource);
        return resource;
    }

    /// <summary>
    /// Idempotently creates or updates a manually managed FGA resource with an explicit identifier.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="resourceId">The stable resource identifier.</param>
    /// <param name="resourceTypeId">The identifier of an existing FGA resource type.</param>
    /// <param name="name">The resource display name.</param>
    /// <param name="parentResourceId">
    /// The optional parent identifier. For an existing resource, <see langword="null"/> preserves its current parent.
    /// </param>
    /// <param name="description">
    /// The optional description. For an existing resource, <see langword="null"/> preserves its current description.
    /// </param>
    /// <param name="isActive">
    /// The optional active state. For an existing resource, <see langword="null"/> preserves its current state.
    /// </param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The added or updated tracked resource. Call <see cref="ISqlOSFgaDbContext.SaveChangesAsync(CancellationToken)"/> to persist it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, the resource type or parent does not exist, or the requested hierarchy is invalid.
    /// </exception>
    public static async Task<SqlOSFgaResource> ProvisionResourceWithIdAsync(
        this ISqlOSFgaDbContext context,
        string resourceId,
        string resourceTypeId,
        string name,
        string? parentResourceId = null,
        string? description = null,
        bool? isActive = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var normalizedResourceTypeId = RequireValue(resourceTypeId, nameof(resourceTypeId));
        var tree = new SqlOSFgaResourceTree(context);
        var resource = await tree.FindAsync(normalizedResourceId, cancellationToken);
        var parentId = resource == null || parentResourceId != null ? NormalizeOptional(parentResourceId) : resource.ParentId;
        var ancestry = await PlaceAsync(context, tree, normalizedResourceId, parentId, normalizedResourceTypeId, cancellationToken);
        var now = Now(context);
        if (resource == null)
        {
            resource = SqlOSFgaResource.Create(
                normalizedResourceId,
                RequireValue(name, nameof(name)),
                normalizedResourceTypeId,
                NormalizeOptional(description),
                ancestry,
                now,
                isActive ?? true);
            context.Set<SqlOSFgaResource>().Add(resource);
            return resource;
        }

        resource.MoveTo(ancestry, FgaActor.Host, now);
        resource.Describe(
            RequireValue(name, nameof(name)),
            normalizedResourceTypeId,
            description != null ? NormalizeOptional(description) : resource.Description,
            now);
        if (isActive.HasValue)
        {
            resource.ChangeActivity(isActive.Value, FgaActor.Host, now);
        }

        return resource;
    }

    /// <summary>
    /// Marks a manually managed FGA resource and all of its direct grants for deletion.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="resourceId">The identifier of the resource to delete.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>A task that completes when the resource and grants have been marked for deletion.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The resource does not exist, has child resources, or <paramref name="resourceId"/> is empty.
    /// </exception>
    /// <remarks>
    /// Child resources are not deleted or reparented. Call
    /// <see cref="ISqlOSFgaDbContext.SaveChangesAsync(CancellationToken)"/> to persist the deletion.
    /// </remarks>
    public static async Task DeleteResourceAsync(
        this ISqlOSFgaDbContext context,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var tree = new SqlOSFgaResourceTree(context);
        var resource = await tree.FindAsync(normalizedResourceId, cancellationToken)
            ?? throw new InvalidOperationException($"FGA resource '{normalizedResourceId}' was not found.");
        if (await tree.HasChildrenAsync(normalizedResourceId, new HashSet<string>(), cancellationToken))
        {
            throw new InvalidOperationException($"FGA resource '{normalizedResourceId}' has child resources. Delete or reparent child resources before deleting this resource.");
        }

        var grants = await context.Set<SqlOSFgaGrant>()
            .Where(grant => grant.ResourceId == normalizedResourceId)
            .ToListAsync(cancellationToken);
        context.Set<SqlOSFgaGrant>().RemoveRange(grants);
        resource.Delete(FgaActor.Host);
        context.Set<SqlOSFgaResource>().Remove(resource);
    }

    /// <summary>
    /// The place under <paramref name="parentId"/> for <paramref name="resourceId"/>, checked in
    /// 7.x's order: the parent is not the resource, the type and the parent exist, then the chain.
    /// </summary>
    private static async Task<SqlOSFgaAncestry> PlaceAsync(
        ISqlOSFgaDbContext context,
        SqlOSFgaResourceTree tree,
        string resourceId,
        string? parentId,
        string resourceTypeId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(resourceId, parentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FGA resource parent cannot be the resource itself.");
        }

        if (!await tree.ResourceTypeExistsAsync(resourceTypeId, cancellationToken))
        {
            throw new InvalidOperationException($"FGA resource type '{resourceTypeId}' was not found. Seed or create the resource type before provisioning resources.");
        }

        if (parentId != null)
        {
            await RequireResourceAsync(context, tree, parentId, cancellationToken);
        }

        var ancestry = await tree.AncestryAsync(resourceId, parentId, cancellationToken);
        ancestry.EnsureCanHold(resourceId);
        return ancestry;
    }

    /// <summary>
    /// Requires a resource that exists, or that a resource-backed entity this unit adds or changes
    /// will create when it is saved.
    /// </summary>
    private static async Task RequireResourceAsync(
        ISqlOSFgaDbContext context,
        SqlOSFgaResourceTree tree,
        string resourceId,
        CancellationToken cancellationToken)
    {
        if (!await tree.ExistsAsync(resourceId, cancellationToken) && !IsPendingResourceEntity(context, resourceId))
        {
            throw new InvalidOperationException($"FGA resource '{resourceId}' was not found.");
        }
    }
}
