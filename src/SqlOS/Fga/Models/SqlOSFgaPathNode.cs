namespace SqlOS.Fga.Models;

/// <summary>
/// A resource on the path from the top of a target's tree down to the target, read from the target's lineage:
/// its level, and whether access flows down from it to the target (every resource from it down is active).
/// Explains a decision in <c>TraceResourceAccessAsync</c>; not mapped to a table.
/// </summary>
internal sealed class SqlOSFgaPathNode
{
    public int Level { get; set; }

    public string ResourceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ResourceTypeId { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public bool InReach { get; set; }
}
