using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using SqlOS.Fga.Configuration;

namespace SqlOS.Benchmarks.Data;

/// <summary>
/// PostgreSQL: parallel <c>COPY ... (FORMAT BINARY)</c> streams over disjoint id ranges. Foreign-key triggers
/// and the closure triggers are skipped for the loading sessions (<c>session_replication_role = replica</c>;
/// the harness supplies the closure rows itself, in key order), secondary indexes are dropped and recreated
/// from their own <c>pg_get_indexdef</c> definitions, and the tables are vacuumed so the visibility map is set
/// the way autovacuum would leave a production table.
/// </summary>
internal sealed class PostgreSqlDatasetLoader(string connectionString, SqlOSFgaOptions fga, Log log) : IDatasetLoader
{
    private string Resources => $"\"{fga.Schema}\".\"{fga.TableNames.Resources}\"";
    private string ResourceTypes => $"\"{fga.Schema}\".\"{fga.TableNames.ResourceTypes}\"";
    private string Closure => $"\"{fga.Schema}\".\"{fga.TableNames.Resources}Closure\"";
    private string ResourceSequence => $"\"{fga.Schema}\".\"{fga.TableNames.Resources}_Seq\"";
    private const string Products = "public.\"Products\"";
    private const string Stores = "public.\"Stores\"";

