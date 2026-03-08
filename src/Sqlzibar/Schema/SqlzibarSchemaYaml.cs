namespace Sqlzibar.Schema;

/// <summary>
/// Root schema definition for YAML import/export.
/// </summary>
public class SqlzibarSchemaYaml
{
    public int Version { get; set; } = 1;
    public List<ResourceTypeEntry> ResourceTypes { get; set; } = [];
    public List<PermissionEntry> Permissions { get; set; } = [];
    public List<RoleEntry> Roles { get; set; } = [];
}

/// <summary>
/// Resource type entry in the YAML schema.
/// </summary>
public class ResourceTypeEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

/// <summary>
/// Permission entry in the YAML schema.
/// </summary>
public class PermissionEntry
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ResourceTypeId { get; set; }
}

/// <summary>
/// Role entry in the YAML schema. Permissions are referenced by key.
/// </summary>
public class RoleEntry
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsVirtual { get; set; }
    public List<string> Permissions { get; set; } = [];
}
