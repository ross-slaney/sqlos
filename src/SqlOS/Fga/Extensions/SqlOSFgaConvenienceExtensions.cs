using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Extensions;

/// <summary>
/// Convenience extension methods that reduce boilerplate in endpoint code.
/// </summary>
public static class SqlOSFgaConvenienceExtensions
{
    private const int DefaultMaxResourceHierarchyDepth = 10;

    /// <summary>
    /// Creates a <see cref="SqlOSFgaResource"/> and adds it to the context (not yet saved).
    /// Returns the generated resource ID so you can assign it to your domain entity.
    /// <example>
    /// <code>
    /// var resourceId = context.CreateResource("retail_root", request.Name, "chain");
    /// chain.ResourceId = resourceId;
    /// await context.SaveChangesAsync();
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="context">The DbContext implementing ISqlOSFgaDbContext.</param>
    /// <param name="parentId">The parent resource ID in the hierarchy.</param>
    /// <param name="name">Display name for the resource.</param>
    /// <param name="resourceTypeId">The resource type identifier.</param>
    /// <param name="id">Optional custom resource ID. If null, a GUID is generated.</param>
    /// <returns>The resource ID (either the provided one or the generated GUID).</returns>
    public static string CreateResource(
        this ISqlOSFgaDbContext context,
        string parentId,
        string name,
        string resourceTypeId,
        string? id = null,
        int maxHierarchyDepth = DefaultMaxResourceHierarchyDepth)
    {
        var resourceId = id ?? Guid.NewGuid().ToString();
        EnsureParentChainDoesNotCreateCycle(context, resourceId, parentId, maxHierarchyDepth);
        var resource = new SqlOSFgaResource
        {
            Id = resourceId,
            ParentId = parentId,
            Name = name,
            ResourceTypeId = resourceTypeId
        };
        context.Set<SqlOSFgaResource>().Add(resource);
        return resourceId;
    }

    private static void EnsureParentChainDoesNotCreateCycle(
        ISqlOSFgaDbContext context,
        string resourceId,
        string parentId,
        int maxHierarchyDepth)
    {
        if (string.Equals(resourceId, parentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FGA resource parent cannot be the resource itself.");
        }

        var maxDepth = Math.Max(1, maxHierarchyDepth);
        var visited = new HashSet<string>(StringComparer.Ordinal) { resourceId };
        string? currentId = parentId;
        var depth = 0;

        while (!string.IsNullOrWhiteSpace(currentId))
        {
            if (!visited.Add(currentId))
            {
                throw new InvalidOperationException("FGA resource hierarchy contains a cycle.");
            }

            if (depth > maxDepth)
            {
                throw new InvalidOperationException($"FGA resource hierarchy exceeds the maximum depth of {maxDepth}.");
            }

            var parent = context.Set<SqlOSFgaResource>()
                .AsNoTracking()
                .Where(r => r.Id == currentId)
                .Select(r => new { r.ParentId })
                .FirstOrDefault();
            currentId = parent?.ParentId;
            depth++;
        }
    }

    /// <summary>
    /// Fetches an entity by predicate, checks authorization, and returns the appropriate HTTP result.
    /// Returns 404 if not found, 403 if denied, or 200 with the mapped DTO.
    /// <example>
    /// <code>
    /// return await authService.AuthorizedDetailAsync(
    ///     context.Chains.Include(c => c.Locations),
    ///     c => c.Id == id,
    ///     subjectId, "CHAIN_VIEW",
    ///     c => new ChainDto { Id = c.Id, Name = c.Name });
    /// </code>
    /// </example>
    /// </summary>
    public static async Task<IResult> AuthorizedDetailAsync<TEntity, TDto>(
        this ISqlOSFgaAuthService authService,
        IQueryable<TEntity> query,
        Expression<Func<TEntity, bool>> predicate,
        string subjectId,
        string permissionKey,
        Func<TEntity, TDto> selector)
        where TEntity : class, IHasResourceId
    {
        var entity = await query.FirstOrDefaultAsync(predicate);
        if (entity is null)
            return Results.NotFound();

        var access = await authService.CheckAccessAsync(subjectId, permissionKey, entity.ResourceId);
        if (!access.Allowed)
            return Results.Json(new { error = "Permission denied" }, statusCode: 403);

        return Results.Ok(selector(entity));
    }
}
