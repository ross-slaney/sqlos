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
    /// The row's FGA scope column: its resource's type and, for each level of the resource tree, the ancestor
    /// from which access flows down to the row. SqlOS fills and maintains it in the database, and list queries
    /// filtered by <c>BuildFilterAsync</c> read it there, so a page costs the same at any table size.
    /// Application code never sets or reads it. Declare it with a private setter,
    /// <code>public byte[]? FgaScope { get; private set; }</code>
    /// or, to keep it off the class's public surface (and out of JSON), implement it explicitly:
    /// <code>byte[]? IHasResourceId.FgaScope => null;</code>
    /// SqlOS maps the column either way.
    /// </summary>
    byte[]? FgaScope { get; }
}
