namespace SqlOS.Fga.Models;

/// <summary>
/// A resource the caller may see, as <c>fn_VisibleSet</c> returns it: its id, which the "list first" filter
/// <c>BuildFilterAsync</c> returns compares with the application row's resource id. Not mapped to a table.
/// </summary>
internal sealed class SqlOSFgaVisibleResource
{
    public string ResourceId { get; set; } = string.Empty;
}
