using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
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
END
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSAppliedMigrations' AND schema_id = SCHEMA_ID('{schema}'))
BEGIN
    CREATE TABLE [{schema}].[SqlOSAppliedMigrations] (
        [Sequence] BIGINT IDENTITY(1,1) NOT NULL,
        [ScriptName] NVARCHAR(450) NOT NULL,
        [Version] INT NOT NULL,
        [AppliedAt] DATETIME2 NOT NULL,
        CONSTRAINT [PK_SqlOSAppliedMigrations] PRIMARY KEY ([ScriptName]),
        CONSTRAINT [UX_SqlOSAppliedMigrations_Sequence] UNIQUE ([Sequence])
    );
END";
        await _context.Database.ExecuteSqlRawAsync(ensureVersionTableSql, cancellationToken);

        var currentVersion = await GetCurrentVersionAsync(schema, cancellationToken);
        var orderedMigrations = migrations
            .OrderBy(x => x.Version)
            .ThenBy(x => x.ResourceName, StringComparer.Ordinal)
            .ToList();
        var appliedMigrations = await GetAppliedMigrationsAsync(schema, cancellationToken);
        if (appliedMigrations.Count == 0 && currentVersion > 0)
        {
            // Legacy installations only recorded the latest version. Scripts below that
            // version must have completed. A unique script at the current version also
            // completed, while every member of a duplicate-version group is rerun because
            // the old marker cannot tell which member committed before a crash.
            var currentVersionScriptCount = orderedMigrations.Count(x => x.Version == currentVersion);
            foreach (var migration in orderedMigrations.Where(x =>
                         x.Version < currentVersion
                         || (x.Version == currentVersion && currentVersionScriptCount == 1)))
            {
                await RecordAppliedMigrationAsync(schema, migration, cancellationToken);
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
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await RunScriptAsync(migration.ResourceName, cancellationToken);
            await RecordAppliedMigrationAsync(schema, migration, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var targetVersion = orderedMigrations.Max(x => x.Version);
        var updateVersionSql = $"UPDATE [{schema}].[SqlOSSchema] SET [Version] = @targetVersion";
        await _context.Database.ExecuteSqlRawAsync(updateVersionSql, [new SqlParameter("@targetVersion", targetVersion)], cancellationToken);
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

    private async Task<HashSet<string>> GetAppliedMigrationsAsync(string schema, CancellationToken cancellationToken)
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
            cmd.CommandText = $"SELECT [ScriptName] FROM [{schema}].[SqlOSAppliedMigrations]";
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

    private Task RecordAppliedMigrationAsync(string schema, MigrationScript migration, CancellationToken cancellationToken)
    {
        var sql = $@"
IF NOT EXISTS (SELECT 1 FROM [{schema}].[SqlOSAppliedMigrations] WHERE [ScriptName] = @scriptName)
BEGIN
    INSERT INTO [{schema}].[SqlOSAppliedMigrations] ([ScriptName], [Version], [AppliedAt])
    VALUES (@scriptName, @version, SYSUTCDATETIME());
END";
        return _context.Database.ExecuteSqlRawAsync(sql,
            [new SqlParameter("@scriptName", migration.ResourceName), new SqlParameter("@version", migration.Version)],
            cancellationToken);
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
