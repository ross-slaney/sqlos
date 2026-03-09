namespace SqlOS.Fga.Models;

/// <summary>
/// Result of a resource access check.
/// </summary>
public class SqlOSFgaAccessCheckResult
{
    public bool Allowed { get; set; }
    public List<SqlOSFgaAccessTrace>? Trace { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// A step in the access trace showing how the authorization decision was made.
/// </summary>
public class SqlOSFgaAccessTrace
{
    public string Step { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string? ResourceName { get; set; }
    public string? GrantId { get; set; }
    public string? RoleName { get; set; }
    public string? SubjectName { get; set; }
}
