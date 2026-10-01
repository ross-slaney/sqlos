namespace SqlOS.Fga.Models;

/// <summary>
/// Automated agent subject (job, worker, AI).
/// </summary>
public sealed class SqlOSFgaAgent
{
    private SqlOSFgaAgent()
    {
    }

    internal SqlOSFgaAgent(string id, string subjectId, string? agentType, string? description, DateTime now)
    {
        Id = id;
        SubjectId = subjectId;
        AgentType = agentType;
        Description = description;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public string Id { get; private set; } = string.Empty;
    public string SubjectId { get; private set; } = string.Empty;
    public string? AgentType { get; private set; }
    public string? Description { get; private set; }
    public DateTime? LastRunAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    // Navigation
    public SqlOSFgaSubject? Subject { get; private set; }

    internal void RecordRun(DateTime now) => (LastRunAt, UpdatedAt) = (now, now);

    internal void Describe(string? agentType, string? description, DateTime now)
        => (AgentType, Description, UpdatedAt) = (agentType, description, now);
}
