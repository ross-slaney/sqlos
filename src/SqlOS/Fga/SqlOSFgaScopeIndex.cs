using System.Security.Cryptography;
using System.Text;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Paging;

namespace SqlOS.Fga;

/// <summary>
/// SQL Server's private projection of an application table. It carries one entry per application row,
/// independent of principals: the key, declared order columns and scope. SQL Server's computed columns
/// and per-level indexes belong here, not on the application's table or in its EF model.
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

    public static string RebuildRoutine(SqlOSFgaScopeTable table) => "sp_" + Table(table) + "_Rebuild";

    /// <summary>The projection's indexes: the per-level ones. The missing-rows index stays on the application table, where the rows without a scope are.</summary>
    public static IReadOnlyList<string> IndexNames(SqlOSFgaScopeTable table, int levels)
        => SqlOSFgaLineage.ScopeIndexNames(table, levels)
            .Where(n => n != SqlOSFgaLineage.ScopeMissingIndexName(table.Table))
            .ToList();

    public static IReadOnlyList<SqlOSFgaScopeColumn> Columns(SqlOSFgaScopeTable table)
        => SqlOSFgaPageIndex.DirectColumns(table)
            .Append(new SqlOSFgaScopeColumn(SqlOSFgaLineage.ScopeColumn, "varbinary(512)", true)).ToList();
}
