namespace SqlOS.Fga.Models;

/// <summary>
/// Junction table linking roles to permissions.
/// </summary>
public class SqlOSFgaRolePermission
{
    public string RoleId { get; set; } = string.Empty;
    public string PermissionId { get; set; } = string.Empty;

    // Navigation
    public SqlOSFgaRole? Role { get; set; }
    public SqlOSFgaPermission? Permission { get; set; }
}
