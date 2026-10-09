using System.Globalization;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Services;

namespace SqlOS.Database;

internal sealed partial class PostgreSqlDatabaseProvider
{
    private string ScopeIndexTable(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
        => Qualify(options.Schema, SqlOSFgaScopeIndex.Table(table));

    private static string ScopeIndexExists(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
        => $"EXISTS (SELECT 1 FROM pg_class c INNER JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = '{SqlLiteral(options.Schema)}' AND c.relname = '{SqlLiteral(SqlOSFgaScopeIndex.Table(table))}' AND c.relkind = 'r')";

    /// <summary>The signature the projection was built with, kept as the table's comment; NULL when there is no projection.</summary>
    private static string ScopeIndexStoredSignature(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
        => $"(SELECT obj_description(c.oid, 'pg_class') FROM pg_class c INNER JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = '{SqlLiteral(options.Schema)}' AND c.relname = '{SqlLiteral(SqlOSFgaScopeIndex.Table(table))}' AND c.relkind = 'r')";

    /// <summary>
    /// What the projection's columns are made of: the model's shape, and the catalog's types and collations of
    /// the application's columns, so a migration that changes one (even without touching the C# property)
    /// invalidates the projection.
    /// </summary>
    private static string ScopeIndexSignature(SqlOSFgaScopeTable table)
    {
        var columns = SqlOSFgaScopeIndex.Columns(table);
        var shape = SqlOSFgaFunctionInitializer.Hash(columns.Select(c => $"{c.Column}|{c.StoreType}|{c.IsNullable}"));
        return $"""
            md5('{shape}' || COALESCE((
                SELECT string_agg(c.column_name || '|' || c.data_type || '|' || c.udt_name || '|' || COALESCE(c.character_maximum_length::text, '') || '|' || COALESCE(c.numeric_precision::text, '') || '|' || COALESCE(c.numeric_scale::text, '') || '|' || c.is_nullable || '|' || COALESCE(c.collation_name, '') || '|' || COALESCE(c.generation_expression, ''), ';' ORDER BY c.column_name)
                FROM information_schema.columns c
                WHERE c.table_schema = {ScopeSchemaLiteral(table)} AND c.table_name = '{SqlLiteral(table.Table)}' AND c.column_name IN ({NameList(columns.Select(c => c.Column))})), ''))
            """;
    }

    /// <summary>
    /// The projection of an application table and its rebuild routine (see the SQL Server provider). The table
    /// is created from the application's own columns, so their types and collations carry over; nothing is
    /// added to the application's table. Projection changes rebuild under the same lock as resource moves and
    /// row writes.
    /// </summary>
    private IReadOnlyList<string> ScopeIndexTableSql(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
    {
        var index = ScopeIndexTable(options, table);
        var name = SqlOSFgaScopeIndex.Table(table);
        var columns = SqlOSFgaScopeIndex.Columns(table);
        var list = string.Join(", ", columns.Select(c => QuoteIdentifier(c.Column)));
        var select = string.Join(", ", columns.Select(c => "t." + QuoteIdentifier(c.Column)));
        var keys = string.Join(", ", table.KeyColumns.Select(QuoteIdentifier));
        var lockKey = SqlOSFgaLineage.LineageLockKey(options).ToString(CultureInfo.InvariantCulture);
        var ddl = $"""
            DO $sqlos$
            DECLARE
                v_signature text := {ScopeIndexSignature(table)};
            BEGIN
                PERFORM pg_advisory_xact_lock({lockKey});
                IF {ScopeIndexExists(options, table)} AND {ScopeIndexStoredSignature(options, table)} IS DISTINCT FROM v_signature THEN
                    DROP TABLE {index};
                END IF;
                IF NOT {ScopeIndexExists(options, table)} THEN
                    CREATE TABLE {index} AS SELECT {select} FROM {ScopeTable(table)} t WITH NO DATA;
                    ALTER TABLE {index} ADD CONSTRAINT {QuoteIdentifier("PK_" + name)} PRIMARY KEY ({keys});
                    INSERT INTO {index} ({list}) SELECT {select} FROM {ScopeTable(table)} t;
                    EXECUTE format('COMMENT ON TABLE {index.Replace("'", "''", StringComparison.Ordinal)} IS %L', v_signature);
                END IF;
            END
            $sqlos$;
            """;

        var rebuild = $"""
            CREATE OR REPLACE FUNCTION {Qualify(options.Schema, "fn_" + SqlOSFgaScopeIndex.RebuildRoutine(table))}()
            RETURNS void
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                PERFORM pg_advisory_xact_lock({lockKey});
                {Indent(ScopeIndexMerge(options, table, rows: null, removeMissing: true), 4)}
                -- A rebuild can change the entire distribution: refresh the projection's statistics here, as
                -- rebuilding its indexes would; normal row writes use normal upkeep.
                ANALYZE {index};
            END
            $sqlos$;
            """;
        return [ddl, rebuild];
    }

    /// <summary>
    /// Refresh the projection of the application's rows (those in <paramref name="rows"/>, a relation with the
    /// key columns; every row when null), changed values only; with <paramref name="removeMissing"/>, also drop
    /// the entries of rows that no longer exist. A plain upsert, not a serializable one: the writers of one key
    /// are already serialized by the application table's own primary key, and the rebuild runs under the
    /// exclusive lineage lock.
    /// </summary>
    private string ScopeIndexMerge(SqlOSFgaOptions options, SqlOSFgaScopeTable table, string? rows, bool removeMissing = false)
    {
        var index = ScopeIndexTable(options, table);
        var columns = SqlOSFgaScopeIndex.Columns(table).Select(c => QuoteIdentifier(c.Column)).ToList();
        var keys = string.Join(", ", table.KeyColumns.Select(QuoteIdentifier));
        var only = rows is null
            ? ""
            : $"\nWHERE EXISTS (SELECT 1 FROM {rows} i WHERE {string.Join(" AND ", table.KeyColumns.Select(k => $"i.{QuoteIdentifier(k)} = t.{QuoteIdentifier(k)}"))})";
        var delete = removeMissing
            ? $"\nDELETE FROM {index} d WHERE NOT EXISTS (SELECT 1 FROM {ScopeTable(table)} t WHERE {string.Join(" AND ", table.KeyColumns.Select(k => $"t.{QuoteIdentifier(k)} = d.{QuoteIdentifier(k)}"))});"
            : "";
        return $"""
            INSERT INTO {index} AS d ({string.Join(", ", columns)})
            SELECT {string.Join(", ", columns.Select(c => "t." + c))}
            FROM {ScopeTable(table)} t{only}
            ON CONFLICT ({keys}) DO UPDATE SET {string.Join(", ", columns.Select(c => $"{c} = EXCLUDED.{c}"))}
            WHERE ({string.Join(", ", columns.Select(c => "d." + c))}) IS DISTINCT FROM ({string.Join(", ", columns.Select(c => "EXCLUDED." + c))});{delete}
            """;
    }

    private string ScopeIndexDeleteRows(SqlOSFgaOptions options, SqlOSFgaScopeTable table, string rows)
        => $"""
            DELETE FROM {ScopeIndexTable(options, table)} d
            USING {rows} x
            WHERE {string.Join(" AND ", table.KeyColumns.Select(k => $"d.{QuoteIdentifier(k)} = x.{QuoteIdentifier(k)}"))};
            """;

    /// <summary>
    /// A planned statement reads the application's table through its projection: the scope from the
    /// projection, whose per-level indexes serve the authorization predicate, and every other column from
    /// the row itself. The projection decides which rows are visible and nothing more, so a value it holds
    /// (a key, an order column) is never what the application sees, stale or not.
    /// </summary>
    public string BuildScopeIndexQuerySql(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(table);
        return $"""
            SELECT {string.Join(", ", table.Columns.Select(c => $"{(c.Column == SqlOSFgaLineage.ScopeColumn ? "s" : "a")}.{QuoteIdentifier(c.Column)} AS {QuoteIdentifier(c.Column)}"))}
            FROM {ScopeIndexTable(options, table)} s
            INNER JOIN {ScopeTable(table)} a ON {string.Join(" AND ", table.KeyColumns.Select(k => $"a.{QuoteIdentifier(k)} = s.{QuoteIdentifier(k)}"))}
            """;
    }

    /// <summary>A projection of a table SqlOS no longer maintains (renamed, or no longer protected).</summary>
    private static string StaleScopeIndexTables(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> tables)
        => $"""
            SELECT format('DROP TABLE %I.%I', n.nspname, c.relname) AS statement
            FROM pg_class c
            INNER JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = '{SqlLiteral(options.Schema)}' AND c.relkind = 'r' AND c.relname LIKE '{SqlOSFgaScopeIndex.Prefix.Replace("_", "\\_", StringComparison.Ordinal)}%'
              AND c.relname NOT IN ({(tables.Count == 0 ? "''" : NameList(tables.Select(SqlOSFgaScopeIndex.Table)))})
            """;
}
