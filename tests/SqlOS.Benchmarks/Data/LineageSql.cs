using SqlOS.Fga;

namespace SqlOS.Benchmarks.Data;

/// <summary>The checksum queries both loaders run over the lineage and the scope column.</summary>
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

    /// <summary>
    /// An order-independent hash of the scope values: each row's key with its value, hashed by the engine
    /// (<c>CHECKSUM</c> on SQL Server, <c>hashtext</c> on PostgreSQL), summed. Equal counts and hashes mean the
    /// same value in every row.
    /// </summary>
    public static string ProductsChecksum(string table, bool postgres)
    {
        var scope = postgres ? $"\"{SqlOSFgaLineage.ScopeColumn}\"" : $"[{SqlOSFgaLineage.ScopeColumn}]";
        var hash = postgres
            ? $"hashtext(\"Id\"::text || ':' || encode({scope}, 'hex'))::bigint"
            : $"CAST(CHECKSUM([Id], {scope}) AS DECIMAL(38, 0))";
        return $"""
            SELECT COUNT(*) AS Rows, COALESCE(SUM({hash}), 0)
            FROM {table}
            WHERE {scope} IS NOT NULL
            """;
    }
}
