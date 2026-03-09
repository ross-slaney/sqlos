using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

        var schema = _options.Schema;

        // Discover all migration scripts (NNN_Name.sql pattern)
        var migrations = DiscoverMigrations();
        if (migrations.Count == 0)
        {
            _logger.LogWarning("No migration scripts found.");
            return;
        }

        var maxVersion = migrations.Max(m => m.Version);
        _logger.LogDebug("Found {Count} migration scripts (max version: {MaxVersion})", migrations.Count, maxVersion);

        // Ensure the version tracking table exists
        var ensureVersionTableSql = $@"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSFgaSchema' AND schema_id = SCHEMA_ID('{schema}'))
BEGIN
    CREATE TABLE [{schema}].[SqlOSFgaSchema] ([Version] INT NOT NULL);
END";
        await _context.Database.ExecuteSqlRawAsync(ensureVersionTableSql, cancellationToken);

        // Read the current version
        var currentVersion = await GetCurrentVersionAsync(schema, cancellationToken);

        if (currentVersion == null)
        {
            _logger.LogInformation("Fresh install detected. Running all migrations (v1 -> v{MaxVersion})...", maxVersion);
            foreach (var migration in migrations.OrderBy(m => m.Version))
            {
                _logger.LogDebug("Running migration {Version}: {Name}", migration.Version, migration.Name);
                await RunScriptAsync(migration.ResourceName, cancellationToken);
            }
            _logger.LogInformation("Schema v{Version} installed successfully.", maxVersion);
        }
        else if (currentVersion < maxVersion)
        {
            _logger.LogInformation("Schema upgrade needed: v{Current} -> v{Target}", currentVersion, maxVersion);
            var pendingMigrations = migrations.Where(m => m.Version > currentVersion).OrderBy(m => m.Version);
            foreach (var migration in pendingMigrations)
            {
                _logger.LogDebug("Running migration {Version}: {Name}", migration.Version, migration.Name);
                await RunScriptAsync(migration.ResourceName, cancellationToken);
            }
            _logger.LogInformation("Schema upgraded to v{Version}.", maxVersion);
        }
        else
        {
            _logger.LogInformation("Schema is up to date (v{Version}).", currentVersion);
        }
    }

    private List<MigrationScript> DiscoverMigrations()
    {
        var assembly = typeof(SqlOSFgaSchemaInitializer).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();
        var migrations = new List<MigrationScript>();

        // Pattern: SqlOS.Fga.Schema.NNN_Name.sql where NNN is a number
        var pattern = new Regex(@"^SqlOS\.Fga\.Schema\.(\d+)_(.+)\.sql$", RegexOptions.IgnoreCase);

        foreach (var resourceName in resourceNames)
        {
            var match = pattern.Match(resourceName);
            if (match.Success)
            {
                var version = int.Parse(match.Groups[1].Value);
                var name = match.Groups[2].Value;
                migrations.Add(new MigrationScript(version, name, resourceName));
            }
        }

        return migrations;
    }

    private async Task<int?> GetCurrentVersionAsync(string schema, CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
            await connection.OpenAsync(cancellationToken);

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT TOP 1 [Version] FROM [{schema}].[SqlOSFgaSchema]";
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

    private record MigrationScript(int Version, string Name, string ResourceName);

    private async Task RunScriptAsync(string resourceName, CancellationToken cancellationToken)
    {
        var assembly = typeof(SqlOSFgaSchemaInitializer).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

        using var reader = new StreamReader(stream);
        var rawSql = await reader.ReadToEndAsync(cancellationToken);

        // Replace placeholders with configured values
        var sql = SubstitutePlaceholders(rawSql);

        // Split on GO batches (GO on its own line)
        var batches = Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToArray();

        _logger.LogDebug("Executing {Count} SQL batch(es) from {Resource}...", batches.Length, resourceName);

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
