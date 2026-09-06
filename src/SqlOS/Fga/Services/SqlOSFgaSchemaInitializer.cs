using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.Database;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Services;

public class SqlOSFgaSchemaInitializer
{
    private readonly ISqlOSFgaDbContext _context;
    private readonly SqlOSFgaOptions _options;
    private readonly ILogger<SqlOSFgaSchemaInitializer> _logger;

    public SqlOSFgaSchemaInitializer(
        ISqlOSFgaDbContext context,
        IOptions<SqlOSFgaOptions> options,
        ILogger<SqlOSFgaSchemaInitializer> logger)
    {
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Checking SqlOSFga schema version...");

        var provider = SqlOSDatabase.Resolve(_context.Database);
        var schema = _options.Schema;
        var assembly = typeof(SqlOSFgaSchemaInitializer).Assembly;
        var migrations = SqlOSMigrationManifest.Discover(assembly, provider.FgaMigrationResourcePrefix);
        if (migrations.Count == 0)
        {
            _logger.LogWarning("No migration scripts found.");
            return;
        }

        var maxVersion = migrations.Max(m => m.Version);
        _logger.LogDebug("Found {Count} migration scripts (max version: {MaxVersion})", migrations.Count, maxVersion);

        await _context.Database.ExecuteSqlRawAsync(provider.BuildEnsureFgaVersionTableSql(schema), cancellationToken);

        var currentVersion = await GetCurrentVersionAsync(provider, schema, cancellationToken);

        if (currentVersion == null)
        {
            _logger.LogInformation("Fresh install detected. Running all migrations (v1 -> v{MaxVersion})...", maxVersion);
            foreach (var migration in migrations.OrderBy(m => m.Version))
            {
                await RunMigrationAsync(provider, schema, migration, cancellationToken);
            }
            _logger.LogInformation("Schema v{Version} installed successfully.", maxVersion);
        }
        else if (currentVersion < maxVersion)
        {
            _logger.LogInformation("Schema upgrade needed: v{Current} -> v{Target}", currentVersion, maxVersion);
            var pendingMigrations = migrations.Where(m => m.Version > currentVersion).OrderBy(m => m.Version);
            foreach (var migration in pendingMigrations)
            {
                await RunMigrationAsync(provider, schema, migration, cancellationToken);
            }
            _logger.LogInformation("Schema upgraded to v{Version}.", maxVersion);
        }
        else
        {
            _logger.LogInformation("Schema is up to date (v{Version}).", currentVersion);
        }
    }

    private async Task RunMigrationAsync(
        ISqlOSDatabaseProvider provider,
        string schema,
        SqlOSMigrationManifest.Script migration,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Running migration {Version}: {Name}", migration.Version, migration.Name);
        await RunScriptAsync(provider, migration.ResourceName, cancellationToken);
        await EnsurePersistedVersionAsync(provider, schema, migration.Version, cancellationToken);
    }

    private async Task EnsurePersistedVersionAsync(
        ISqlOSDatabaseProvider provider,
        string schema,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var persistedVersion = await GetCurrentVersionAsync(provider, schema, cancellationToken);
        if (persistedVersion != expectedVersion)
        {
            throw new InvalidOperationException(
                $"SqlOSFga migration v{expectedVersion} completed, " +
                $"but the persisted schema version is {persistedVersion?.ToString() ?? "missing"}.");
        }
    }

    private async Task<int?> GetCurrentVersionAsync(
        ISqlOSDatabaseProvider provider,
        string schema,
        CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
            await connection.OpenAsync(cancellationToken);

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = provider.BuildSelectFgaVersionSql(schema);
            if (_context.Database.CurrentTransaction != null)
                cmd.Transaction = _context.Database.CurrentTransaction.GetDbTransaction();

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            if (result != null && result != DBNull.Value)
                return Convert.ToInt32(result);
            return null;
        }
        finally
        {
            if (!wasOpen)
                await connection.CloseAsync();
        }
    }

    private async Task RunScriptAsync(
        ISqlOSDatabaseProvider provider,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(SqlOSFgaSchemaInitializer).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

        using var reader = new StreamReader(stream);
        var rawSql = await reader.ReadToEndAsync(cancellationToken);
        var sql = SubstitutePlaceholders(rawSql);

        var batches = provider.SplitBatches(sql);
        _logger.LogDebug("Executing {Count} SQL batch(es) from {Resource}...", batches.Count, resourceName);

        foreach (var batch in batches)
        {
            await _context.Database.ExecuteSqlRawAsync(batch, cancellationToken);
        }
    }

    private string SubstitutePlaceholders(string sql)
    {
        var tables = _options.TableNames;

        return sql
            .Replace("{Schema}", _options.Schema)
            .Replace("{SubjectTypes}", tables.SubjectTypes)
            .Replace("{Subjects}", tables.Subjects)
            .Replace("{UserGroups}", tables.UserGroups)
            .Replace("{UserGroupMemberships}", tables.UserGroupMemberships)
            .Replace("{ResourceTypes}", tables.ResourceTypes)
            .Replace("{Resources}", tables.Resources)
            .Replace("{Grants}", tables.Grants)
            .Replace("{Roles}", tables.Roles)
            .Replace("{Permissions}", tables.Permissions)
            .Replace("{RolePermissions}", tables.RolePermissions)
            .Replace("{ServiceAccounts}", tables.ServiceAccounts)
            .Replace("{Users}", tables.Users)
            .Replace("{Agents}", tables.Agents);
    }
}
