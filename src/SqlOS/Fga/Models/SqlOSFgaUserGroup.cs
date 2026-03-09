namespace SqlOS.Fga.Models;

/// <summary>
/// Group of users (e.g., teams, departments).
/// </summary>
public class SqlOSFgaUserGroup
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? GroupType { get; set; }
    public string SubjectId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public SqlOSFgaSubject? Subject { get; set; }
    public ICollection<SqlOSFgaUserGroupMembership> Memberships { get; set; } = new List<SqlOSFgaUserGroupMembership>();
}
