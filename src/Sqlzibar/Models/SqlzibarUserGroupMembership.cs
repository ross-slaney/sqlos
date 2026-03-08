namespace Sqlzibar.Models;

/// <summary>
/// Junction table for subject-to-group membership.
/// Only subjects of type 'user' or 'service_account' can be members — no nested groups.
/// </summary>
public class SqlzibarUserGroupMembership
{
    public string SubjectId { get; set; } = string.Empty;
    public string UserGroupId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public SqlzibarSubject? Subject { get; set; }
    public SqlzibarUserGroup? UserGroup { get; set; }
}
