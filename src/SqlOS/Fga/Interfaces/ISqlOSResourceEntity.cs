namespace SqlOS.Fga.Interfaces;

/// <summary>
/// Implement on application entities that should be mirrored into the SqlOS FGA resource hierarchy.
/// </summary>
public interface ISqlOSResourceEntity : IHasResourceId
{
    string ResourceTypeId { get; }
    string ResourceName { get; }
    string? ParentResourceId { get; }
    string? ResourceDescription { get; }
    bool ResourceIsActive { get; }
}
