using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Services;

namespace SqlOS.Database;

internal sealed partial class SqlServerDatabaseProvider
{
    private static string ScopeIndexTable(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
        => $"[{Escape(options.Schema)}].[{Escape(SqlOSFgaScopeIndex.Table(table))}]";

    private static string ScopeIndexObject(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
        => $"ISNULL(OBJECT_ID(N'{SqlLiteral(ScopeIndexTable(options, table))}'), 0)";

    private static string ScopeIndexSignature(SqlOSFgaScopeTable table)
    {
        var columns = SqlOSFgaScopeIndex.Columns(table);
        var shape = SqlOSFgaFunctionInitializer.Hash(columns.Select(c => $"{c.Column}|{c.StoreType}|{c.IsNullable}"));
        // Collations are database metadata as well as EF configuration. An ALTER COLUMN must invalidate
        // the projection even when its C# property and CLR type did not change.
        return $"""
            CONVERT(nvarchar(64), HASHBYTES('SHA2_256', CONCAT(N'{shape}',
                (SELECT c.name, c.system_type_id, c.max_length, c.precision, c.scale, c.is_nullable, c.collation_name, cc.definition, cc.is_persisted
                 FROM sys.columns c LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
                 WHERE c.object_id = {ObjectOf(table)} AND c.name IN ({NameList(columns.Select(c => c.Column))})
                 ORDER BY c.name FOR XML RAW))), 2)
            """;
    }

    /// <summary>
    /// Preserve the application's column types and collations with SELECT INTO. A join prevents identity
    /// inheritance; a rowversion is copied as bytes, not a new generated rowversion. Projection changes
    /// rebuild under the same lock as resource moves and row writes. No application column is added here.
    /// </summary>
    private IReadOnlyList<string> ScopeIndexTableSql(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
    {
        var index = ScopeIndexTable(options, table);
        var name = SqlOSFgaScopeIndex.Table(table);
        var columns = SqlOSFgaScopeIndex.Columns(table);
        var select = string.Join(", ", columns.Select(c => c.StoreType.Equals("rowversion", StringComparison.OrdinalIgnoreCase) || c.StoreType.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
            ? $"CONVERT(binary(8), t.[{Escape(c.Column)}]) AS [{Escape(c.Column)}]"
            : $"t.[{Escape(c.Column)}]"));
        var list = string.Join(", ", columns.Select(c => $"[{Escape(c.Column)}]"));
        var keys = string.Join(", ", table.KeyColumns.Select(k => $"[{Escape(k)}]"));
        var ddl = $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            {LineageLock(options, "Exclusive")}
            DECLARE @sqlosProjection nvarchar(64) = {ScopeIndexSignature(table)};
            IF OBJECT_ID(N'{SqlLiteral(index)}', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID(N'{SqlLiteral(index)}') AND minor_id = 0 AND name = N'SqlOSProjection' AND CONVERT(nvarchar(64), value) = @sqlosProjection)
                DROP TABLE {index};
            IF OBJECT_ID(N'{SqlLiteral(index)}', N'U') IS NULL
            BEGIN
                SELECT TOP (0) {select} INTO {index}
                FROM {ScopeTable(table)} t CROSS JOIN (SELECT 0 AS dummy) no_identity;
                ALTER TABLE {index} ADD CONSTRAINT [PK_{Escape(name)}] PRIMARY KEY ({keys});
                INSERT INTO {index} ({list}) SELECT {select} FROM {ScopeTable(table)} t;
                EXEC sys.sp_addextendedproperty @name = N'SqlOSProjection', @value = @sqlosProjection,
                    @level0type = N'SCHEMA', @level0name = N'{SqlLiteral(options.Schema)}',
                    @level1type = N'TABLE', @level1name = N'{SqlLiteral(name)}';
            END
            COMMIT TRANSACTION;
            """;

        var rebuild = $"""
            CREATE OR ALTER PROCEDURE [{Escape(options.Schema)}].[{Escape(SqlOSFgaScopeIndex.RebuildRoutine(table))}]
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                {LineageLock(options, "Exclusive")}
                {ScopeIndexMerge(options, table, ScopeTable(table), removeMissing: true)}
                -- A rebuild can change the entire distribution. Refresh the projection's statistics
                -- here, just as rebuilding its indexes would; normal row writes use normal index upkeep.
                UPDATE STATISTICS {index} WITH FULLSCAN;
                COMMIT TRANSACTION;
            END
            """;
        return [ddl, rebuild];
    }

    /// <summary>Refresh only changed projection values; an ordinary row change never fans out by principal.</summary>
    private static string ScopeIndexMerge(SqlOSFgaOptions options, SqlOSFgaScopeTable table, string source, bool removeMissing = false)
    {
        var columns = SqlOSFgaScopeIndex.Columns(table).Select(c => c.Column).ToList();
        var list = string.Join(", ", columns.Select(c => $"[{Escape(c)}]"));
        var values = string.Join(", ", columns.Select(c => $"s.[{Escape(c)}]"));
        // SQL text equality can ignore case and trailing spaces. The projection also supplies values to
        // EF, so it must copy exact bytes, even a case-only change to a string primary key. Comparing keys
        // with normal SQL equality still identifies the row; updating them preserves their stored spelling.
        var changes = columns;
        return $"""
            MERGE {ScopeIndexTable(options, table)} WITH (HOLDLOCK) AS d
            USING (SELECT {list} FROM {source}) AS s
            ON {string.Join(" AND ", table.KeyColumns.Select(k => $"d.[{Escape(k)}] = s.[{Escape(k)}]"))}
            WHEN MATCHED AND EXISTS (SELECT {string.Join(", ", changes.Select(c => $"CONVERT(varbinary(max), d.[{Escape(c)}])"))} EXCEPT SELECT {string.Join(", ", changes.Select(c => $"CONVERT(varbinary(max), s.[{Escape(c)}])"))})
                THEN UPDATE SET {string.Join(", ", changes.Select(c => $"[{Escape(c)}] = s.[{Escape(c)}]"))}
            WHEN NOT MATCHED BY TARGET THEN INSERT ({list}) VALUES ({values})
            {(removeMissing ? "WHEN NOT MATCHED BY SOURCE THEN DELETE" : "")};
            """;
    }

    private static string ScopeIndexRefreshRows(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
        => ScopeIndexMerge(options, table, $"(SELECT t.* FROM {ScopeTable(table)} t INNER JOIN inserted i ON {string.Join(" AND ", table.KeyColumns.Select(k => $"t.[{Escape(k)}] = i.[{Escape(k)}]"))}) current_rows");

    private static string ScopeIndexDeleteRows(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
        => $"DELETE d FROM {ScopeIndexTable(options, table)} d INNER JOIN deleted old ON {string.Join(" AND ", table.KeyColumns.Select(k => $"d.[{Escape(k)}] = old.[{Escape(k)}]"))};";

    /// <summary>
    /// EF's fallback statements read the same projection as the walk. Project the indexed columns from
    /// the private table so native predicates/orderings can seek its indexes; other application columns
    /// still come from the application. The join and all filtering remain in SQL.
    /// </summary>
    internal static string ScopeIndexQuery(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
    {
        var indexed = SqlOSFgaScopeIndex.Columns(table).Select(c => c.Column).ToHashSet(StringComparer.Ordinal);
        return $"""
            SELECT {string.Join(", ", table.Columns.Select(c => $"{(indexed.Contains(c.Column) ? "s" : "a")}.[{Escape(c.Column)}] AS [{Escape(c.Column)}]"))}
            FROM {ScopeIndexTable(options, table)} s
            INNER JOIN {ScopeTable(table)} a ON {string.Join(" AND ", table.KeyColumns.Select(k => $"a.[{Escape(k)}] = s.[{Escape(k)}]"))}
            """;
    }

    private static string StaleScopeIndexTables(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> tables)
        => $"""
            SELECT N'DROP TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(t.object_id)) + N'.' + QUOTENAME(t.name) + N';' AS Statement
            FROM sys.tables t
            WHERE OBJECT_SCHEMA_NAME(t.object_id) = N'{SqlLiteral(options.Schema)}' AND t.name LIKE N'{SqlOSFgaScopeIndex.Prefix.Replace("_", "[_]", StringComparison.Ordinal)}%'
              AND t.name NOT IN ({(tables.Count == 0 ? "N''" : NameList(tables.Select(SqlOSFgaScopeIndex.Table)))})
            """;

    private static string StaleScopeIndexProcedures(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> tables)
        => $"""
            SELECT N'DROP PROCEDURE ' + QUOTENAME(OBJECT_SCHEMA_NAME(p.object_id)) + N'.' + QUOTENAME(p.name) + N';' AS Statement
            FROM sys.procedures p
            WHERE OBJECT_SCHEMA_NAME(p.object_id) = N'{SqlLiteral(options.Schema)}' AND p.name LIKE N'sp[_]{SqlOSFgaScopeIndex.Prefix.Replace("_", "[_]", StringComparison.Ordinal)}%'
              AND p.name NOT IN ({(tables.Count == 0 ? "N''" : NameList(tables.Select(SqlOSFgaScopeIndex.RebuildRoutine)))})
            """;
}
