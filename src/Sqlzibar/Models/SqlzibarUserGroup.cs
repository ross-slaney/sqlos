namespace Sqlzibar.Models;

/// <summary>
/// Group of users (e.g., teams, departments).
/// </summary>
public class SqlzibarUserGroup
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? GroupType { get; set; }
    public string SubjectId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public SqlzibarSubject? Subject { get; set; }
    public ICollection<SqlzibarUserGroupMembership> Memberships { get; set; } = new List<SqlzibarUserGroupMembership>();
}
