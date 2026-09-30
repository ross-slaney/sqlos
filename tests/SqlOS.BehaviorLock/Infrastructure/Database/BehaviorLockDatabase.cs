using System.Data.Common;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SqlOS.BehaviorLock.Host;

namespace SqlOS.BehaviorLock.Infrastructure.Database;

/// <summary>
/// Provisions the database server for the whole test run with the repository's existing
/// mechanism: the <c>tests/SqlOS.IntegrationTests.AppHost</c> Aspire app, which starts SQL Server,
/// or PostgreSQL when <c>SQLOS_TEST_PROVIDER</c> is <c>postgresql</c>. Every scenario then gets its
/// own database on that server, so scenarios are isolated and can run in parallel.
/// </summary>
public static class BehaviorLockDatabase
{
    public const string ProviderEnvironmentVariable = "SQLOS_TEST_PROVIDER";

    private static readonly SemaphoreSlim StartGate = new(1, 1);
    private static DistributedApplication? _app;
    private static string? _serverConnectionString;

    public static DatabaseProvider Provider { get; } =
        Environment.GetEnvironmentVariable(ProviderEnvironmentVariable)?.Trim().ToLowerInvariant()
            is "postgresql" or "postgres" or "npgsql"
            ? DatabaseProvider.PostgreSql
            : DatabaseProvider.SqlServer;

    public static string ProviderName => Provider == DatabaseProvider.PostgreSql ? "PostgreSql" : "SqlServer";

    /// <summary>Starts the Aspire-provisioned server on first use and returns its connection string.</summary>
    public static async Task<string> GetServerConnectionStringAsync(CancellationToken cancellationToken = default)
    {
        if (_serverConnectionString != null)
        {
            return _serverConnectionString;
        }

        await StartGate.WaitAsync(cancellationToken);
        try
        {
            if (_serverConnectionString != null)
            {
                return _serverConnectionString;
            }

            if (Provider == DatabaseProvider.PostgreSql)
            {
                AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
            }

            var appHost = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.SqlOS_IntegrationTests_AppHost>(cancellationToken);
            // The container's startup log is noise in every test's output; keep warnings and errors.
            appHost.Services.AddLogging(logging => logging
                .AddFilter("Aspire", LogLevel.Warning)
                .AddFilter("SqlOS.IntegrationTests.AppHost", LogLevel.Warning)
                .AddFilter("Microsoft", LogLevel.Warning));
            _app = await appHost.BuildAsync(cancellationToken);
            await _app.StartAsync(cancellationToken);
            using var healthy = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            healthy.CancelAfter(TimeSpan.FromMinutes(5));
            await _app.ResourceNotifications.WaitForResourceHealthyAsync("sql", healthy.Token);
            _serverConnectionString = await _app.GetConnectionStringAsync("sqlos-test", cancellationToken)
                ?? throw new InvalidOperationException("Aspire did not provide the sqlos-test connection string.");
            return _serverConnectionString;
        }
        finally
        {
            StartGate.Release();
        }
    }

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public static async Task<string> CreateDatabaseAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var server = await GetServerConnectionStringAsync(cancellationToken);
        var safePrefix = new string(prefix.Where(char.IsLetterOrDigit).Take(20).ToArray());
        var name = $"bl_{safePrefix}_{Guid.NewGuid():N}";
        await ExecuteAdminAsync(
            server,
            Provider == DatabaseProvider.PostgreSql
                ? $"""CREATE DATABASE "{name}";"""
                : $"CREATE DATABASE [{name}];",
            cancellationToken);
        return WithDatabase(server, name);
    }

    public static async Task DropDatabaseAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var server = await GetServerConnectionStringAsync(cancellationToken);
        var name = DatabaseName(connectionString);
        if (Provider == DatabaseProvider.PostgreSql)
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(server, $"""DROP DATABASE IF EXISTS "{name}" WITH (FORCE);""", cancellationToken);
            return;
        }

        SqlConnection.ClearAllPools();
        await ExecuteAdminAsync(
            server,
            $"""
             IF DB_ID(N'{name}') IS NOT NULL
             BEGIN
                 ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                 DROP DATABASE [{name}];
             END
             """,
            cancellationToken);
    }

    public static DbConnection OpenConnection(string connectionString)
    {
        DbConnection connection = Provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnection(connectionString)
            : new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    public static string DatabaseName(string connectionString)
        => Provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnectionStringBuilder(connectionString).Database!
            : new SqlConnectionStringBuilder(connectionString).InitialCatalog;

    public static async Task StopAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private static string WithDatabase(string server, string database)
        => Provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString
            : new SqlConnectionStringBuilder(server) { InitialCatalog = database }.ConnectionString;

    private static async Task ExecuteAdminAsync(string server, string sql, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(WithDatabase(server, Provider == DatabaseProvider.PostgreSql ? "postgres" : "master"));
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
