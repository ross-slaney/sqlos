using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.Fga.Models;

/// <summary>
/// A resource in the hierarchy. Grants on parent resources cascade to all descendants.
/// </summary>
/// <remarks>
/// A node of the resource tree. It is placed under a parent only with the parent's
/// <see cref="SqlOSFgaAncestry"/>, a bounded walk of the chain above it, so a resource never
/// becomes its own ancestor and the tree never grows deeper than
/// <c>Fga.MaxResourceHierarchyDepth</c>. Deactivating a resource withdraws the access it passes
/// down to its subtree; the read path applies that rule, so descendants are not changed.
/// </remarks>
public sealed class SqlOSFgaResource : ISqlOSAggregate
{
    private readonly DomainEventBuffer _events = new();

    private SqlOSFgaResource()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string? ParentId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string ResourceTypeId { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    // Navigation
    public SqlOSFgaResource? Parent { get; private set; }
    public ICollection<SqlOSFgaResource> Children { get; private set; } = new List<SqlOSFgaResource>();
    public SqlOSFgaResourceType? ResourceType { get; private set; }
    public ICollection<SqlOSFgaGrant> Grants { get; private set; } = new List<SqlOSFgaGrant>();

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    /// <summary>
    /// Creates a resource under the parent <paramref name="ancestry"/> describes, or a top-level
    /// resource when it has none.
    /// </summary>
    internal static SqlOSFgaResource Create(
        string id,
        string name,
        string resourceTypeId,
        string? description,
        SqlOSFgaAncestry ancestry,
        DateTime now,
        bool isActive = true)
    {
        var resource = new SqlOSFgaResource
        {
            Id = id,
            Name = name,
            ResourceTypeId = resourceTypeId,
            Description = description,
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now
        };
        resource.Place(ancestry);
        resource._events.Raise(new FgaResourceCreated(id, resource.ParentId, resourceTypeId));
        return resource;
    }

    /// <summary>Changes the name, type and description.</summary>
    internal void Describe(string name, string resourceTypeId, string? description, DateTime now)
    {
        UpdatedAt = now;
        if (Name == name && ResourceTypeId == resourceTypeId && Description == description)
        {
            return;
        }

        (Name, ResourceTypeId, Description) = (name, resourceTypeId, description);
        _events.Raise(new FgaResourceDescribed(Id));
    }

    /// <summary>Moves the resource, with its subtree, under the parent <paramref name="ancestry"/> describes.</summary>
    internal void MoveTo(SqlOSFgaAncestry ancestry, FgaActor actor, DateTime now)
    {
        UpdatedAt = now;
        if (ParentId == ancestry.ParentId)
        {
            return;
        }

        var from = ParentId;
        Place(ancestry);
        _events.Raise(new FgaResourceMoved(Id, from, ParentId, actor));
    }

    internal void ChangeActivity(bool isActive, FgaActor actor, DateTime now)
    {
        UpdatedAt = now;
        if (IsActive == isActive)
        {
            return;
        }

        IsActive = isActive;
        _events.Raise(new FgaResourceActivationChanged(Id, isActive, actor));
    }

    /// <summary>
    /// Records that the resource is deleted. A resource with children cannot be: the caller checks
    /// the store, then deletes the row and its grants in the same save.
    /// </summary>
    internal void Delete(FgaActor actor)
        => _events.Raise(new FgaResourceDeleted(Id, ParentId, actor));

    private void Place(SqlOSFgaAncestry ancestry)
    {
        ancestry.EnsureCanHold(Id);
        ParentId = ancestry.ParentId;
    }
}

/// <summary>
/// The chain above a place in the resource tree: the parent first, then its ancestors, as a
/// bounded walk read them. It stops at the top, at a missing ancestor, at the first resource it
/// meets twice, or one step past the maximum depth, so a walk of a malformed tree still ends.
/// </summary>
internal sealed class SqlOSFgaAncestry
{
    private SqlOSFgaAncestry(string? parentId, IReadOnlyList<string> chain, int maxDepth)
    {
        ParentId = parentId;
        Chain = chain;
        MaxDepth = maxDepth;
    }

    /// <summary>No parent: a top-level resource.</summary>
    public static SqlOSFgaAncestry None { get; } = new(null, [], int.MaxValue);

    public string? ParentId { get; }

    /// <summary>The parent, then each ancestor the walk read.</summary>
    public IReadOnlyList<string> Chain { get; }

    public int MaxDepth { get; }

    /// <summary>The chain above an existing parent, read by <paramref name="parentOf"/>.</summary>
    public static async Task<SqlOSFgaAncestry> WalkAsync(
        string resourceId,
        string parentId,
        int maxDepth,
        Func<string, Task<(bool Found, string? ParentId)>> parentOf)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { resourceId };
        string? current = parentId;
        while (!string.IsNullOrWhiteSpace(current) && chain.Count <= maxDepth)
        {
            chain.Add(current);
            if (!seen.Add(current))
            {
                break;
            }

            var (found, parent) = await parentOf(current).ConfigureAwait(false);
            current = found ? parent : null;
        }

        return new SqlOSFgaAncestry(parentId, chain, maxDepth);
    }

    /// <summary>
    /// Refuses a place for <paramref name="resourceId"/> that would make it its own parent or
    /// ancestor, or put it deeper than the maximum depth, with the 7.x messages.
    /// </summary>
    public void EnsureCanHold(string resourceId)
    {
        if (string.Equals(resourceId, ParentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FGA resource parent cannot be the resource itself.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { resourceId };
        for (var depth = 1; depth <= Chain.Count; depth++)
        {
            if (!seen.Add(Chain[depth - 1]))
            {
                throw new InvalidOperationException("FGA resource hierarchy contains a cycle.");
            }

            if (depth > MaxDepth)
            {
                throw new InvalidOperationException($"FGA resource hierarchy exceeds the configured maximum depth of {MaxDepth}.");
            }
        }
    }
}
