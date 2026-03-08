namespace Sqlzibar.Models;

/// <summary>
/// Core security subject (can be user, group, or service account).
/// </summary>
public class SqlzibarSubject
{
    public string Id { get; set; } = string.Empty;
    public string SubjectTypeId { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string? ExternalRef { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public SqlzibarSubjectType? SubjectType { get; set; }
    public SqlzibarUser? User { get; set; }
    public SqlzibarAgent? Agent { get; set; }
    public SqlzibarUserGroup? UserGroup { get; set; }
    public SqlzibarServiceAccount? ServiceAccount { get; set; }
    public ICollection<SqlzibarGrant> Grants { get; set; } = new List<SqlzibarGrant>();
}
