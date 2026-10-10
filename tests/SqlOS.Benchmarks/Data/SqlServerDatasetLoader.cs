using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlOS.Fga.Configuration;

namespace SqlOS.Benchmarks.Data;

/// <summary>
/// SQL Server: ordered <c>SqlBulkCopy</c> with a table lock into the clustered primary keys (minimally logged
/// under the simple recovery model), nonclustered indexes disabled during the load and rebuilt afterwards.
/// Bulk copy fires no triggers, so the lineage travels with the rows.
/// </summary>
internal sealed class SqlServerDatasetLoader(string connectionString, SqlOSFgaOptions fga, long? diskBudgetBytes, Log log) : IDatasetLoader
{
    private string Resources => $"[{fga.Schema}].[{fga.TableNames.Resources}]";
    private string ResourceSequence => $"[{fga.Schema}].[{fga.TableNames.Resources}_Seq]";
    private const string Products = "[dbo].[Products]";
    private const string Stores = "[dbo].[Stores]";

    /// <summary>Rows per bulk-copy transaction, so the log truncates under simple recovery as the load runs.</summary>
    private const int BatchRows = 1_000_000;

    public async Task ConfigureDatabaseAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            """
            DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(DB_NAME()) + N' SET RECOVERY SIMPLE;';
            SELECT @sql += N'ALTER DATABASE ' + QUOTENAME(DB_NAME()) + N' MODIFY FILE (NAME = ' + QUOTENAME(name)
                + N', FILEGROWTH = ' + CASE WHEN type = 1 THEN N'1024MB' ELSE N'4096MB' END + N');'
            FROM sys.database_files;
            EXEC (@sql);
            """,
            cancellationToken);

        if (diskBudgetBytes is { } budget)
        {
            // Cap the files below the disk's free space, so running out fails this benchmark with a SQL error
            // instead of filling the disk the CI runner itself needs (which loses every log).
            var gb = budget / 1_000_000_000;
            // Every bulk-copy batch, and every range of SqlOS's lineage rebuild, commits on its own, so the log
            // needs little; the index rebuilds sort in tempdb.
            var logGb = Math.Max(4, gb / 25);
            var temp = Math.Max(8, gb / 10);
            var data = Math.Max(8, gb - logGb - temp);
            await ExecuteAsync(
                $"""
                DECLARE @sql nvarchar(max) = N'';
                SELECT @sql += N'ALTER DATABASE ' + QUOTENAME(DB_NAME()) + N' MODIFY FILE (NAME = ' + QUOTENAME(name)
                    + N', MAXSIZE = ' + CASE WHEN type = 1 THEN N'{logGb}GB' ELSE N'{data}GB' END + N');'
                FROM sys.database_files;
                SELECT @sql += N'ALTER DATABASE tempdb MODIFY FILE (NAME = ' + QUOTENAME(name)
                    + N', MAXSIZE = ' + CAST(CASE WHEN type = 1 THEN 4096 ELSE {temp} * 1024 / (SELECT COUNT(*) FROM tempdb.sys.database_files WHERE type = 0) END AS nvarchar(20)) + N'MB);'
                FROM tempdb.sys.database_files;
                EXEC (@sql);
                """,
                cancellationToken);
            log.Info($"  file caps: data {data} GB, log {logGb} GB, tempdb {temp} GB (disk budget {gb} GB)");
        }
    }

    public async Task<long> ReadRootSeqAsync(string rootId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Seq FROM {Resources} WHERE Id = @id";
        command.Parameters.AddWithValue("@id", rootId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task LoadHierarchyAsync(RetailTree tree, CancellationToken cancellationToken)
    {
        await BulkCopyAsync(Resources, DatasetRows.Columns.Resources, DatasetRows.Hierarchy(tree), orderedBy: null, cancellationToken);
        await BulkCopyAsync(Stores, DatasetRows.Columns.Stores, DatasetRows.Stores(tree), orderedBy: null, cancellationToken);
        await ExecuteAsync($"ALTER SEQUENCE {ResourceSequence} RESTART WITH {tree.ProductSeqOffset + 1}", cancellationToken);
    }

    public async Task<LoadTiming> GrowProductsAsync(RetailTree tree, long from, long to, CancellationToken cancellationToken)
    {
        var disabled = await DisableNonclusteredIndexesAsync(cancellationToken);

        var rows = Stopwatch.StartNew();
        await Task.WhenAll(
            Task.Run(() => BulkCopyAsync(Resources, DatasetRows.Columns.Resources, DatasetRows.ProductResources(tree, from, to), orderedBy: ["Id"], cancellationToken), cancellationToken),
            Task.Run(() => BulkCopyAsync(Products, DatasetRows.Columns.Products, DatasetRows.Products(tree, from, to), orderedBy: ["Id"], cancellationToken), cancellationToken));
        rows.Stop();
        await ExecuteAsync("CHECKPOINT;", cancellationToken);
        log.Info($"  rows loaded in {rows.Elapsed.TotalSeconds:F1}s (lineage included)");
        await LogFileSizesAsync("load", cancellationToken);

        var indexes = Stopwatch.StartNew();
        foreach (var (table, index) in disabled)
        {
            await ExecuteAsync($"ALTER INDEX [{index}] ON {table} REBUILD WITH (SORT_IN_TEMPDB = ON);", cancellationToken);
        }

        indexes.Stop();
        await ExecuteAsync("CHECKPOINT;", cancellationToken);
        log.Info($"  {disabled.Count} nonclustered indexes rebuilt in {indexes.Elapsed.TotalSeconds:F1}s");
        await LogFileSizesAsync("index rebuilds", cancellationToken);

        // Bulk copy skips constraint checks and leaves the foreign keys untrusted; validate them so the
        // database is in the state an application's would be, then refresh statistics like the paper did.
        var maintenance = Stopwatch.StartNew();
        await ExecuteAsync($"ALTER TABLE {Resources} WITH CHECK CHECK CONSTRAINT ALL;", cancellationToken);
        await ExecuteAsync($"UPDATE STATISTICS {Resources}; UPDATE STATISTICS {Products};", cancellationToken);
        await ExecuteAsync($"ALTER SEQUENCE {ResourceSequence} RESTART WITH {tree.ProductSeq(to) + 1}", cancellationToken);
        maintenance.Stop();
        log.Info($"  constraints validated and statistics updated in {maintenance.Elapsed.TotalSeconds:F1}s");
        await LogFileSizesAsync("validation", cancellationToken);

        return new LoadTiming(rows.Elapsed, indexes.Elapsed, maintenance.Elapsed);
    }

    public async Task<LineageChecksum> LineageChecksumAsync(CancellationToken cancellationToken)
    {
        static string Quote(string column) => $"[{column}]";
        static string Cast(string expression) => $"CONVERT(DECIMAL(38, 0), {expression})";
        var (rows, hash) = await ChecksumAsync(LineageSql.ResourcesChecksum(Resources, Quote, Cast), cancellationToken);
        return new LineageChecksum(rows, hash);
    }

    private async Task<(long Rows, decimal Hash)> ChecksumAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = sql.Replace("COUNT(*)", "COUNT_BIG(*)", StringComparison.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetDecimal(1));
    }

    public async Task<long> DatabaseSizeBytesAsync(CancellationToken cancellationToken)
    {
        var pages = await ScalarAsync<long>(
            "SELECT CAST(SUM(used_pages) AS bigint) FROM sys.allocation_units;",
            cancellationToken);
        return pages * 8192;
    }

    public async Task<string> EngineVersionAsync(CancellationToken cancellationToken)
    {
        var version = await ScalarAsync<string>(
            "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) + N' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(128));",
            cancellationToken);
        return $"SQL Server {version}";
    }

    /// <summary>Allocated size of the benchmark database's files and tempdb, for the CI log.</summary>
    private async Task LogFileSizesAsync(string phase, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT SUM(CAST(size AS bigint)) * 8 / 1024 FROM sys.database_files WHERE type = 0),
                (SELECT SUM(CAST(size AS bigint)) * 8 / 1024 FROM sys.database_files WHERE type = 1),
                (SELECT SUM(CAST(size AS bigint)) * 8 / 1024 FROM tempdb.sys.database_files);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        log.Info($"  files after {phase}: data {reader.GetInt64(0) / 1024.0:F1} GB, log {reader.GetInt64(1) / 1024.0:F1} GB, tempdb {reader.GetInt64(2) / 1024.0:F1} GB");
    }

    private async Task<List<(string Table, string Index)>> DisableNonclusteredIndexesAsync(CancellationToken cancellationToken)
    {
        var indexes = new List<(string, string)>();
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT QUOTENAME(OBJECT_SCHEMA_NAME(i.object_id)) + N'.' + QUOTENAME(OBJECT_NAME(i.object_id)), i.name
                FROM sys.indexes AS i
                WHERE i.object_id IN (OBJECT_ID(N'{Resources}'), OBJECT_ID(N'{Products}'))
                  AND i.type = 2
                ORDER BY i.object_id, i.index_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                indexes.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (table, index) in indexes)
        {
            await ExecuteAsync($"ALTER INDEX [{index}] ON {table} DISABLE;", cancellationToken);
        }

        return indexes;
    }

    private async Task BulkCopyAsync(
        string table,
        IReadOnlyList<(string Name, Type Type)> columns,
        IEnumerable<object?[]> rows,
        string[]? orderedBy,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, externalTransaction: null)
        {
            DestinationTableName = table,
            BatchSize = BatchRows,
            BulkCopyTimeout = 0,
            EnableStreaming = true,
        };
        foreach (var (name, _) in columns)
        {
            bulk.ColumnMappings.Add(name, name);
        }

        // The rows arrive in clustered-key order; saying so lets SQL Server append without a sort.
        foreach (var column in orderedBy ?? [])
        {
            bulk.ColumnOrderHints.Add(column, Microsoft.Data.SqlClient.SortOrder.Ascending);
        }

        await using var reader = new GeneratedDataReader(columns, rows);
        await bulk.WriteToServerAsync(reader, cancellationToken);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
