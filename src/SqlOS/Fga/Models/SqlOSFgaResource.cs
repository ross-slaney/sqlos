namespace SqlOS.Fga.Models;

/// <summary>
/// A resource in the hierarchy. Grants on parent resources cascade to all descendants.
/// </summary>
public class SqlOSFgaResource
{
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ResourceTypeId { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public SqlOSFgaResource? Parent { get; set; }
    public ICollection<SqlOSFgaResource> Children { get; set; } = new List<SqlOSFgaResource>();
    public SqlOSFgaResourceType? ResourceType { get; set; }
    public ICollection<SqlOSFgaGrant> Grants { get; set; } = new List<SqlOSFgaGrant>();
}
