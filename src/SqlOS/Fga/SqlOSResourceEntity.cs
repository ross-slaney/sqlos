using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga;

/// <summary>
/// The base class for entities whose backing FGA resource SqlOS synchronizes during saves: the one-line form
/// of <see cref="ISqlOSResourceEntity"/>. Deriving from it brings the resource id with it, so the entity
/// declares only what describes its resource: the type it was seeded with, its display name, and (when
/// access is inherited) its parent.
/// </summary>
/// <remarks>
/// An entity that already has a base class implements <see cref="ISqlOSResourceEntity"/> itself instead;
/// SqlOS treats both the same. Map the derived entities, never this class: a <c>DbSet</c> of it (or any
/// mapping of it as an entity) fails at model building, because an EF Core hierarchy would put the derived
/// entities in one table.
/// </remarks>
public abstract class SqlOSResourceEntity : ISqlOSResourceEntity
{
    /// <summary>The stable identifier of the entity's backing FGA resource.</summary>
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>The identifier of the seeded FGA resource type for this entity.</summary>
    public abstract string ResourceTypeId { get; }

    /// <summary>The display name stored on the backing FGA resource.</summary>
    public abstract string ResourceName { get; }

    /// <summary>The parent resource identifier access is inherited from, or null for a root.</summary>
    public virtual string? ParentResourceId => null;

    /// <summary>The optional description stored on the backing FGA resource.</summary>
    public virtual string? ResourceDescription => null;

    /// <summary>Whether the backing FGA resource is active.</summary>
    public virtual bool ResourceIsActive => true;
}