    public Task ConfigureDatabaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<IReadOnlyDictionary<string, int>> ReadTypeSeqsAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT \"Id\", \"Seq\" FROM {ResourceTypes}", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            map[reader.GetString(0)] = reader.GetInt32(1);
        }

        return map;
    }

    public async Task<long> ReadRootSeqAsync(string rootId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT \"Seq\" FROM {Resources} WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", rootId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task LoadHierarchyAsync(RetailTree tree, IReadOnlyDictionary<string, int> typeSeq, CancellationToken cancellationToken)
    {
        await CopyAsync(Resources, DatasetRows.Columns.Resources, DatasetRows.Hierarchy(tree), cancellationToken);
        await CopyAsync(Stores, DatasetRows.Columns.Stores, DatasetRows.Stores(tree), cancellationToken);
        await CopyAsync(Closure, DatasetRows.Columns.Closure, DatasetRows.HierarchyClosure(tree, typeSeq), cancellationToken);
        await ExecuteAsync($"SELECT setval('{ResourceSequence}', {tree.ProductSeqOffset}, true); ANALYZE {Resources}; ANALYZE {Stores}; ANALYZE {Closure};", cancellationToken);
    }

    public async Task<LoadTiming> GrowProductsAsync(RetailTree tree, IReadOnlyDictionary<string, int> typeSeq, long from, long to, CancellationToken cancellationToken)
    {
        var dropped = await DropSecondaryIndexesAsync(cancellationToken);

        var rows = Stopwatch.StartNew();
        var streams = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        var chunk = (to - from + streams - 1) / streams;
        var copies = new List<Task>();
        for (var start = from; start < to; start += chunk)
        {
            var end = Math.Min(start + chunk, to);
            var (s, e) = (start, end);
            copies.Add(Task.Run(() => CopyAsync(Resources, DatasetRows.Columns.Resources, DatasetRows.ProductResources(tree, s, e), cancellationToken), cancellationToken));
            copies.Add(Task.Run(() => CopyAsync(Products, DatasetRows.Columns.Products, DatasetRows.Products(tree, s, e), cancellationToken), cancellationToken));
        }

        await Task.WhenAll(copies);
        rows.Stop();
        log.Info($"  rows loaded in {rows.Elapsed.TotalSeconds:F1}s ({streams} streams per table)");

        // The closure, one ancestor position per pass, each pass in primary-key order.
        var closure = Stopwatch.StartNew();
        var positions = DatasetRows.AncestorPositions(tree);
        var productType = typeSeq["product"];
        for (var position = 0; position < positions; position++)
        {
            await CopyAsync(Closure, DatasetRows.Columns.Closure, DatasetRows.ProductClosure(tree, position, productType, from, to), cancellationToken);
        }

        closure.Stop();
        log.Info($"  closure rows loaded in {closure.Elapsed.TotalSeconds:F1}s ({positions} ancestor positions)");

        var indexes = Stopwatch.StartNew();
        foreach (var definition in dropped)
        {
            await ExecuteAsync(definition + ";", cancellationToken);
        }

        indexes.Stop();
        log.Info($"  {dropped.Count} secondary indexes rebuilt in {indexes.Elapsed.TotalSeconds:F1}s");

        var maintenance = Stopwatch.StartNew();
        await ExecuteAsync($"VACUUM (ANALYZE) {Resources};", cancellationToken);
        await ExecuteAsync($"VACUUM (ANALYZE) {Products};", cancellationToken);
        await ExecuteAsync($"VACUUM (ANALYZE) {Closure};", cancellationToken);
        await ExecuteAsync($"SELECT setval('{ResourceSequence}', {tree.ProductSeq(to)}, true);", cancellationToken);
        maintenance.Stop();
        log.Info($"  vacuum and analyze in {maintenance.Elapsed.TotalSeconds:F1}s");

        return new LoadTiming(rows.Elapsed, closure.Elapsed, indexes.Elapsed, maintenance.Elapsed);
    }

    public async Task<(long Rows, decimal Hash)> ClosureChecksumAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT COUNT(*)::bigint,
                   COALESCE(SUM("AncestorSeq"::numeric * 31 + "TypeSeq" * 7 + "DescendantSeq"), 0)::numeric
            FROM {Closure}
            """,
            connection)
        {
            CommandTimeout = 0,
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetDecimal(1));
    }

    public async Task<long> DatabaseSizeBytesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT pg_database_size(current_database());", connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<string> EngineVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SHOW server_version;", connection);
        return $"PostgreSQL {(string)(await command.ExecuteScalarAsync(cancellationToken))!}";
    }

    private async Task<List<string>> DropSecondaryIndexesAsync(CancellationToken cancellationToken)
    {
        var definitions = new List<(string Name, string Definition)>();
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"""
                SELECT quote_ident(n.nspname) || '.' || quote_ident(c.relname), pg_get_indexdef(x.indexrelid)
                FROM pg_index AS x
                JOIN pg_class AS c ON c.oid = x.indexrelid
                JOIN pg_namespace AS n ON n.oid = c.relnamespace
                WHERE x.indrelid IN ('{Resources}'::regclass, '{Products}'::regclass)
                  AND NOT x.indisprimary
                  AND NOT EXISTS (SELECT 1 FROM pg_constraint AS k WHERE k.conindid = x.indexrelid)
                ORDER BY x.indrelid, c.relname;
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                definitions.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (name, _) in definitions)
        {
            await ExecuteAsync($"DROP INDEX {name};", cancellationToken);
        }

        return definitions.Select(d => d.Definition).ToList();
    }

    private async Task CopyAsync(
        string table,
        IReadOnlyList<(string Name, Type Type)> columns,
        IEnumerable<object?[]> rows,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var role = new NpgsqlCommand("SET session_replication_role = replica;", connection))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        var columnList = string.Join(", ", columns.Select(c => $"\"{c.Name}\""));
        var types = columns.Select(c => TypeOf(table, c.Name, c.Type)).ToArray();
        await using var writer = await connection.BeginBinaryImportAsync(
            $"COPY {table} ({columnList}) FROM STDIN (FORMAT BINARY)",
            cancellationToken);

        // Synchronous writes: COPY is CPU-bound here and the per-value ValueTask overhead of the async API
        // costs more than it saves. The whole call already runs on its own task.
        foreach (var row in rows)
        {
            writer.StartRow();
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i] is null)
                {
                    writer.WriteNull();
                }
                else
                {
                    writer.Write(row[i], types[i]);
                }
            }
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private static NpgsqlDbType TypeOf(string table, string column, Type type)
        => type == typeof(string)
            ? column == "Name" && table.Contains("SqlOSFga", StringComparison.Ordinal) ? NpgsqlDbType.Text : NpgsqlDbType.Varchar
            : type == typeof(int) ? NpgsqlDbType.Integer
            : type == typeof(long) ? NpgsqlDbType.Bigint
            : type == typeof(bool) ? NpgsqlDbType.Boolean
            : type == typeof(DateTime) ? NpgsqlDbType.Timestamp
            : type == typeof(decimal) ? NpgsqlDbType.Numeric
            : throw new NotSupportedException(type.Name);

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
