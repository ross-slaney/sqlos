namespace SqlOS.Fga.Models;

/// <summary>
/// Keyless entity for the <c>fn_PageNodes</c> table-valued function: one of a caller's minimal
/// granted resources for a permission. Not mapped to any table.
/// </summary>
public class SqlOSFgaPageNode
{
    /// <summary>Gets or sets the granted resource identifier.</summary>
    public string NodeId { get; set; } = string.Empty;
}
