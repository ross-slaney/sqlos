using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSOidcConnectionSeedingTests
{
    [TestMethod]
    public async Task UpsertSeededOidcConnectionsAsync_SeedsMicrosoftConnection()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedMicrosoftConnection(
            "microsoft-client",
            "microsoft-secret",
            tenant: "common",
            "http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}");

        var (admin, _) = CreateServices(context, optionsValue);

        await admin.UpsertSeededOidcConnectionsAsync();

        var connection = await context.Set<SqlOSOidcConnection>().SingleAsync();
        connection.ProviderType.Should().Be(SqlOSOidcProviderType.Microsoft);
        connection.DisplayName.Should().Be("Microsoft");
        connection.ClientId.Should().Be("microsoft-client");
        connection.IsEnabled.Should().BeTrue();
        connection.UseDiscovery.Should().BeTrue();
        connection.MicrosoftTenant.Should().Be("common");
        connection.DiscoveryUrl.Should().Be("https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration");
        connection.ClientSecretEncrypted.Should().NotBeNullOrWhiteSpace();

        // The {connectionId} placeholder is replaced with the generated id.
        var callbacks = JsonSerializer.Deserialize<List<string>>(connection.AllowedCallbackUrisJson)!;
        callbacks.Should().ContainSingle()
            .Which.Should().Be($"http://localhost:5062/api/v1/auth/oidc/callback/{connection.Id}");
    }

    [TestMethod]
    public async Task UpsertSeededOidcConnectionsAsync_SeededConnectionAppearsAsEnabledProvider()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedMicrosoftConnection(
            "microsoft-client",
            "microsoft-secret",
            tenant: null,
            "http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}");

        var (admin, oidc) = CreateServices(context, optionsValue);
        await admin.UpsertSeededOidcConnectionsAsync();

        var providers = await oidc.ListEnabledProvidersAsync();

        providers.Should().ContainSingle(x => x.ProviderType == "Microsoft" && x.DisplayName == "Microsoft");
    }

    [TestMethod]
    public async Task UpsertSeededOidcConnectionsAsync_IsIdempotent_AndReconcilesFields()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedMicrosoftConnection(
            "microsoft-client",
            "microsoft-secret",
            tenant: "common",
            "http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}");

        var (admin, _) = CreateServices(context, optionsValue);

        await admin.UpsertSeededOidcConnectionsAsync();
        var firstId = (await context.Set<SqlOSOidcConnection>().SingleAsync()).Id;

        // Mutate the seed to simulate a config change, then re-run startup seeding.
        optionsValue.OidcConnectionSeeds[0].ClientId = "microsoft-client-rotated";
        optionsValue.OidcConnectionSeeds[0].MicrosoftTenant = "contoso.onmicrosoft.com";

        await admin.UpsertSeededOidcConnectionsAsync();

        var connection = await context.Set<SqlOSOidcConnection>().SingleAsync();
        connection.Id.Should().Be(firstId, "re-seeding must update in place, not create a duplicate");
        connection.ClientId.Should().Be("microsoft-client-rotated");
        connection.MicrosoftTenant.Should().Be("contoso.onmicrosoft.com");
        connection.DiscoveryUrl.Should().Be("https://login.microsoftonline.com/contoso.onmicrosoft.com/v2.0/.well-known/openid-configuration");
    }

    [TestMethod]
    public async Task UpsertSeededOidcConnectionsAsync_PreservesManualDisable()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedMicrosoftConnection(
            "microsoft-client",
            "microsoft-secret",
            tenant: "common",
            "http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}");

        var (admin, _) = CreateServices(context, optionsValue);
        await admin.UpsertSeededOidcConnectionsAsync();

        var connection = await context.Set<SqlOSOidcConnection>().SingleAsync();
        await admin.SetOidcConnectionEnabledAsync(connection.Id, false);

        // Re-run seeding: a manual disable from the dashboard must survive restart.
        await admin.UpsertSeededOidcConnectionsAsync();

        var reloaded = await context.Set<SqlOSOidcConnection>().SingleAsync();
        reloaded.IsEnabled.Should().BeFalse();
    }

    [TestMethod]
    public async Task UpsertSeededOidcConnectionsAsync_DoesNotClearSecretWhenSeedOmitsIt()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedMicrosoftConnection(
            "microsoft-client",
            "microsoft-secret",
            tenant: "common",
            "http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}");

        var (admin, _) = CreateServices(context, optionsValue);
        await admin.UpsertSeededOidcConnectionsAsync();
        var original = (await context.Set<SqlOSOidcConnection>().SingleAsync()).ClientSecretEncrypted;

        // Config without a secret (kept out of source) must not wipe the stored credential.
        optionsValue.OidcConnectionSeeds[0].ClientSecret = null;
        await admin.UpsertSeededOidcConnectionsAsync();

        var connection = await context.Set<SqlOSOidcConnection>().SingleAsync();
        connection.ClientSecretEncrypted.Should().Be(original);
    }

    [TestMethod]
    public async Task UpsertSeededOidcConnectionsAsync_SeedsGoogleConnection()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedGoogleConnection(
            "google-client",
            "google-secret",
            "http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}");

        var (admin, _) = CreateServices(context, optionsValue);

        await admin.UpsertSeededOidcConnectionsAsync();

        var connection = await context.Set<SqlOSOidcConnection>().SingleAsync();
        connection.ProviderType.Should().Be(SqlOSOidcProviderType.Google);
        connection.DisplayName.Should().Be("Google");
        connection.ClientId.Should().Be("google-client");
        connection.IsEnabled.Should().BeTrue();
        connection.UseDiscovery.Should().BeTrue();
        connection.DiscoveryUrl.Should().Be("https://accounts.google.com/.well-known/openid-configuration");
        connection.MicrosoftTenant.Should().BeNull();
        connection.ClientSecretEncrypted.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task UpsertSeededOidcConnectionsAsync_SeedsGitHubConnection()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedGitHubConnection(
            "github-client",
            "github-secret",
            "http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}");

        var (admin, oidc) = CreateServices(context, optionsValue);

        await admin.UpsertSeededOidcConnectionsAsync();

        var connection = await context.Set<SqlOSOidcConnection>().SingleAsync();
        connection.ProviderType.Should().Be(SqlOSOidcProviderType.GitHub);
        connection.Protocol.Should().Be(SqlOSSocialProviderProtocol.OAuthProfile);
        connection.DisplayName.Should().Be("GitHub");
        connection.ClientId.Should().Be("github-client");
        connection.UseDiscovery.Should().BeFalse();
        connection.Issuer.Should().Be("https://github.com");
        connection.AuthorizationEndpoint.Should().Be("https://github.com/login/oauth/authorize");
        connection.TokenEndpoint.Should().Be("https://github.com/login/oauth/access_token");
        connection.UserInfoEndpoint.Should().Be("https://api.github.com/user");
        connection.JwksUri.Should().BeNull();
        connection.ClientSecretEncrypted.Should().NotBeNullOrWhiteSpace();

        var providers = await oidc.ListEnabledProvidersAsync();
        providers.Should().ContainSingle(x =>
            x.ProviderType == "GitHub" &&
            x.Protocol == "OAuthProfile" &&
            !string.IsNullOrWhiteSpace(x.LogoDataUrl));
    }

    [TestMethod]
    public async Task UpsertSeededOidcConnectionsAsync_MatchesCustomConnectionByDisplayName()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue
            .SeedOidcConnection(oidc =>
            {
                oidc.ProviderType = SqlOSOidcProviderType.Custom;
                oidc.DisplayName = "Acme Identity";
                oidc.ClientId = "acme-client";
                oidc.ClientSecret = "acme-secret";
                oidc.UseDiscovery = true;
                oidc.DiscoveryUrl = "https://oidc.example.local/.well-known/openid-configuration";
                oidc.AllowedCallbackUris = ["https://app.example.local/callback/{connectionId}"];
            })
            .SeedOidcConnection(oidc =>
            {
                oidc.ProviderType = SqlOSOidcProviderType.Custom;
                oidc.DisplayName = "Globex Identity";
                oidc.ClientId = "globex-client";
                oidc.ClientSecret = "globex-secret";
                oidc.UseDiscovery = true;
                oidc.DiscoveryUrl = "https://oidc.globex.local/.well-known/openid-configuration";
                oidc.AllowedCallbackUris = ["https://app.example.local/callback/{connectionId}"];
            });

        var (admin, _) = CreateServices(context, optionsValue);

        await admin.UpsertSeededOidcConnectionsAsync();
        await admin.UpsertSeededOidcConnectionsAsync();

        var connections = await context.Set<SqlOSOidcConnection>().ToListAsync();
        connections.Should().HaveCount(2);
        connections.Select(x => x.DisplayName).Should().BeEquivalentTo(["Acme Identity", "Globex Identity"]);
    }

    [TestMethod]
    public async Task UpsertSeededOidcConnectionsAsync_ThrowsWhenCallbackMissing()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedOidcConnection(oidc =>
        {
            oidc.ProviderType = SqlOSOidcProviderType.Microsoft;
            oidc.DisplayName = "Microsoft";
            oidc.ClientId = "microsoft-client";
            oidc.ClientSecret = "microsoft-secret";
            oidc.AllowedCallbackUris = [];
        });

        var (admin, _) = CreateServices(context, optionsValue);

        await FluentActions.Awaiting(() => admin.UpsertSeededOidcConnectionsAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*callback URI*");
    }

    [TestMethod]
    public async Task UpsertSeededOidcConnectionsAsync_ThrowsWhenSecretMissing()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedOidcConnection(oidc =>
        {
            oidc.ProviderType = SqlOSOidcProviderType.Microsoft;
            oidc.DisplayName = "Microsoft";
            oidc.ClientId = "microsoft-client";
            oidc.ClientSecret = null;
            oidc.AllowedCallbackUris = ["http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}"];
        });

        var (admin, _) = CreateServices(context, optionsValue);

        await FluentActions.Awaiting(() => admin.UpsertSeededOidcConnectionsAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*client secret*");
    }

    [TestMethod]
    public async Task UpsertSeededOidcConnectionsAsync_NoSeeds_IsNoOp()
    {
        using var context = CreateContext();
        var (admin, _) = CreateServices(context, new SqlOSAuthServerOptions());

        await admin.UpsertSeededOidcConnectionsAsync();

        (await context.Set<SqlOSOidcConnection>().CountAsync()).Should().Be(0);
    }

    private static (SqlOSAdminService admin, SqlOSOidcAuthService oidc) CreateServices(
        TestSqlOSInMemoryDbContext context,
        SqlOSAuthServerOptions optionsValue)
    {
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options, new EphemeralDataProtectionProvider());
        var admin = new SqlOSAdminService(context, options, crypto);
        var oidc = new SqlOSOidcAuthService(
            context,
            admin,
            crypto,
            new FakeOidcProviderHttpClientFactory(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlOSOidcAuthService>.Instance);
        return (admin, oidc);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}
