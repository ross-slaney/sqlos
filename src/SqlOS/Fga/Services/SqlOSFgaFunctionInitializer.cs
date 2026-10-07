using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.Database;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Services;

/// <summary>
/// Creates the SHRBAC enforcement routines in the database: the ancestor columns of the configured depth,
/// <c>fn_ActiveSubjects</c>, <c>fn_AccessRoots</c>, <c>fn_IsResourceAccessible</c>, the routines and triggers
/// that keep the resource lineage (and the scope column of application tables) exact, and the per-level
/// indexes of those tables; and drops the ones of tables SqlOS no longer maintains. The definitions' hash is
/// stored with the schema version, so a startup that finds the same hash, every object present, and nothing
/// stale changes nothing; a new definition (a new SqlOS version, a changed option, an application table that
/// is new, renamed, or has a new declared index) is applied under an exclusive lock, one batch per transaction.
/// Builds the lineage once when it is empty but the resource tree is not. At every start, fills the scope of
/// the application rows that have none.
/// </summary>
public class SqlOSFgaFunctionInitializer
{
    private const string LockName = "SqlOS:FgaFunctionInitializer";
    private const string LockWaitMessage = "Could not acquire the SqlOS FGA function lock.";
    /// <summary>How long one wait for the initializer lock lasts before the instance logs that it is still waiting.</summary>
    internal static TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(30);
    private const int DeadlockAttempts = 5;
    private readonly ISqlOSFgaDbContext _context;
    private readonly SqlOSFgaOptions _options;
    private readonly ILogger<SqlOSFgaFunctionInitializer> _logger;

    public SqlOSFgaFunctionInitializer(
        ISqlOSFgaDbContext context,
        IOptions<SqlOSFgaOptions> options,
        ILogger<SqlOSFgaFunctionInitializer> logger)
    {
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureFunctionsExistAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Ensuring database functions exist...");
        var provider = SqlOSDatabase.Resolve(_context.Database);
        var modelTables = ScopeTables();

        try
        {
            if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
            {
                // Inside a caller's transaction the caller owns the locking.
                await EnsureAsync(provider, modelTables, cancellationToken);
                _logger.LogInformation("Database functions verified.");
                return;
            }

            await _context.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                // Checked and applied under the lock, inside the retry: the routines' DDL takes
                // schema-modification locks that can deadlock with queries running beside the initializer (an
                // initializer's own check among them); the engine then picks a victim. The session asks to be it
                // (so a query never is) and tries again.
                for (var attempt = 1; ; attempt++)
                {
                    await AcquireLockAsync(provider, cancellationToken);
                    try
                    {
                        await EnsureAsync(provider, modelTables, cancellationToken);
                        break;
                    }
                    catch (Exception ex) when (attempt < DeadlockAttempts && SqlOSDatabaseErrors.IsDeadlock(ex))
                    {
                        _logger.LogWarning(ex, "The SqlOS FGA function initializer lost a deadlock (attempt {Attempt} of {Attempts}); retrying.", attempt, DeadlockAttempts);
                    }
                    finally
                    {
                        await provider.ReleaseSessionLockAsync(_context.Database, LockName, cancellationToken);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                }
            }
            finally
            {
                await _context.Database.CloseConnectionAsync();
            }

            _logger.LogInformation("Database functions verified.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create or update the SqlOS FGA functions. Authorization queries may fail.");
            throw;
        }
    }

