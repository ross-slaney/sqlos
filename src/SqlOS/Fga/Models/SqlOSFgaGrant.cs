namespace SqlOS.Fga.Models;

/// <summary>
/// A grant assigns a role to a subject for a specific resource.
/// </summary>
public class SqlOSFgaGrant
{
    public string Id { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public SqlOSFgaSubject? Subject { get; set; }
    public SqlOSFgaResource? Resource { get; set; }
    public SqlOSFgaRole? Role { get; set; }
}
