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
/// Creates the SHRBAC enforcement routines in the database: the ancestor columns of the configured depth
/// and the index of each level, <c>fn_ActiveSubjects</c>, <c>fn_AccessRoots</c>, <c>fn_ListVisible</c>,
/// <c>fn_VisibleSet</c>, <c>fn_ListFirst</c>, <c>fn_IsResourceAccessible</c>, and the routines and triggers
/// that keep the resource lineage exact. The
/// definitions' hash is stored with the schema version, so a startup that finds the same hash and every
/// object present changes nothing; a new definition (a new SqlOS version, a changed option) is applied under
/// an exclusive lock, one batch per transaction. Builds the lineage once when it is empty but the resource
/// tree is not.
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

        try
        {
            if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
            {
                // Inside a caller's transaction the caller owns the locking.
                await EnsureAsync(provider, cancellationToken);
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
                        await EnsureAsync(provider, cancellationToken);
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
    /// Waits for the initializer lock as long as another instance holds it: after an upgrade it may be
    /// building indexes or the lineage on a large database for minutes. The host's shutdown token ends the wait.
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
                    + "After an upgrade it can take minutes on a large database.");
            }
        }
    }

    /// <summary>Brings the database in line with the definitions. Runs under the initializer lock.</summary>
    private async Task EnsureAsync(ISqlOSDatabaseProvider provider, CancellationToken cancellationToken)
    {
        // Dependencies first: the ancestor columns and their indexes, then fn_ActiveSubjects, then the routines
        // built on them. Each batch is idempotent (CREATE OR ALTER / CREATE OR REPLACE) and runs in its own
        // transaction, so no batch holds a schema lock on one object while waiting for another; readers cannot
        // deadlock with the initializer.
        var batches = new List<string>();
        batches.AddRange(provider.BuildEnsureLineageColumnsSql(_options));
        batches.Add(provider.BuildActiveSubjectsFunctionSql(_options));
        batches.Add(provider.BuildAccessRootsFunctionSql(_options));
        batches.Add(provider.BuildListVisibleFunctionSql(_options));
        batches.Add(provider.BuildVisibleSetFunctionSql(_options));
        batches.Add(provider.BuildListFirstFunctionSql(_options));
        batches.Add(provider.BuildIsResourceAccessibleFunctionSql(_options));
        batches.AddRange(provider.BuildLineageMaintenanceSql(_options));
        var hash = Hash(batches);

        if (!await IsCurrentAsync(provider, hash, cancellationToken))
        {
            await ApplyAsync(provider, batches, hash, cancellationToken);
        }
    }

    /// <summary>The stored hash matches these definitions, every object they create exists, and the lineage is built.</summary>
    private async Task<bool> IsCurrentAsync(ISqlOSDatabaseProvider provider, string hash, CancellationToken cancellationToken)
    {
        var stored = await ExecuteScalarAsync(provider.BuildSelectRoutinesHashSql(_options), cancellationToken) as string;
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
        _logger.LogDebug("Creating or updating the FGA routines, the resource lineage columns and indexes, and the lineage triggers...");
        foreach (var batch in batches)
        {
            // Without a timeout: an index over a large resources table takes minutes.
            await ExecuteNonQueryAsync(batch, cancellationToken);
        }

        _logger.LogInformation("The SqlOS FGA routines, triggers, and indexes are ready.");

        if (await LineageNeedsBuildAsync(provider, cancellationToken))
        {
            await BuildLineageAsync(provider, cancellationToken);
        }

        // Stored last: a rebuild that fails part-way (its ranges commit one by one) leaves the hash behind, so
        // the next startup applies the definitions and builds again.
        await _context.Database.ExecuteSqlRawAsync(
            provider.BuildStoreRoutinesHashSql(_options),
            [provider.CreateParameter("@RoutinesHash", hash)],
            cancellationToken);
    }

    /// <summary>
    /// The lineage has not been built in full: never built, or a rebuild stopped part-way (it clears
    /// <c>LineageBuilt</c> before its first range and sets it after its last). Build it; from then on the
    /// triggers keep it current.
    /// </summary>
    private async Task<bool> LineageNeedsBuildAsync(ISqlOSDatabaseProvider provider, CancellationToken cancellationToken)
    {
        var value = await ExecuteScalarAsync(provider.BuildLineageNeedsBuildSql(_options), cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    private async Task BuildLineageAsync(ISqlOSDatabaseProvider provider, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Building the FGA resource lineage for the existing resource tree...");
        await ExecuteNonQueryAsync(provider.BuildLineageRebuildSql(_options), cancellationToken);
        _logger.LogInformation("FGA resource lineage built.");
    }

    private async Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken)
    {
        var (connection, wasOpen) = await OpenAsync(cancellationToken);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
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

    internal static string BuildListVisibleFunctionSql(SqlOSFgaOptions options)
        => SqlServerDatabaseProvider.Instance.BuildListVisibleFunctionSql(options);

    internal static string BuildListFirstFunctionSql(SqlOSFgaOptions options)
        => SqlServerDatabaseProvider.Instance.BuildListFirstFunctionSql(options);
}
