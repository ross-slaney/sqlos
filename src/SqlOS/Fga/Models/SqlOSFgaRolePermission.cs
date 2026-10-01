namespace SqlOS.Fga.Models;

/// <summary>
/// Junction table linking roles to permissions.
/// </summary>
public sealed class SqlOSFgaRolePermission
{
    private SqlOSFgaRolePermission()
    {
    }

    internal SqlOSFgaRolePermission(string roleId, string permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
    }

    public string RoleId { get; private set; } = string.Empty;
    public string PermissionId { get; private set; } = string.Empty;

    // Navigation
    public SqlOSFgaRole? Role { get; private set; }
    public SqlOSFgaPermission? Permission { get; private set; }
}
