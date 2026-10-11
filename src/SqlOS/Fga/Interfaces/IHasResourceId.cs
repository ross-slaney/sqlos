namespace SqlOS.Fga.Interfaces;

/// <summary>
/// Exposes the FGA resource identifier used by SqlOS point checks and query filters.
/// </summary>
/// <remarks>
/// Implement <see cref="ISqlOSResourceEntity"/> instead when SqlOS should synchronize the
/// entity's backing FGA resource during EF Core saves.
/// </remarks>
public interface IHasResourceId
{
    /// <summary>Gets the stable identifier of the entity's backing FGA resource.</summary>
    string ResourceId { get; }
}
