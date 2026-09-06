using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Database;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSSchemaInitializer
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly ILogger<SqlOSSchemaInitializer> _logger;

    public SqlOSSchemaInitializer(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        ILogger<SqlOSSchemaInitializer> logger)
    {
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var provider = SqlOSDatabase.Resolve(_context.Database);
        var assembly = typeof(SqlOSSchemaInitializer).Assembly;
        var migrations = SqlOSMigrationManifest.Discover(assembly, provider.AuthMigrationResourcePrefix);
        if (migrations.Count == 0)
        {
            _logger.LogWarning("No SqlOS migration scripts found.");
            return;
        }

        var schema = _options.Schema;
        await _context.Database.ExecuteSqlRawAsync(provider.BuildEnsureAuthVersionTablesSql(schema), cancellationToken);

        var currentVersion = await GetCurrentVersionAsync(provider, schema, cancellationToken);
        var orderedMigrations = migrations
            .OrderBy(x => x.Version)
            .ThenBy(x => x.ResourceName, StringComparer.Ordinal)
            .ToList();
        var appliedMigrations = await GetAppliedMigrationsAsync(provider, schema, cancellationToken);
        if (appliedMigrations.Count == 0 && currentVersion > 0)
        {
            // Legacy installations only recorded the latest version. Scripts below that
            // version must have completed. A unique script at the current version also
            // completed, while every member of a duplicate-version group is rerun because
            // the old marker cannot tell which member committed before a crash.
            var currentVersionScriptCount = orderedMigrations.Count(x => x.Version == currentVersion);
            var legacyAppliedMigrations = orderedMigrations.Where(x =>
                    x.Version < currentVersion
                    || (x.Version == currentVersion && currentVersionScriptCount == 1))
                .ToList();
            await ExecuteInTransactionAsync(async () =>
            {
                foreach (var migration in legacyAppliedMigrations)
                {
                    await RecordAppliedMigrationAsync(provider, schema, migration, cancellationToken);
                }
            }, cancellationToken);
            foreach (var migration in legacyAppliedMigrations)
            {
                appliedMigrations.Add(migration.ResourceName);
            }
        }

        var pendingMigrations = orderedMigrations
            .Where(x => !appliedMigrations.Contains(x.ResourceName))
            .ToList();
        if (pendingMigrations.Count == 0)
        {
            _logger.LogInformation("SqlOS schema is up to date at version {Version}.", currentVersion);
            return;
        }

        foreach (var migration in pendingMigrations)
        {
            _logger.LogInformation("Running SqlOS schema migration {Version}: {Name}", migration.Version, migration.Name);
            await ExecuteInTransactionAsync(async () =>
            {
                await RunScriptAsync(provider, migration.ResourceName, cancellationToken);
                await RecordAppliedMigrationAsync(provider, schema, migration, cancellationToken);
            }, cancellationToken);
        }

        var targetVersion = orderedMigrations.Max(x => x.Version);
        await _context.Database.ExecuteSqlRawAsync(
            provider.BuildUpdateVersionSql(schema),
            [provider.CreateParameter("@targetVersion", targetVersion)],
            cancellationToken);
    }

    private async Task<int> GetCurrentVersionAsync(
        ISqlOSDatabaseProvider provider,
        string schema,
        CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = provider.BuildSelectVersionSql(schema);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<HashSet<string>> GetAppliedMigrationsAsync(
        ISqlOSDatabaseProvider provider,
        string schema,
        CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = provider.BuildSelectAppliedMigrationsSql(schema);
            var result = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private Task RecordAppliedMigrationAsync(
        ISqlOSDatabaseProvider provider,
        string schema,
        SqlOSMigrationManifest.Script migration,
        CancellationToken cancellationToken)
        => _context.Database.ExecuteSqlRawAsync(
            provider.BuildRecordAppliedMigrationSql(schema),
            [
                provider.CreateParameter("@scriptName", migration.ResourceName),
                provider.CreateParameter("@version", migration.Version)
            ],
            cancellationToken);

    private async Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task RunScriptAsync(
        ISqlOSDatabaseProvider provider,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(SqlOSSchemaInitializer).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        var rawSql = await reader.ReadToEndAsync(cancellationToken);
        var sql = rawSql.Replace("{Schema}", _options.Schema);

        foreach (var batch in provider.SplitBatches(sql))
        {
            await _context.Database.ExecuteSqlRawAsync(batch, cancellationToken);
        }
    }
}