    /// <summary>
    /// Waits for the initializer lock as long as another instance holds it: after an upgrade or a model change it
    /// may be building indexes or the lineage on a large database for minutes. The host's shutdown token ends
    /// the wait.
    /// </summary>
    private async Task AcquireLockAsync(ISqlOSDatabaseProvider provider, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await provider.AcquireSessionLockAsync(_context.Database, LockName, LockWait, LockWaitMessage, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex.Message.Contains(LockWaitMessage, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Another instance is setting up the SqlOS FGA routines; this instance waits for it to finish. "
                    + "After an upgrade or a model change it can take minutes on a large database.");
            }
        }
    }

    /// <summary>
    /// Brings the database in line with the definitions, then fills the scope of rows that have none. Runs
    /// under the initializer lock.
    /// </summary>
    private async Task EnsureAsync(ISqlOSDatabaseProvider provider, IReadOnlyList<SqlOSFgaScopeTable> modelTables, CancellationToken cancellationToken)
    {
        // The protected tables whose migration is applied. A table without its scope column (the application runs
        // SqlOS's bootstrap before its own migrations, as an application with foreign keys to SqlOS's tables must,
        // or has not added the migration yet) is left for the next start, which finds it ready.
        var tables = new List<SqlOSFgaScopeTable>();
        foreach (var table in modelTables)
        {
            if (Convert.ToInt32(await ExecuteScalarAsync(provider.BuildScopeTableReadySql(table), null, cancellationToken), CultureInfo.InvariantCulture) == 1)
            {
                tables.Add(table);
            }
            else
            {
                _logger.LogWarning(
                    "{Table} holds an entity with a resource id but has no {Column} column yet. SqlOS adds its triggers and indexes "
                    + "at the next start after the migration that adds the column is applied; until then list filters on it fail.",
                    table.Schema is null ? table.Table : $"{table.Schema}.{table.Table}",
                    SqlOSFgaLineage.ScopeColumn);
            }
        }

        // Dependencies first: the ancestor columns, then fn_ActiveSubjects, then the routines built on them.
        // Each batch is idempotent (CREATE OR ALTER / CREATE OR REPLACE) and runs in its own transaction, so no
        // batch holds a schema lock on one object while waiting for another; readers cannot deadlock with the
        // initializer. Stale objects go before the tables' indexes are ensured.
        var batches = new List<string>();
        batches.AddRange(provider.BuildEnsureLineageColumnsSql(_options));
        batches.Add(provider.BuildActiveSubjectsFunctionSql(_options));
        batches.Add(provider.BuildAccessRootsFunctionSql(_options));
        batches.Add(provider.BuildIsResourceAccessibleFunctionSql(_options));
        // The page index (grant counts, direct indexes, grants triggers) before the lineage maintenance: the
        // scope triggers and the rebuild refer to its tables and routines.
        batches.AddRange(provider.BuildPageIndexSql(_options, tables));
        batches.AddRange(provider.BuildLineageMaintenanceSql(_options, tables));
        batches.Add(provider.BuildScopeCleanupSql(_options, tables));
        batches.AddRange(provider.BuildEnsureScopeIndexesSql(_options, tables));
        var hash = Hash(batches);

        if (!await IsCurrentAsync(provider, tables, hash, cancellationToken))
        {
            await ApplyAsync(provider, batches, hash, cancellationToken);
        }

        // Every start: the rows written while SqlOS's triggers were absent (a bulk load that skipped them, rows
        // written before a table had them) get their scope. An index seek when there are none.
        await ExecuteNonQueryAsync(provider.BuildScopeFillSql(_options), cancellationToken);
    }

    /// <summary>The application tables of the context's model that carry the scope column.</summary>
    private IReadOnlyList<SqlOSFgaScopeTable> ScopeTables()
        => _context is DbContext db ? SqlOSFgaScopeColumns.Tables(db.Model) : [];

    /// <summary>
    /// The stored hash matches these definitions, every object they create exists and none is stale, and the
    /// lineage is built.
    /// </summary>
    private async Task<bool> IsCurrentAsync(ISqlOSDatabaseProvider provider, IReadOnlyList<SqlOSFgaScopeTable> scopeTables, string hash, CancellationToken cancellationToken)
    {
        var stored = await ExecuteScalarAsync(provider.BuildSelectRoutinesHashSql(_options, scopeTables), null, cancellationToken) as string;
        if (!string.Equals(stored, hash, StringComparison.Ordinal))
        {
            return false;
        }

        return !await LineageNeedsBuildAsync(provider, cancellationToken);
    }

    private async Task ApplyAsync(
        ISqlOSDatabaseProvider provider,
        IReadOnlyList<string> batches,
        string hash,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Creating or updating the FGA routines, the resource lineage columns, and the lineage triggers...");
        foreach (var batch in batches)
        {
            await _context.Database.ExecuteSqlRawAsync(batch, cancellationToken);
        }

        _logger.LogInformation("The SqlOS FGA routines, triggers, and indexes are ready.");

        if (await LineageNeedsBuildAsync(provider, cancellationToken))
        {
            // The rebuild fills the scope columns of every application table as well, and the page index.
            await BuildLineageAsync(provider, cancellationToken);
        }
        else
        {
            // A new definition (a new table, order or version) rebuilds the grant counts and the direct
            // indexes from the grants and the rows: seconds, proportional to the grants.
            _logger.LogInformation("Rebuilding the FGA grant counts and direct indexes...");
            await ExecuteNonQueryAsync(provider.BuildPageIndexRebuildSql(_options), cancellationToken);
        }

        // Stored last: a rebuild that fails part-way (its ranges commit one by one) leaves the hash behind, so
        // the next startup applies the definitions and builds again.
        await _context.Database.ExecuteSqlRawAsync(
            provider.BuildStoreRoutinesHashSql(_options),
            [provider.CreateParameter("@RoutinesHash", hash)],
            cancellationToken);
    }

    /// <summary>
    /// The first startup after the lineage migration finds an existing tree whose roots have no depth. Build
    /// it once; from then on the triggers keep it current.
    /// </summary>
    private async Task<bool> LineageNeedsBuildAsync(ISqlOSDatabaseProvider provider, CancellationToken cancellationToken)
    {
        var value = await ExecuteScalarAsync(provider.BuildLineageNeedsBuildSql(_options), null, cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    private async Task BuildLineageAsync(ISqlOSDatabaseProvider provider, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Building the FGA resource lineage for the existing resource tree...");
        await ExecuteNonQueryAsync(provider.BuildLineageRebuildSql(_options), cancellationToken);
        _logger.LogInformation("FGA resource lineage built.");
    }

    private async Task<object?> ExecuteScalarAsync(string sql, DbParameter? parameter, CancellationToken cancellationToken)
    {
        var (connection, wasOpen) = await OpenAsync(cancellationToken);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (parameter is not null)
            {
                command.Parameters.Add(parameter);
            }

            Enlist(command);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is DBNull ? null : value;
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        var (connection, wasOpen) = await OpenAsync(cancellationToken);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 0;
            Enlist(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<(DbConnection Connection, bool WasOpen)> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync(cancellationToken);
        }

        return (connection, wasOpen);
    }

    private void Enlist(DbCommand command)
    {
        if (_context.Database.CurrentTransaction != null)
        {
            command.Transaction = _context.Database.CurrentTransaction.GetDbTransaction();
        }
    }

    /// <summary>SHA-256 of the definitions, so any change to a routine or to the options it embeds re-applies them.</summary>
    internal static string Hash(IEnumerable<string> batches)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n--\n", batches)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string BuildIsResourceAccessibleFunctionSql(SqlOSFgaOptions options)
        => SqlServerDatabaseProvider.Instance.BuildIsResourceAccessibleFunctionSql(options);

    internal static string BuildActiveSubjectsFunctionSql(SqlOSFgaOptions options)
        => SqlServerDatabaseProvider.Instance.BuildActiveSubjectsFunctionSql(options);

    internal static string BuildAccessRootsFunctionSql(SqlOSFgaOptions options)
        => SqlServerDatabaseProvider.Instance.BuildAccessRootsFunctionSql(options);
}
