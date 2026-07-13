namespace SqlOS.Fga.Interfaces;

/// <summary>
/// Describes an application entity whose backing FGA resource should be synchronized during
/// <c>SqlOSDbContext&lt;TContext&gt;</c> saves.
/// </summary>
/// <remarks>
/// Resource synchronization does not grant access. Provision subjects and create role grants explicitly.
/// </remarks>
public interface ISqlOSResourceEntity : IHasResourceId
{
    /// <summary>Gets the identifier of the seeded FGA resource type for this entity.</summary>
    string ResourceTypeId { get; }

    /// <summary>Gets the display name stored on the backing FGA resource.</summary>
    string ResourceName { get; }

    /// <summary>Gets the optional parent resource identifier used for inherited access.</summary>
    string? ParentResourceId { get; }

    /// <summary>Gets the optional description stored on the backing FGA resource.</summary>
    string? ResourceDescription { get; }

    /// <summary>Gets whether the backing FGA resource is active.</summary>
    bool ResourceIsActive { get; }
}
