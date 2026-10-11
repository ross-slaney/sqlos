namespace SqlOS.Fga.Interfaces;

/// <summary>
/// Exposes the FGA resource identifier used by SqlOS point checks and query filters. It is the only thing
/// SqlOS asks of an application table: the filter <c>BuildFilterAsync</c> returns joins the row's resource to
/// the resources the caller may see, all of which SqlOS keeps in its own tables.
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
