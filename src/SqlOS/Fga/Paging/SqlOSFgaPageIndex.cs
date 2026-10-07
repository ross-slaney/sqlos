using SqlOS.Fga.Configuration;

namespace SqlOS.Fga.Paging;

/// <summary>
/// Names of what a page needs beyond the lineage and the scope column (see <see cref="SqlOSFgaLineage"/>):
/// the grant counts per (principal, resource) that tell the walk where to jump, the direct index per
/// application table that streams rows granted on their own resource, and the routines and triggers that
/// keep both exact. One definition for both database providers and the initializer.
/// </summary>
internal static class SqlOSFgaPageIndex
{
    /// <summary>
    /// Per (principal, resource) — the resource and every ancestor of a granted resource — the principal's
    /// usable grants at or below it (<c>Grants</c>), the children with such grants (<c>GrantChildren</c>, the
    /// split threshold), and the grants below an inactive descendant (<c>CutGrants</c>). Schema v14.
    /// </summary>
    public const string CountsTable = "SqlOSFgaGrantCounts";

    public const string CountsParentIndex = "IX_SqlOSFgaGrantCounts_Parent";

    /// <summary>Rebuilds the counts of the given principals (all when none are given), from the grants and the lineage.</summary>
    public static string CountsRebuildRoutine => "SqlOSFgaGrantCounts_Rebuild";

    /// <summary>Adds (+1) or removes (-1) a set of grants' contributions to the counts.</summary>
    public static string CountsAdjustRoutine => "SqlOSFgaGrantCounts_Adjust";

    /// <summary>Rebuilds the counts of the principals whose grants crossed a validity boundary in a window of time.</summary>
    public static string CountsRefreshRoutine => "SqlOSFgaGrantCounts_Refresh";

    /// <summary>Rebuilds the counts and every direct index: run after the lineage is built.</summary>
    public static string RebuildRoutine => "SqlOSFgaPageIndex_Rebuild";

    /// <summary>The statement triggers on the grants table that keep the counts and the direct indexes current.</summary>
    public static IReadOnlyList<string> GrantTriggerNames(string grantsTable)
        => [$"TR_{grantsTable}_{SqlOSFgaLineage.ScopePrefix}Page_Insert", $"TR_{grantsTable}_{SqlOSFgaLineage.ScopePrefix}Page_Update", $"TR_{grantsTable}_{SqlOSFgaLineage.ScopePrefix}Page_Delete"];

    /// <summary>
    /// The direct index of an application table: one row per (grant, row of the table attached to the granted
    /// resource) carrying the row's key and sort columns, the grant's window, the row's type and whether its
    /// resource is active; indexed per declared order, so a principal's directly granted rows are one seek.
    /// </summary>
    public static string DirectTable(SqlOSFgaScopeTable table)
        => $"{SqlOSFgaLineage.ScopePrefix}Direct_{(table.Schema is null ? "" : table.Schema + "_")}{table.Table}";

    public const string DirectPrefix = "SqlOSFgaDirect_";

    public static string DirectIndexName(SqlOSFgaScopeTable table, string suffix) => $"IX_{DirectTable(table)}_{suffix}";

    public static IReadOnlyList<string> DirectIndexNames(SqlOSFgaScopeTable table)
        => table.Orders.Select(o => DirectIndexName(table, o.Suffix)).ToList();

    /// <summary>Rebuilds one table's direct index from the grants and the rows.</summary>
    public static string DirectRebuildRoutine(SqlOSFgaScopeTable table) => $"{DirectTable(table)}_Rebuild";

    /// <summary>The columns of a direct index beyond the grant: the table's key, then every column a declared order uses.</summary>
    public static IReadOnlyList<SqlOSFgaScopeColumn> DirectColumns(SqlOSFgaScopeTable table)
    {
        var names = new List<string>(table.KeyColumns);
        foreach (var order in table.Orders)
        {
            foreach (var column in order.Columns)
            {
                if (!names.Contains(column, StringComparer.Ordinal))
                {
                    names.Add(column);
                }
            }
        }

        return names.Select(n => table.Columns.FirstOrDefault(c => c.Column == n)
            ?? throw new InvalidOperationException($"The type of column {n} of {table.Table} is unknown to SqlOS; it cannot build the direct index.")).ToList();
    }

    /// <summary>The scope-column offset of a level's ancestor bytes, for SQL text.</summary>
    public static string Offset(int level) => SqlOSFgaLineage.ScopeAncestorOffset(level).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// What a page's statements are built for: the table, the alias EF Core gave it in the filter, the order (the
/// key last), the filter, and whether the permission restricts the resource type.
/// </summary>
internal sealed record SqlOSFgaPageSpec(
    SqlOSFgaScopeTable Table,
    string Alias,
    IReadOnlyList<SqlOSFgaScopeColumn> Order,
    string? IndexSuffix,
    string? PredicateSql,
    bool Typed);
