namespace SqlOS.Fga.Models;

/// <summary>
/// A role that groups permissions.
/// </summary>
public class SqlOSFgaRole
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsVirtual { get; set; } = false;

    // Navigation
    public ICollection<SqlOSFgaRolePermission> RolePermissions { get; set; } = new List<SqlOSFgaRolePermission>();
    public ICollection<SqlOSFgaGrant> Grants { get; set; } = new List<SqlOSFgaGrant>();
}
