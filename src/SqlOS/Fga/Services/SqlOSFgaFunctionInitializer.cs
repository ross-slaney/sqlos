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
/// Creates the SHRBAC enforcement routines in the database: <c>fn_ActiveSubjects</c>, <c>fn_AccessRoots</c>,
/// <c>fn_IsResourceAccessible</c>, and the routines and triggers that maintain the resource closure. The
/// definitions' hash is stored with the schema version, so a startup that finds the same hash and every
/// routine present changes nothing; a new definition (a new SqlOS version, a changed option) is applied under
/// an exclusive lock, one batch per transaction. Builds the closure once when it is empty but the resource tree
/// is not.
/// </summary>
public class SqlOSFgaFunctionInitializer
{
    private const string LockName = "SqlOS:FgaFunctionInitializer";
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

        // fn_ActiveSubjects first (both functions reference it), then fn_AccessRoots (the page references it).
        // Each batch is
        // idempotent (CREATE OR ALTER / CREATE OR REPLACE) and runs in its own transaction, so no batch holds a
        // schema lock on one object while waiting for another; readers cannot deadlock with the initializer.
        var batches = new List<string>
        {
            provider.BuildActiveSubjectsFunctionSql(_options),
            provider.BuildAccessRootsFunctionSql(_options),
            provider.BuildIsResourceAccessibleFunctionSql(_options),
        };
        batches.AddRange(provider.BuildResourceClosureMaintenanceSql(_options));
        var hash = Hash(batches);

        try
        {
            if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
            {
                // Inside a caller's transaction the caller owns the locking.
                await ApplyAsync(provider, batches, hash, cancellationToken);
                _logger.LogInformation("Database functions verified.");
                return;
            }

            await _context.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                if (await IsCurrentAsync(provider, hash, cancellationToken))
                {
                    _logger.LogDebug("fn_ActiveSubjects, fn_AccessRoots, fn_IsResourceAccessible, and the resource closure maintenance are current.");
                    _logger.LogInformation("Database functions verified.");
                    return;
                }

                await provider.AcquireSessionLockAsync(
                    _context.Database,
                    LockName,
                    TimeSpan.FromSeconds(30),
                    "Could not acquire the SqlOS FGA function lock.",
                    cancellationToken);
                try
                {
                    // Another process may have applied the same definitions while this one waited.
                    if (!await IsCurrentAsync(provider, hash, cancellationToken))
                    {
                        await ApplyAsync(provider, batches, hash, cancellationToken);
                    }
                }
                finally
                {
                    await provider.ReleaseSessionLockAsync(_context.Database, LockName, cancellationToken);
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

    /// <summary>The stored hash matches these definitions, every routine exists, and the closure is built.</summary>
    private async Task<bool> IsCurrentAsync(ISqlOSDatabaseProvider provider, string hash, CancellationToken cancellationToken)
    {
        var stored = await ExecuteScalarAsync(provider.BuildSelectRoutinesHashSql(_options), null, cancellationToken) as string;
        if (!string.Equals(stored, hash, StringComparison.Ordinal))
        {
            return false;
        }

        return !await ClosureNeedsBuildAsync(provider, cancellationToken);
    }

    private async Task ApplyAsync(ISqlOSDatabaseProvider provider, IReadOnlyList<string> batches, string hash, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Creating or updating fn_ActiveSubjects, fn_AccessRoots, fn_IsResourceAccessible, and the resource closure maintenance...");
        foreach (var batch in batches)
        {
            await _context.Database.ExecuteSqlRawAsync(batch, cancellationToken);
        }

        await _context.Database.ExecuteSqlRawAsync(
            provider.BuildStoreRoutinesHashSql(_options),
            [provider.CreateParameter("@RoutinesHash", hash)],
            cancellationToken);
        _logger.LogInformation("fn_IsResourceAccessible TVF is ready.");

        if (await ClosureNeedsBuildAsync(provider, cancellationToken))
        {
            await BuildClosureAsync(provider, cancellationToken);
        }
    }

    /// <summary>
    /// The first startup after the closure migration finds an empty closure beside an existing tree. Fill it
    /// once; from then on the triggers keep it current.
    /// </summary>
    private async Task<bool> ClosureNeedsBuildAsync(ISqlOSDatabaseProvider provider, CancellationToken cancellationToken)
    {
        var value = await ExecuteScalarAsync(provider.BuildResourceClosureNeedsBuildSql(_options), null, cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    private async Task BuildClosureAsync(ISqlOSDatabaseProvider provider, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Building the FGA resource closure for the existing resource tree...");
        await ExecuteNonQueryAsync(provider.BuildResourceClosureRebuildSql(_options), cancellationToken);
        _logger.LogInformation("FGA resource closure built.");
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
