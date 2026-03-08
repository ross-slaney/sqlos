namespace Sqlzibar.Models;

/// <summary>
/// Defines the type of subject (user, service_account, group).
/// </summary>
public class SqlzibarSubjectType
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Navigation
    public ICollection<SqlzibarSubject> Subjects { get; set; } = new List<SqlzibarSubject>();
}
