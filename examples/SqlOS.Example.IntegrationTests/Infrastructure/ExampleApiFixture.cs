using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Example.Api.Data;

namespace SqlOS.Example.IntegrationTests.Infrastructure;

[TestClass]
public static class ExampleApiFixture
{
    private static DistributedApplication? _app;
    private static WebApplicationFactory<Program>? _factory;
    private static string _connectionString = string.Empty;

    public static HttpClient Client { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.SqlOS_IntegrationTests_AppHost>();

        _app = await appHost.BuildAsync();
        await _app.StartAsync();

        using var sqlCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("sql", sqlCts.Token);

        var baseConnectionString = await _app.GetConnectionStringAsync("sqlos-test")
            ?? throw new InvalidOperationException("Could not get SQL connection string from Aspire.");
        var databaseName = $"SqlOSExample_{Guid.NewGuid():N}"[..30];
        _connectionString = baseConnectionString.Replace("Database=sqlos-test", $"Database={databaseName}");

        _factory = BuildFactory();

        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await Client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        context.WriteLine($"SqlOS example fixture initialized with DB {databaseName}");
    }

    public static WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder>? configureBuilder = null)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("ExampleApiFixture has not been initialized.");
        }

        return BuildFactory(configureBuilder);
    }

    public static IsolatedExampleApiFactory CreateIsolatedFactory(Action<IWebHostBuilder>? configureBuilder = null)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("ExampleApiFixture has not been initialized.");
        }

        var databaseName = $"SqlOSExample_{Guid.NewGuid():N}"[..30];
        var connectionBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connectionString);
        var masterBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        using (var connection = new Microsoft.Data.SqlClient.SqlConnection(masterBuilder.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}]";
            command.ExecuteNonQuery();
        }
        connectionBuilder.InitialCatalog = databaseName;
        var isolatedConnectionString = connectionBuilder.ConnectionString;
        return new IsolatedExampleApiFactory(BuildFactory(configureBuilder, isolatedConnectionString), isolatedConnectionString);
    }

    private static WebApplicationFactory<Program> BuildFactory(
        Action<IWebHostBuilder>? configureBuilder = null,
        string? connectionString = null)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("environment", "Development");
                builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString ?? _connectionString);
                builder.UseSetting("SqlOS:Issuer", "https://localhost/sqlos/auth");
                builder.UseSetting("SqlOS:Dashboard:AuthMode", "DevelopmentOnly");
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IHttpClientFactory, FakeOidcProviderHttpClientFactory>();
                });
                configureBuilder?.Invoke(builder);
            });

    [AssemblyCleanup]
    public static async Task CleanupAsync()
    {
        Client?.Dispose();

        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }

        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            try
            {
                var dbOptions = new DbContextOptionsBuilder<ExampleAppDbContext>()
                    .UseSqlServer(_connectionString)
                    .Options;
                await using var context = new ExampleAppDbContext(dbOptions);
                await context.Database.EnsureDeletedAsync();
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

public sealed class IsolatedExampleApiFactory : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _connectionString;

    internal IsolatedExampleApiFactory(WebApplicationFactory<Program> factory, string connectionString)
    {
        _factory = factory;
        _connectionString = connectionString;
    }

    public HttpClient CreateClient(WebApplicationFactoryClientOptions options)
        => _factory.CreateClient(options);

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        var dbOptions = new DbContextOptionsBuilder<ExampleAppDbContext>()
            .UseSqlServer(_connectionString)
            .Options;
        await using var context = new ExampleAppDbContext(dbOptions);
        await context.Database.EnsureDeletedAsync();
    }
}
