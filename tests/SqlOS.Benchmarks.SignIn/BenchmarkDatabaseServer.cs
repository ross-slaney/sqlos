using System.Data.Common;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SqlOS.BehaviorLock.Host;

namespace SqlOS.Benchmarks.SignIn;

/// <summary>
/// The database server a run measures against. By default it is started with the repository's
/// own mechanism, the <c>tests/SqlOS.IntegrationTests.AppHost</c> Aspire app that the behavior lock
/// and the integration tests use (SQL Server, or PostgreSQL when the provider says so); a
/// connection string from <c>--server</c> replaces it. Either way the run gets a database of its
/// own and drops it at the end.
/// </summary>
internal sealed class BenchmarkDatabaseServer : IAsyncDisposable
{
    private const string AppHostResource = "sql";

    private readonly DistributedApplication? _app;
    private readonly string _serverConnectionString;

    private BenchmarkDatabaseServer(DatabaseProvider provider, string serverConnectionString, string source, DistributedApplication? app)
    {
        Provider = provider;
        _serverConnectionString = serverConnectionString;
        Source = source;
        _app = app;
    }

    public DatabaseProvider Provider { get; }

    /// <summary>Where the server came from: the Aspire container image, or the <c>--server</c> option.</summary>
    public string Source { get; }

    public static async Task<BenchmarkDatabaseServer> StartAsync(DatabaseProvider provider, string? server, CancellationToken cancellationToken)
    {
        if (provider == DatabaseProvider.PostgreSql)
        {
            // As every host does before Npgsql's first use (UseSqlOS sets it too).
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        }

        if (server != null)
        {
            return new BenchmarkDatabaseServer(provider, server, "--server", app: null);
        }

        // The app host picks its resource from this variable when it builds.
        Environment.SetEnvironmentVariable(
            "SQLOS_TEST_PROVIDER",
            provider == DatabaseProvider.PostgreSql ? "postgresql" : "sqlserver");
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.SqlOS_IntegrationTests_AppHost>(cancellationToken);
        // Keep warnings and errors, but not the container's own log, which Aspire forwards at
        // error level (PostgreSQL writes everything to stderr) and which would print mid-run.
        appHost.Services.AddLogging(logging => logging
            .AddFilter("Aspire", LogLevel.Warning)
            .AddFilter("SqlOS.IntegrationTests.AppHost", LogLevel.Warning)
            .AddFilter("SqlOS.IntegrationTests.AppHost.Resources", LogLevel.None)
            .AddFilter("Microsoft", LogLevel.Warning));
        var image = appHost.Resources
            .Single(resource => resource.Name == AppHostResource)
            .Annotations.OfType<ContainerImageAnnotation>()
            .Select(annotation => $"{annotation.Registry}/{annotation.Image}:{annotation.Tag}")
            .FirstOrDefault() ?? "unknown image";
        var app = await appHost.BuildAsync(cancellationToken);
        try
        {
            await app.StartAsync(cancellationToken);
            using var healthy = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            healthy.CancelAfter(TimeSpan.FromMinutes(5));
            await app.ResourceNotifications.WaitForResourceHealthyAsync(AppHostResource, healthy.Token);
            var connectionString = await app.GetConnectionStringAsync("sqlos-test", cancellationToken)
                ?? throw new InvalidOperationException("Aspire did not provide the sqlos-test connection string.");
            return new BenchmarkDatabaseServer(provider, connectionString, $"Aspire container {image}", app);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates an empty database for this run and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"signin_{Guid.NewGuid():N}";
        await ExecuteAdminAsync(
            Provider == DatabaseProvider.PostgreSql ? $"""CREATE DATABASE "{name}";""" : $"CREATE DATABASE [{name}];",
            cancellationToken);
        return WithDatabase(_serverConnectionString, name);
    }

    public async Task DropDatabaseAsync(string connectionString)
    {
        var name = DatabaseName(connectionString);
        if (Provider == DatabaseProvider.PostgreSql)
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync($"""DROP DATABASE IF EXISTS "{name}" WITH (FORCE);""", CancellationToken.None);
            return;
        }

        SqlConnection.ClearAllPools();
        await ExecuteAdminAsync(
            $"""
             IF DB_ID(N'{name}') IS NOT NULL
             BEGIN
                 ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                 DROP DATABASE [{name}];
             END
             """,
            CancellationToken.None);
    }

    /// <summary>The server's own description of itself, for the results.</summary>
    public async Task<string> ReadVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = Open(AdminDatabase());
        await using var command = connection.CreateCommand();
        command.CommandText = Provider == DatabaseProvider.PostgreSql ? "SELECT version();" : "SELECT @@VERSION;";
        var version = (string?)await command.ExecuteScalarAsync(cancellationToken) ?? "unknown";
        // SQL Server's answer spans several lines; the first one names the release and build.
        return version.Split('\n')[0].Trim();
    }

    /// <summary>Opens a connection to the run's database, outside SqlOS.</summary>
    public async Task<DbConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        DbConnection connection = Provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnection(connectionString)
            : new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public string DatabaseName(string connectionString)
        => Provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnectionStringBuilder(connectionString).Database!
            : new SqlConnectionStringBuilder(connectionString).InitialCatalog;

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private async Task ExecuteAdminAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = Open(AdminDatabase());
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string AdminDatabase()
        => WithDatabase(_serverConnectionString, Provider == DatabaseProvider.PostgreSql ? "postgres" : "master");

    private DbConnection Open(string connectionString)
    {
        DbConnection connection = Provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnection(connectionString)
            : new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    private string WithDatabase(string server, string database)
        => Provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString
            : new SqlConnectionStringBuilder(server) { InitialCatalog = database }.ConnectionString;
}
