namespace SqlOS.Fga.Models;

/// <summary>
/// Junction table for subject-to-group membership.
/// Only subjects of type 'user' or 'service_account' can be members — no nested groups.
/// </summary>
public sealed class SqlOSFgaUserGroupMembership
{
    private SqlOSFgaUserGroupMembership()
    {
    }

    internal SqlOSFgaUserGroupMembership(string subjectId, string userGroupId, DateTime now)
    {
        SubjectId = subjectId;
        UserGroupId = userGroupId;
        CreatedAt = now;
    }

    public string SubjectId { get; private set; } = string.Empty;
    public string UserGroupId { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }

    // Navigation
    public SqlOSFgaSubject? Subject { get; private set; }
    public SqlOSFgaUserGroup? UserGroup { get; private set; }
}
