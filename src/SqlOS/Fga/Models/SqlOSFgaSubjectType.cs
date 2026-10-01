namespace SqlOS.Fga.Models;

/// <summary>
/// Defines the type of subject (user, service_account, group).
/// </summary>
public sealed class SqlOSFgaSubjectType
{
    private SqlOSFgaSubjectType()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    // Navigation
    public ICollection<SqlOSFgaSubject> Subjects { get; private set; } = new List<SqlOSFgaSubject>();

    internal static SqlOSFgaSubjectType Define(string id, string name, string? description = null)
        => new() { Id = id, Name = name, Description = description };
}
