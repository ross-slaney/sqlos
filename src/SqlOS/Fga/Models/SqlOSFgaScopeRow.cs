using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Models;

/// <summary>A query-only view of a private index. Never mapped onto an application table.</summary>
internal sealed class SqlOSFgaScopeRow : IHasResourceId
{
    public static string ModelName(string entityName) => "SqlOS.Scope:" + entityName;
    public static string KeyName(int index) => "ScopeKey" + index;

    public string ResourceId { get; set; } = "";
    public byte[]? FgaScope { get; set; }
}
