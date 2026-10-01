namespace SqlOS.Fga.Models;

/// <summary>
/// A resource a caller holds a usable grant on, as <c>fn_AccessRoots</c> returns it: the resource's compact
/// key and its level in the tree. Every row at or beneath it whose reach includes that level is visible to
/// the caller. Read by <c>BuildFilterAsync</c>; not mapped to a table.
/// </summary>
internal sealed class SqlOSFgaAccessRoot
{
    public long ResourceSeq { get; set; }

    public short Depth { get; set; }
}
