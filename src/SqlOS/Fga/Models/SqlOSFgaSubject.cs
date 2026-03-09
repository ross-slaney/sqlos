namespace SqlOS.Fga.Models;

/// <summary>
/// Core security subject (can be user, group, or service account).
/// </summary>
public class SqlOSFgaSubject
{
    public string Id { get; set; } = string.Empty;
    public string SubjectTypeId { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string? ExternalRef { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public SqlOSFgaSubjectType? SubjectType { get; set; }
    public SqlOSFgaUser? User { get; set; }
    public SqlOSFgaAgent? Agent { get; set; }
    public SqlOSFgaUserGroup? UserGroup { get; set; }
    public SqlOSFgaServiceAccount? ServiceAccount { get; set; }
    public ICollection<SqlOSFgaGrant> Grants { get; set; } = new List<SqlOSFgaGrant>();
}
