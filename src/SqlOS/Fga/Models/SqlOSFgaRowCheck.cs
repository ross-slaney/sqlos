namespace SqlOS.Fga.Models;

/// <summary>
/// The point check's answer for one row, as <c>fn_CheckRow</c> returns it: one row when the caller may use the
/// permission on the row's resource, none otherwise. The "check each row" filter <c>BuildFilterAsync</c>
/// returns is an EXISTS over it. Not mapped to a table.
/// </summary>
internal sealed class SqlOSFgaRowCheck
{
    public bool Allowed { get; set; }
}
