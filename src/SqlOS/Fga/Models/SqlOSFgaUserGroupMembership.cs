namespace SqlOS.Fga.Models;

/// <summary>
/// Junction table for subject-to-group membership.
/// Only subjects of type 'user' or 'service_account' can be members — no nested groups.
/// </summary>
public class SqlOSFgaUserGroupMembership
{
    public string SubjectId { get; set; } = string.Empty;
    public string UserGroupId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public SqlOSFgaSubject? Subject { get; set; }
    public SqlOSFgaUserGroup? UserGroup { get; set; }
}
