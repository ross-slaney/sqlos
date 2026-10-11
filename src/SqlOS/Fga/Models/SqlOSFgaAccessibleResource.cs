namespace SqlOS.Fga.Models;

/// <summary>
/// Keyless entity for TVF-based authorization queries.
/// Not mapped to any database table — only used via fn_IsResourceAccessible TVF.
/// </summary>
public class SqlOSFgaAccessibleResource
{
    public string Id { get; set; } = string.Empty;
}
