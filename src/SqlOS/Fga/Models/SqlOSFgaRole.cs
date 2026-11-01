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

    /// <summary>Application definitions are reconciled by startup; customer definitions are managed at runtime.</summary>
    public string DefinitionSource { get; set; } = SqlOSFgaRoleSources.Application;
    /// <summary>The immutable resource boundary owning a customer definition. Never inferred from an ID prefix.</summary>
    public string? OwnerResourceId { get; set; }
    /// <summary>Optimistic concurrency token for definition and grant mutations.</summary>
    public long Revision { get; set; } = 1;
    public DateTime? ArchivedAt { get; set; }

    // Navigation
    public ICollection<SqlOSFgaRolePermission> RolePermissions { get; set; } = new List<SqlOSFgaRolePermission>();
    public ICollection<SqlOSFgaGrant> Grants { get; set; } = new List<SqlOSFgaGrant>();
}

/// <summary>Ownership of a role definition, independent of where its grants apply.</summary>
public static class SqlOSFgaRoleSources
{
    public const string Application = "application";
    public const string Customer = "customer";
}
