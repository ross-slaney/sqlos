namespace SqlOS.Fga.Models;

/// <summary>
/// Type of resource in the hierarchy (e.g., root, agency, team, project).
/// </summary>
public class SqlOSFgaResourceType
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Navigation
    public ICollection<SqlOSFgaResource> Resources { get; set; } = new List<SqlOSFgaResource>();
    public ICollection<SqlOSFgaPermission> Permissions { get; set; } = new List<SqlOSFgaPermission>();
}
