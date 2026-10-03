namespace SqlOS.Fga.Models;

/// <summary>
/// The grant that decides a point check, as <c>fn_IsResourceAccessible</c> returns it: the resource on the
/// target's active path that holds the grant (the nearest one when several do), the grant, the live subject
/// it was made to, its role, and the resource's level. No row means the check is denied. Not mapped to a table.
/// </summary>
internal sealed class SqlOSFgaAccessMatch
{
    public string Id { get; set; } = string.Empty;

    public string GrantId { get; set; } = string.Empty;

    public string SubjectId { get; set; } = string.Empty;

    public string RoleId { get; set; } = string.Empty;

    public int Level { get; set; }
}
