namespace SqlOS.Fga.Models;

/// <summary>
/// A permission that can be granted. Permissions are capabilities that gate
/// whether a principal can access a feature/endpoint.
/// </summary>
public class SqlOSFgaPermission
{
    public string Id { get; set; } = string.Empty;
    public string? ResourceTypeId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Navigation
    public SqlOSFgaResourceType? ResourceType { get; set; }
    public ICollection<SqlOSFgaRolePermission> RolePermissions { get; set; } = new List<SqlOSFgaRolePermission>();
}
