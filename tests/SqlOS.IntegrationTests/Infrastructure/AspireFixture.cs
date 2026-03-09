using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Fga.Infrastructure;

namespace SqlOS.IntegrationTests.Infrastructure;

[TestClass]
public static class AspireFixture
{
    private static DistributedApplication? _app;

    public static string SqlConnectionString { get; private set; } = string.Empty;
    public static TestSqlOSDbContext SharedContext { get; private set; } = null!;
    public static SqlOSAuthServerOptions Options { get; private set; } = new();
    public static SqlOSFgaOptions FgaOptions { get; private set; } = new();

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
        var databaseName = $"SqlOSTest_{Guid.NewGuid():N}"[..30];
        SqlConnectionString = baseConnectionString.Replace("Database=sqlos-test", $"Database={databaseName}");
        Options = new SqlOSAuthServerOptions { Issuer = "https://tests/sqlos/auth", BasePath = "/sqlos/auth" };
        FgaOptions = new SqlOSFgaOptions();

        var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(SqlConnectionString)
            .Options;
        SharedContext = new TestSqlOSDbContext(dbOptions);
        await SharedContext.Database.EnsureCreatedAsync();

        var schemaInitializer = new SqlOSSchemaInitializer(
            SharedContext,
            Microsoft.Extensions.Options.Options.Create(Options),
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());
        await schemaInitializer.EnsureSchemaAsync();

        var fgaSchemaInitializer = new SqlOSFgaSchemaInitializer(
            SharedContext,
            Microsoft.Extensions.Options.Options.Create(FgaOptions),
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSFgaSchemaInitializer>());
        await fgaSchemaInitializer.EnsureSchemaAsync();

        var fgaFunctionInitializer = new SqlOSFgaFunctionInitializer(
            SharedContext,
            Microsoft.Extensions.Options.Options.Create(FgaOptions),
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSFgaFunctionInitializer>());
        await fgaFunctionInitializer.EnsureFunctionsExistAsync();

        var fgaSeedService = new SqlOSFgaSeedService(
            SharedContext,
            Microsoft.Extensions.Options.Options.Create(FgaOptions),
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSFgaSeedService>());
        await fgaSeedService.SeedCoreAsync();
        await FgaTestDataSeeder.SeedAsync(SharedContext);

        var crypto = new SqlOSCryptoService(SharedContext, Microsoft.Extensions.Options.Options.Create(Options));
        var admin = new SqlOSAdminService(SharedContext, Microsoft.Extensions.Options.Options.Create(Options), crypto);
        await crypto.EnsureActiveSigningKeyAsync();
        await admin.EnsureDefaultClientAsync();

        context.WriteLine($"SqlOS integration DB initialized: {databaseName}");
    }

    [AssemblyCleanup]
    public static async Task CleanupAsync()
    {
        if (SharedContext != null)
        {
            await SharedContext.Database.EnsureDeletedAsync();
            await SharedContext.DisposeAsync();
        }

        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
