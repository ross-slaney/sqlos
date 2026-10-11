using System.Security.Cryptography;
using System.Text;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Paging;

namespace SqlOS.Fga;

/// <summary>
/// SqlOS's projection of an application table, the same on both engines. It carries one entry per
/// application row, independent of principals: the key, ResourceId, the declared order columns and the scope. The
/// per-level indexes (and on SQL Server the computed columns they are built on) belong here, not on the
/// application's table or in its EF model: a planned statement reads the projection to decide which rows
/// are visible and the row for everything else, and a page's streams seek the projection.
/// </summary>
internal static class SqlOSFgaScopeIndex
{
    public const string Prefix = "SqlOSFgaScopeIndex_";

    public static string Table(SqlOSFgaScopeTable table)
    {
        var name = Prefix + (table.Schema is null ? "" : table.Schema + "_") + table.Table;
        if (name.Length <= 110) return name;
        return name[..96] + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12];
    }

    /// <summary>The rebuild routine's name without the engine's prefix (<c>sp_</c> on SQL Server, <c>fn_</c> on PostgreSQL).</summary>
    public static string RebuildRoutine(SqlOSFgaScopeTable table) => Table(table) + "_Rebuild";

    /// <summary>The projection's per-level indexes and its resource lookup for maintenance and non-page predicates.</summary>
    public static IReadOnlyList<string> IndexNames(SqlOSFgaScopeTable table, int levels)
        => SqlOSFgaLineage.ScopeIndexNames(table, levels)
            .Append(ResourceIndexName(table))
            .Where(n => n != SqlOSFgaLineage.ScopeMissingIndexName(table.Table))
            .ToList();

    public static string ResourceIndexName(SqlOSFgaScopeTable table) => Table(table) + "_ResourceId";

    public static IReadOnlyList<SqlOSFgaScopeColumn> Columns(SqlOSFgaScopeTable table)
        => SqlOSFgaPageIndex.DirectColumns(table)
            .Append(table.Columns.FirstOrDefault(c => c.Column == table.ResourceIdColumn) ?? new SqlOSFgaScopeColumn(table.ResourceIdColumn, "nvarchar(450)", false))
            .Append(new SqlOSFgaScopeColumn(SqlOSFgaLineage.ScopeColumn, "varbinary(512)", true)).DistinctBy(c => c.Column).ToList();
}
