namespace SqlOS.Fga.Models;

/// <summary>
/// Human user subject extension.
/// </summary>
public class SqlOSFgaUser
{
    public string Id { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public SqlOSFgaSubject? Subject { get; set; }
}
