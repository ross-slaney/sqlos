using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.DataProtection;
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
    public static IDataProtectionProvider DataProtectionProvider { get; } = new EphemeralDataProtectionProvider();

    [AssemblyInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        if (TestDatabase.IsPostgreSql)
        {
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        }

        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.SqlOS_IntegrationTests_AppHost>();

        _app = await appHost.BuildAsync();
        await _app.StartAsync();

        using var sqlCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("sql", sqlCts.Token);

        var baseConnectionString = await _app.GetConnectionStringAsync("sqlos-test")
            ?? throw new InvalidOperationException("Could not get SQL connection string from Aspire.");
        var databaseName = $"SqlOSTest_{Guid.NewGuid():N}"[..30];
        await TestDatabase.CreateDatabaseAsync(baseConnectionString, databaseName);
        SqlConnectionString = TestDatabase.CreateIsolatedConnectionString(baseConnectionString, databaseName);
        Options = new SqlOSAuthServerOptions { Issuer = "https://tests/sqlos/auth", BasePath = "/sqlos/auth" };
        Options.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
        FgaOptions = new SqlOSFgaOptions();

        var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseTestProvider(SqlConnectionString)
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

        var crypto = new SqlOSCryptoService(SharedContext, Microsoft.Extensions.Options.Options.Create(Options), DataProtectionProvider);
        var admin = new SqlOSAdminService(SharedContext, Microsoft.Extensions.Options.Options.Create(Options), crypto);
        await crypto.EnsureActiveSigningKeyAsync();
        await admin.UpsertSeededClientsAsync();

        context.WriteLine($"SqlOS integration DB initialized: {databaseName}");
    }

    public static async Task<TestSqlOSDbContext> CreateIsolatedAuthContextAsync(
        string databasePrefix = "SqlOSCase",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SqlConnectionString))
        {
            throw new InvalidOperationException("AspireFixture has not been initialized.");
        }

        var safePrefix = new string(databasePrefix.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safePrefix))
        {
            safePrefix = "SqlOSCase";
        }

        safePrefix = safePrefix.Length > 24 ? safePrefix[..24] : safePrefix;
        var databaseName = $"{safePrefix}_{Guid.NewGuid():N}";
        await TestDatabase.CreateDatabaseAsync(SqlConnectionString, databaseName, cancellationToken);
        var connectionString = TestDatabase.CreateIsolatedConnectionString(SqlConnectionString, databaseName);

        var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseTestProvider(connectionString)
            .Options;
        var context = new TestSqlOSDbContext(dbOptions);
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var schemaInitializer = new SqlOSSchemaInitializer(
            context,
            Microsoft.Extensions.Options.Options.Create(Options),
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());
        await schemaInitializer.EnsureSchemaAsync(cancellationToken);

        return context;
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
