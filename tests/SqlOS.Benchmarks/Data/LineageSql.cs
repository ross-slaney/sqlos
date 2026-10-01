using SqlOS.Fga;

namespace SqlOS.Benchmarks.Data;

/// <summary>The checksum queries both loaders run over the lineage and the scope columns.</summary>
internal static class LineageSql
{
    /// <summary>
    /// An order-independent hash of the lineage: for each resource, its key weighted by depth and reach, plus
    /// every ancestor column weighted by its level. Equal counts and hashes mean the same values in every row.
    /// </summary>
    public static string ResourcesChecksum(string table, Func<string, string> quote, Func<string, string> cast)
    {
        var terms = string.Join(" + ", Enumerable.Range(0, DatasetRows.Levels)
            .Select(level => $"COALESCE({cast(quote(SqlOSFgaLineage.AncestorColumn(level)))}, 0) * {level + 1}"));
        return $"""
            SELECT COUNT(*) AS Rows,
                   COALESCE(SUM({cast(quote("Seq"))} * ({quote("Depth")} + 1) * 31 + COALESCE({quote("Reach")}, -1) * 7 + {terms}), 0)
            FROM {table}
            WHERE {quote("Depth")} IS NOT NULL
            """;
    }

    public static string ProductsChecksum(string table, Func<string, string> quote, Func<string, string> cast)
    {
        var terms = string.Join(" + ", Enumerable.Range(0, DatasetRows.Levels)
            .Select(level => $"COALESCE({cast(quote(SqlOSFgaLineage.ScopeAncestorColumn(level)))}, 0) * {level + 1}"));
        return $"""
            SELECT COUNT(*) AS Rows,
                   COALESCE(SUM({cast(quote("Id"))} * ({quote(SqlOSFgaLineage.ScopeReachColumn)} + 1) * 31 + COALESCE({quote(SqlOSFgaLineage.ScopeTypeSeqColumn)}, -1) * 7 + {terms}), 0)
            FROM {table}
            WHERE {quote(SqlOSFgaLineage.ScopeReachColumn)} IS NOT NULL
            """;
    }
}
