namespace SqlOS.Fga.Models;

/// <summary>
/// Group of users (e.g., teams, departments).
/// </summary>
public sealed class SqlOSFgaUserGroup
{
    private SqlOSFgaUserGroup()
    {
    }

    internal SqlOSFgaUserGroup(string id, string subjectId, string name, string? description, string? groupType, DateTime now)
    {
        Id = id;
        SubjectId = subjectId;
        Name = name;
        Description = description;
        GroupType = groupType;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public string Id { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? GroupType { get; private set; }
    public string SubjectId { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    // Navigation
    public SqlOSFgaSubject? Subject { get; private set; }
    public ICollection<SqlOSFgaUserGroupMembership> Memberships { get; private set; } = new List<SqlOSFgaUserGroupMembership>();

    internal void Describe(string name, string? description, string? groupType, DateTime now)
        => (Name, Description, GroupType, UpdatedAt) = (name, description, groupType, now);

    /// <summary>Returns whether the activity changed.</summary>
    internal bool ChangeActivity(bool isActive, DateTime now)
    {
        UpdatedAt = now;
        var changed = IsActive != isActive;
        IsActive = isActive;
        return changed;
    }
}
