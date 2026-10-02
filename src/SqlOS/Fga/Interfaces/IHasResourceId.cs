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

    /// <summary>
    /// Gets the row's FGA scope: its resource's type and, for each level of the resource tree, the ancestor
    /// from which access flows down to the row. SqlOS fills and maintains this column in the database;
    /// application code never sets it. Declare it as a public property with a private setter:
    /// <code>public byte[]? FgaScope { get; private set; }</code>
    /// List queries filtered by <c>BuildFilterAsync</c> read it, so a page costs the same at any table size.
    /// </summary>
    byte[]? FgaScope { get; }
}
