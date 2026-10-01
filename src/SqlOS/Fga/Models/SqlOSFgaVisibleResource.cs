namespace SqlOS.Fga.Models;

/// <summary>
/// Keyless row of the authorized page query behind <c>ListVisibleAsync</c>: a resource the caller may see and
/// its sequence number, the page's cursor key. Not mapped to any database table.
/// </summary>
internal sealed class SqlOSFgaVisibleResource
{
    public string ResourceId { get; set; } = string.Empty;
    public long Seq { get; set; }
}
