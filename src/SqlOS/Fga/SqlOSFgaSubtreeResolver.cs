using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Models;

namespace SqlOS.Fga;

/// <summary>Where a resource sits relative to a boundary resource's subtree.</summary>
internal enum SqlOSFgaSubtreeMembership
{
    /// <summary>The resource is the boundary or one of its descendants.</summary>
    Within,

    /// <summary>The ancestor chain ends, or reaches a dangling parent, without meeting the boundary.</summary>
    Outside,

    /// <summary>The resource itself does not exist.</summary>
    ResourceNotFound,

    /// <summary>The boundary resource does not exist.</summary>
    BoundaryNotFound,

    /// <summary>The ancestor chain revisits a resource.</summary>
    HierarchyCycle,

    /// <summary>The ancestor chain is longer than the configured maximum hierarchy depth.</summary>
    HierarchyTooDeep
}

/// <summary>
/// Decides whether an FGA resource is a boundary resource or one of its descendants by walking
/// <c>ParentId</c> links. The walk applies the same guards as the access-check ancestor walk in
/// <c>SqlOSFgaAuthService</c> (visited-set cycle detection and <c>Fga.MaxResourceHierarchyDepth</c>)
/// but returns a typed outcome instead of throwing, so callers can fail closed with an auditable
/// reason. Membership is structural: it follows the tree, not ID prefixes, and ignores
/// <c>IsActive</c>. One instance caches parent lookups for one unit of work.
/// </summary>
internal sealed class SqlOSFgaSubtreeResolver
{
    private readonly IQueryable<SqlOSFgaResource> _resources;
    private readonly int _maxDepth;
    private readonly Dictionary<string, ResourceNode?> _nodes = new(StringComparer.Ordinal);

    public SqlOSFgaSubtreeResolver(IQueryable<SqlOSFgaResource> resources, int maxDepth)
    {
        _resources = resources;
        _maxDepth = SqlOSFgaHierarchyDepth.Normalize(maxDepth);
    }

    /// <summary>Returns the stored identifier of an existing resource, or <c>null</c>.</summary>
    public async Task<string?> FindIdAsync(string resourceId, CancellationToken cancellationToken = default)
        => (await FindAsync(resourceId, cancellationToken))?.Id;

    public async Task<SqlOSFgaSubtreeMembership> CheckAsync(
        string resourceId,
        string boundaryResourceId,
        CancellationToken cancellationToken = default)
    {
        // Compare the identifiers stored in the tree, never caller-supplied spellings.
        var boundary = await FindAsync(boundaryResourceId, cancellationToken);
        if (boundary == null)
        {
            return SqlOSFgaSubtreeMembership.BoundaryNotFound;
        }

        // Walk the whole chain, as the access check does, so a cycle or an over-deep hierarchy
        // anywhere above the resource fails closed even after the boundary has been seen.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var foundBoundary = false;
        string? currentId = resourceId;
        var depth = 0;
        while (!string.IsNullOrWhiteSpace(currentId))
        {
            var node = await FindAsync(currentId, cancellationToken);
            if (node == null)
            {
                if (depth == 0)
                {
                    return SqlOSFgaSubtreeMembership.ResourceNotFound;
                }

                // A dangling parent link ends the chain; it cannot prove membership by itself.
                break;
            }

            if (!visited.Add(node.Id))
            {
                return SqlOSFgaSubtreeMembership.HierarchyCycle;
            }

            if (depth > _maxDepth)
            {
                return SqlOSFgaSubtreeMembership.HierarchyTooDeep;
            }

            foundBoundary |= string.Equals(node.Id, boundary.Id, StringComparison.Ordinal);
            currentId = node.ParentId;
            depth++;
        }

        return foundBoundary ? SqlOSFgaSubtreeMembership.Within : SqlOSFgaSubtreeMembership.Outside;
    }

    private async Task<ResourceNode?> FindAsync(string resourceId, CancellationToken cancellationToken)
    {
        if (_nodes.TryGetValue(resourceId, out var cached))
        {
            return cached;
        }

        var node = await _resources
            .AsNoTracking()
            .Where(resource => resource.Id == resourceId)
            .Select(resource => new ResourceNode(resource.Id, resource.ParentId))
            .FirstOrDefaultAsync(cancellationToken);
        _nodes[resourceId] = node;
        if (node != null)
        {
            _nodes.TryAdd(node.Id, node);
        }
        return node;
    }

    private sealed record ResourceNode(string Id, string? ParentId);
}
