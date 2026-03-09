namespace SqlOS.Fga.Interfaces;

/// <summary>
/// Marker interface for entities that have a ResourceId property.
/// Used by the authorization system to filter entities based on accessible resources.
/// </summary>
public interface IHasResourceId
{
    string ResourceId { get; }
}
