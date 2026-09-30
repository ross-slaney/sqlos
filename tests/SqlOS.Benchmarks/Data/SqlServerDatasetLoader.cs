using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlOS.Fga.Configuration;

namespace SqlOS.Benchmarks.Data;

/// <summary>
/// SQL Server: ordered <c>SqlBulkCopy</c> with a table lock into the clustered primary keys (minimally logged
/// under the simple recovery model), nonclustered indexes disabled during the load and rebuilt afterwards.
/// </summary>
internal sealed class SqlServerDatasetLoader(string connectionString, SqlOSFgaOptions fga, Log log) : IDatasetLoader
{
    private string Resources => $"[{fga.Schema}].[{fga.TableNames.Resources}]";
    private const string Products = "[dbo].[Products]";
    private const string Stores = "[dbo].[Stores]";

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
    }

    public async Task LoadHierarchyAsync(RetailTree tree, CancellationToken cancellationToken)
    {
        await BulkCopyAsync(Resources, DatasetRows.Columns.Resources, DatasetRows.Hierarchy(tree), orderedById: false, cancellationToken);
        await BulkCopyAsync(Stores, DatasetRows.Columns.Stores, DatasetRows.Stores(tree), orderedById: false, cancellationToken);
    }

    public async Task<LoadTiming> GrowProductsAsync(RetailTree tree, long from, long to, CancellationToken cancellationToken)
    {
        var disabled = await DisableNonclusteredIndexesAsync(cancellationToken);

        var rows = Stopwatch.StartNew();
        await Task.WhenAll(
            Task.Run(() => BulkCopyAsync(Resources, DatasetRows.Columns.Resources, DatasetRows.ProductResources(tree, from, to), orderedById: true, cancellationToken), cancellationToken),
            Task.Run(() => BulkCopyAsync(Products, DatasetRows.Columns.Products, DatasetRows.Products(tree, from, to), orderedById: true, cancellationToken), cancellationToken));
        rows.Stop();
        log.Info($"  rows loaded in {rows.Elapsed.TotalSeconds:F1}s");

        var indexes = Stopwatch.StartNew();
        foreach (var (table, index) in disabled)
        {
            await ExecuteAsync($"ALTER INDEX [{index}] ON {table} REBUILD WITH (SORT_IN_TEMPDB = ON);", cancellationToken);
        }

        indexes.Stop();
        log.Info($"  {disabled.Count} nonclustered indexes rebuilt in {indexes.Elapsed.TotalSeconds:F1}s");

        // Bulk copy skips constraint checks and leaves the foreign keys untrusted; validate them so the
        // database is in the state an application's would be, then refresh statistics like the paper did.
        var maintenance = Stopwatch.StartNew();
        await ExecuteAsync($"ALTER TABLE {Resources} WITH CHECK CHECK CONSTRAINT ALL;", cancellationToken);
        await ExecuteAsync($"UPDATE STATISTICS {Resources}; UPDATE STATISTICS {Products};", cancellationToken);
        maintenance.Stop();
        log.Info($"  constraints validated and statistics updated in {maintenance.Elapsed.TotalSeconds:F1}s");

        return new LoadTiming(rows.Elapsed, indexes.Elapsed, maintenance.Elapsed);
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
        bool orderedById,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, externalTransaction: null)
        {
            DestinationTableName = table,
            BatchSize = 0,
            BulkCopyTimeout = 0,
            EnableStreaming = true,
        };
        foreach (var (name, _) in columns)
        {
            bulk.ColumnMappings.Add(name, name);
        }

        if (orderedById)
        {
            // The rows arrive in clustered-key order; saying so lets SQL Server append without a sort.
            bulk.ColumnOrderHints.Add("Id", Microsoft.Data.SqlClient.SortOrder.Ascending);
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
