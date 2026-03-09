using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;

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
        var migrations = DiscoverMigrations();
        if (migrations.Count == 0)
        {
            _logger.LogWarning("No SqlOS migration scripts found.");
            return;
        }

        var schema = _options.Schema;
        var ensureVersionTableSql = $@"
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{schema}')
BEGIN
    EXEC('CREATE SCHEMA [{schema}]');
END
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSSchema' AND schema_id = SCHEMA_ID('{schema}'))
BEGIN
    CREATE TABLE [{schema}].[SqlOSSchema] ([Version] INT NOT NULL);
END";
        await _context.Database.ExecuteSqlRawAsync(ensureVersionTableSql, cancellationToken);

        var currentVersion = await GetCurrentVersionAsync(schema, cancellationToken);
        var targetVersion = migrations.Max(x => x.Version);
        if (currentVersion >= targetVersion)
        {
            _logger.LogInformation("SqlOS schema is up to date at version {Version}.", currentVersion);
            return;
        }

        foreach (var migration in migrations.Where(x => x.Version > currentVersion).OrderBy(x => x.Version))
        {
            _logger.LogInformation("Running SqlOS schema migration {Version}: {Name}", migration.Version, migration.Name);
            await RunScriptAsync(migration.ResourceName, cancellationToken);
        }
    }

    private List<MigrationScript> DiscoverMigrations()
    {
        var assembly = typeof(SqlOSSchemaInitializer).Assembly;
        var pattern = new Regex(@"^SqlOS\.AuthServer\.Schema\.(\d+)_(.+)\.sql$", RegexOptions.IgnoreCase);
        var migrations = new List<MigrationScript>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            var match = pattern.Match(name);
            if (!match.Success)
            {
                continue;
            }

            migrations.Add(new MigrationScript(int.Parse(match.Groups[1].Value), match.Groups[2].Value, name));
        }

        return migrations;
    }

    private async Task<int> GetCurrentVersionAsync(string schema, CancellationToken cancellationToken)
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
            cmd.CommandText = $"SELECT TOP 1 [Version] FROM [{schema}].[SqlOSSchema]";
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

    private async Task RunScriptAsync(string resourceName, CancellationToken cancellationToken)
    {
        var assembly = typeof(SqlOSSchemaInitializer).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        var rawSql = await reader.ReadToEndAsync(cancellationToken);
        var sql = rawSql.Replace("{Schema}", _options.Schema);

        var batches = Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(x => !string.IsNullOrWhiteSpace(x));

        foreach (var batch in batches)
        {
            await _context.Database.ExecuteSqlRawAsync(batch, cancellationToken);
        }
    }

    private sealed record MigrationScript(int Version, string Name, string ResourceName);
}
