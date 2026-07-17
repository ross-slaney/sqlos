namespace SqlOS.Fga.Models;

/// <summary>
/// Service account for automated/system access.
/// </summary>
public class SqlOSFgaServiceAccount
{
    public string Id { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecretHash { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string ConfigurationOwner { get; set; } = "dashboard";
    public string? ConfigurationSourceKey { get; set; }
    public string? ConfigurationFingerprint { get; set; }
    public DateTime? LastReconciledAt { get; set; }
    public DateTime? ConfigurationOrphanedAt { get; set; }

    // Navigation
    public SqlOSFgaSubject? Subject { get; set; }
}
