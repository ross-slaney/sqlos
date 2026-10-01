namespace SqlOS.Fga.Models;

/// <summary>
/// Human user subject extension.
/// </summary>
public sealed class SqlOSFgaUser
{
    private SqlOSFgaUser()
    {
    }

    internal SqlOSFgaUser(string id, string subjectId, string? email, bool isActive, DateTime now)
    {
        Id = id;
        SubjectId = subjectId;
        Email = email;
        IsActive = isActive;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public string Id { get; private set; } = string.Empty;
    public string SubjectId { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public DateTime? LastLoginAt { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    // Navigation
    public SqlOSFgaSubject? Subject { get; private set; }

    internal void RecordLogin(DateTime now) => (LastLoginAt, UpdatedAt) = (now, now);

    internal void Describe(string? email, DateTime now)
        => (Email, UpdatedAt) = (email, now);

    /// <summary>Returns whether the activity changed.</summary>
    internal bool ChangeActivity(bool isActive, DateTime now)
    {
        UpdatedAt = now;
        var changed = IsActive != isActive;
        IsActive = isActive;
        return changed;
    }
}
