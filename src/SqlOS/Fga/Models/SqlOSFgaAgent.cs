namespace SqlOS.Fga.Models;

/// <summary>
/// Automated agent subject (job, worker, AI).
/// </summary>
public class SqlOSFgaAgent
{
    public string Id { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string? AgentType { get; set; }
    public string? Description { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public SqlOSFgaSubject? Subject { get; set; }
}
