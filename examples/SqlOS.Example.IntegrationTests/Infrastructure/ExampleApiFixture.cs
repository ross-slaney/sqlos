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

    private static WebApplicationFactory<Program> BuildFactory(Action<IWebHostBuilder>? configureBuilder = null)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("environment", "Development");
                builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
                builder.UseSetting("SqlOS:Issuer", "https://localhost/sqlos/auth");
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
