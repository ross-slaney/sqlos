using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Pagination;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAdminDashboardTests
{
    [TestMethod]
    public async Task ListClientsAsync_AppliesFilters_AndSurfacesLifecycleMetadata()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().AddRange(
            new SqlOSClientApplication
            {
                Id = "cli_dcr_1",
                ClientId = "dcr-chatgpt-1",
                Name = "ChatGPT Bridge One",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://chatgpt.example.test/callback\"]",
                RegistrationSource = "dcr",
                SoftwareId = "chatgpt",
                SoftwareVersion = "1.0.0",
                ClientUri = "https://chatgpt.example.test",
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                IsActive = true
            },
            new SqlOSClientApplication
            {
                Id = "cli_dcr_2",
                ClientId = "dcr-chatgpt-2",
                Name = "ChatGPT Bridge Two",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://chatgpt.example.test/callback\"]",
                RegistrationSource = "dcr",
                SoftwareId = "chatgpt",
                SoftwareVersion = "1.0.0",
                ClientUri = "https://chatgpt.example.test",
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                IsActive = true
            },
            new SqlOSClientApplication
            {
                Id = "cli_cimd",
                ClientId = "https://portable.example.test/oauth/client.json",
                Name = "Portable Client",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://portable.example.test/callback\"]",
                RegistrationSource = "cimd",
                MetadataDocumentUrl = "https://portable.example.test/oauth/client.json",
                MetadataExpiresAt = DateTime.UtcNow.AddMinutes(-5),
                CreatedAt = DateTime.UtcNow.AddDays(-3),
                IsActive = true
            },
            new SqlOSClientApplication
            {
                Id = "cli_disabled",
                ClientId = "manual-disabled",
                Name = "Manual Disabled",
                Description = "Legacy browser client",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://manual.example.test/callback\"]",
                RegistrationSource = "manual",
                CreatedAt = DateTime.UtcNow.AddDays(-4),
                IsActive = false,
                DisabledAt = DateTime.UtcNow.AddDays(-1),
                DisabledReason = "manual_review"
            });
        await context.SaveChangesAsync();

        var dcrResult = SerializeForDashboard(await admin.ListClientsAsync(
            source: "dcr",
            status: "active",
            search: "chatgpt",
            page: 1,
            pageSize: 10));

        dcrResult.GetProperty("data").GetArrayLength().Should().Be(2);
        dcrResult.GetProperty("pageSize").GetInt32().Should().Be(10);
        dcrResult.GetProperty("hasNextPage").GetBoolean().Should().BeFalse();
        dcrResult.TryGetProperty("totalCount", out _).Should().BeFalse();
        dcrResult.TryGetProperty("page", out _).Should().BeFalse();
        dcrResult.GetProperty("summary").GetProperty("activeCount").GetInt32().Should().Be(2);
        dcrResult.GetProperty("summary").GetProperty("registeredCount").GetInt32().Should().Be(2);
        dcrResult.GetProperty("summary").GetProperty("discoveredCount").GetInt32().Should().Be(0);
        dcrResult.GetProperty("summary").GetProperty("disabledCount").GetInt32().Should().Be(0);
        var dcrItems = dcrResult.GetProperty("data");
        dcrItems.GetArrayLength().Should().Be(2);
        foreach (var item in dcrItems.EnumerateArray())
        {
            item.GetProperty("registrationSource").GetString().Should().Be("dcr");
            item.GetProperty("sourceLabel").GetString().Should().Be("Registered");
            item.GetProperty("lifecycleState").GetString().Should().Be("active");
            item.GetProperty("duplicateCount").GetInt32().Should().Be(2);
        }

        var cimdResult = SerializeForDashboard(await admin.ListClientsAsync(
            source: "cimd",
            page: 1,
            pageSize: 10));

        cimdResult.GetProperty("data").GetArrayLength().Should().Be(1);
        cimdResult.GetProperty("summary").GetProperty("activeCount").GetInt32().Should().Be(1);
        cimdResult.GetProperty("summary").GetProperty("discoveredCount").GetInt32().Should().Be(1);
        var cimdItem = cimdResult.GetProperty("data")[0];
        cimdItem.GetProperty("sourceLabel").GetString().Should().Be("Discovered");
        cimdItem.GetProperty("metadataCacheState").GetString().Should().Be("stale");

        var descriptionSearchResult = SerializeForDashboard(await admin.ListClientsAsync(
            status: "disabled",
            search: "legacy browser",
            page: 1,
            pageSize: 10));

        descriptionSearchResult.GetProperty("data").GetArrayLength().Should().Be(1);
        descriptionSearchResult.GetProperty("data")[0].GetProperty("clientId").GetString().Should().Be("manual-disabled");
    }

    [TestMethod]
    public async Task GetClientDetailAsync_IncludesAuditAndRichFields()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().AddRange(
            new SqlOSClientApplication
            {
                Id = "cli_detail_1",
                ClientId = "detail-client-1",
                Name = "Detail Client One",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://detail.example.test/callback\"]",
                RegistrationSource = "dcr",
                SoftwareId = "detail-suite",
                SoftwareVersion = "2026.1",
                ClientUri = "https://detail.example.test",
                MetadataJson = "{\"client_name\":\"Detail Client One\"}",
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                IsActive = true
            },
            new SqlOSClientApplication
            {
                Id = "cli_detail_2",
                ClientId = "detail-client-2",
                Name = "Detail Client Two",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://detail.example.test/callback\"]",
                RegistrationSource = "dcr",
                SoftwareId = "detail-suite",
                SoftwareVersion = "2026.1",
                ClientUri = "https://detail.example.test",
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                IsActive = true
            });
        await context.SaveChangesAsync();

        await admin.RecordAuditAsync(
            "client.disabled",
            "client",
            "cli_detail_1",
            data: new { client_id = "detail-client-1", reason = "manual_review" });

        var detail = SerializeForDashboard(await admin.GetClientDetailAsync("cli_detail_1"));

        detail.GetProperty("clientId").GetString().Should().Be("detail-client-1");
        detail.GetProperty("sourceLabel").GetString().Should().Be("Registered");
        detail.GetProperty("duplicateCount").GetInt32().Should().Be(2);
        detail.GetProperty("redirectUris").GetArrayLength().Should().Be(1);
        detail.GetProperty("metadataJson").GetString().Should().Contain("Detail Client One");
        detail.GetProperty("recentAuditEvents").GetArrayLength().Should().BeGreaterThan(0);
        detail.GetProperty("recentAuditEvents")[0].GetProperty("eventType").GetString().Should().Be("client.disabled");
        detail.GetProperty("emptyAllowlistWarning").GetProperty("code").GetString()
            .Should().Be(SqlOSClientAllowlistWarnings.EmptyAllowlistCode);
        detail.GetProperty("emptyAllowlistWarning").GetProperty("message").GetString()
            .Should().Be(SqlOSClientAllowlistWarnings.UserFacingEmptyAllowlistMessage);
        detail.GetProperty("omittedOpenIdWarning").GetProperty("code").GetString()
            .Should().Be(SqlOSOpenIdScopeWarnings.MissingAllowlistedOpenIdCode);
        detail.GetProperty("omittedOpenIdWarning").GetProperty("message").GetString()
            .Should().Be(SqlOSOpenIdScopeWarnings.MissingAllowlistedOpenIdMessage);
    }

    [TestMethod]
    public async Task GetClientDetailAsync_PopulatedAllowlist_DoesNotWarn()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
        {
            Id = "cli_populated_scopes",
            ClientId = "populated-scopes-client",
            Name = "Populated Scopes Client",
            Audience = "sqlos",
            RedirectUrisJson = "[\"https://app.example.test/callback\"]",
            AllowedScopesJson = "[\"openid\",\"profile\",\"email\"]",
            RegistrationSource = "seeded",
            IsFirstParty = true,
            ConfigurationOwner = "code",
            ConfigurationSourceKey = "populated-scopes-client",
            IsActive = true
        });
        await context.SaveChangesAsync();

        var detail = SerializeForDashboard(await admin.GetClientDetailAsync("cli_populated_scopes"));
        detail.TryGetProperty("emptyAllowlistWarning", out var emptyWarning).Should().BeTrue();
        emptyWarning.ValueKind.Should().Be(JsonValueKind.Null);
        detail.TryGetProperty("omittedOpenIdWarning", out var openIdWarning).Should().BeTrue();
        openIdWarning.ValueKind.Should().Be(JsonValueKind.Null);
        detail.GetProperty("oidcCapable").GetBoolean().Should().BeTrue();
        detail.GetProperty("oidcDiscoveryUrl").GetString()
            .Should().Be("https://localhost/sqlos/auth/.well-known/openid-configuration");
    }

    [TestMethod]
    public async Task GetClientDetailAsync_OmittedOpenIdWarning_ClearsWhenOpenIdIsAllowlisted()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
        {
            Id = "cli_openid_warn",
            ClientId = "openid-warn-client",
            Name = "OpenID Warn Client",
            Audience = "sqlos",
            RedirectUrisJson = "[\"https://app.example.test/callback\"]",
            AllowedScopesJson = "[\"profile\",\"email\"]",
            RegistrationSource = "manual",
            IsFirstParty = true,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var missing = SerializeForDashboard(await admin.GetClientDetailAsync("cli_openid_warn"));
        missing.GetProperty("omittedOpenIdWarning").GetProperty("code").GetString()
            .Should().Be(SqlOSOpenIdScopeWarnings.MissingAllowlistedOpenIdCode);
        missing.GetProperty("oidcCapable").GetBoolean().Should().BeFalse();
        missing.GetProperty("oidcDiscoveryUrl").ValueKind.Should().Be(JsonValueKind.Null);

        var stored = await context.Set<SqlOSClientApplication>().SingleAsync(x => x.Id == "cli_openid_warn");
        stored.AllowedScopesJson = "[\"openid\",\"profile\",\"email\"]";
        await context.SaveChangesAsync();

        var cleared = SerializeForDashboard(await admin.GetClientDetailAsync("cli_openid_warn"));
        cleared.TryGetProperty("omittedOpenIdWarning", out var warning).Should().BeTrue();
        warning.ValueKind.Should().Be(JsonValueKind.Null);
        cleared.GetProperty("oidcCapable").GetBoolean().Should().BeTrue();
        cleared.GetProperty("oidcDiscoveryUrl").GetString()
            .Should().Be("https://localhost/sqlos/auth/.well-known/openid-configuration");
    }

    [TestMethod]
    public async Task GetClientDetailAsync_OpenIdProviderDisabled_IsNeverOidcCapable()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.ConfigureOpenIdProvider(provider => provider.Enabled = false);
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
        {
            Id = "cli_op_disabled",
            ClientId = "op-disabled-client",
            Name = "Provider Disabled Client",
            Audience = "sqlos",
            RedirectUrisJson = "[\"https://app.example.test/callback\"]",
            AllowedScopesJson = "[\"openid\",\"profile\",\"email\"]",
            RegistrationSource = "manual",
            IsFirstParty = true,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var detail = SerializeForDashboard(await admin.GetClientDetailAsync("cli_op_disabled"));
        detail.GetProperty("oidcCapable").GetBoolean().Should().BeFalse();
        detail.GetProperty("oidcDiscoveryUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestMethod]
    public async Task GetClientDetailAsync_UnpublishedDiscovery_StaysOidcCapableWithoutAdvertisingUrl()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.ConfigureOpenIdProvider(provider => provider.PublishDiscoveryDocument = false);
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
        {
            Id = "cli_unpublished_discovery",
            ClientId = "unpublished-discovery-client",
            Name = "Unpublished Discovery Client",
            Audience = "sqlos",
            RedirectUrisJson = "[\"https://app.example.test/callback\"]",
            AllowedScopesJson = "[\"openid\",\"profile\",\"email\"]",
            RegistrationSource = "manual",
            IsFirstParty = true,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var detail = SerializeForDashboard(await admin.GetClientDetailAsync("cli_unpublished_discovery"));
        detail.GetProperty("oidcCapable").GetBoolean().Should().BeTrue(
            "capability reflects provider mode and the allowlist, not discovery publication");
        detail.GetProperty("oidcDiscoveryUrl").ValueKind.Should().Be(
            JsonValueKind.Null,
            "the discovery route returns 404 while PublishDiscoveryDocument is false, so the URL must not be advertised");
    }

    [TestMethod]
    public async Task ListClientsAsync_ProjectsOidcCapable_WithTheDetailRule()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().AddRange(
            new SqlOSClientApplication
            {
                Id = "cli_list_oidc",
                ClientId = "list-oidc-client",
                Name = "List OIDC Client",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://app.example.test/callback\"]",
                AllowedScopesJson = "[\"openid\",\"profile\",\"email\"]",
                RegistrationSource = "manual",
                IsActive = true
            },
            new SqlOSClientApplication
            {
                Id = "cli_list_plain",
                ClientId = "list-plain-client",
                Name = "List Plain Client",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://app.example.test/callback\"]",
                AllowedScopesJson = "[\"profile\"]",
                RegistrationSource = "manual",
                IsActive = true
            });
        await context.SaveChangesAsync();

        var result = SerializeForDashboard(await admin.ListClientsAsync(pageSize: 10));
        var items = result.GetProperty("data").EnumerateArray()
            .ToDictionary(item => item.GetProperty("clientId").GetString()!);
        items["list-oidc-client"].GetProperty("oidcCapable").GetBoolean().Should().BeTrue(
            "the list projection shares the detail rule: provider enabled and openid allowlisted");
        items["list-plain-client"].GetProperty("oidcCapable").GetBoolean().Should().BeFalse();

        var disabledOptionsValue = new SqlOSAuthServerOptions();
        disabledOptionsValue.ConfigureOpenIdProvider(provider => provider.Enabled = false);
        var disabledOptions = Options.Create(disabledOptionsValue);
        var disabledAdmin = new SqlOSAdminService(
            context,
            disabledOptions,
            TestCryptoService.Create(context, disabledOptions));

        var disabledResult = SerializeForDashboard(await disabledAdmin.ListClientsAsync(pageSize: 10));
        disabledResult.GetProperty("data").EnumerateArray()
            .Should().OnlyContain(item => !item.GetProperty("oidcCapable").GetBoolean(),
                "no client is OIDC capable while OpenID Provider mode is disabled");
    }

    [TestMethod]
    public async Task GetClientDetailAsync_MachineOnlyClient_DoesNotWarnAboutMissingOpenId()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
        {
            Id = "cli_machine_openid",
            ClientId = "machine-openid-client",
            Name = "Machine Client",
            Audience = "sqlos",
            RedirectUrisJson = "[]",
            AllowedScopesJson = "[\"ledger.export\"]",
            GrantTypesJson = "[\"client_credentials\"]",
            RegistrationSource = "manual",
            IsFirstParty = false,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var detail = SerializeForDashboard(await admin.GetClientDetailAsync("cli_machine_openid"));
        detail.TryGetProperty("omittedOpenIdWarning", out var warning).Should().BeTrue();
        warning.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestMethod]
    public async Task OidcCapable_MachineOnlyClientWithOpenIdAllowlisted_IsNotCapable()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().AddRange(
            new SqlOSClientApplication
            {
                Id = "cli_machine_oidc",
                ClientId = "machine-oidc-client",
                Name = "Machine OIDC Client",
                Audience = "sqlos",
                RedirectUrisJson = "[]",
                AllowedScopesJson = "[\"openid\",\"ledger.export\"]",
                GrantTypesJson = "[\"client_credentials\"]",
                RegistrationSource = "manual",
                IsFirstParty = false,
                IsActive = true
            },
            new SqlOSClientApplication
            {
                Id = "cli_redirect_oidc",
                ClientId = "redirect-oidc-client",
                Name = "Redirect OIDC Client",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://app.example.test/callback\"]",
                AllowedScopesJson = "[\"openid\",\"profile\"]",
                RegistrationSource = "manual",
                IsActive = true
            });
        await context.SaveChangesAsync();

        var list = SerializeForDashboard(await admin.ListClientsAsync(pageSize: 10));
        var items = list.GetProperty("data").EnumerateArray()
            .ToDictionary(item => item.GetProperty("clientId").GetString()!);
        items["machine-oidc-client"].GetProperty("oidcCapable").GetBoolean().Should().BeFalse(
            "a client-credentials-only client can never complete an interactive flow, even with openid allowlisted");
        items["redirect-oidc-client"].GetProperty("oidcCapable").GetBoolean().Should().BeTrue(
            "a redirect-capable client with openid allowlisted stays OIDC capable");

        var machineDetail = SerializeForDashboard(await admin.GetClientDetailAsync("cli_machine_oidc"));
        machineDetail.GetProperty("oidcCapable").GetBoolean().Should().BeFalse(
            "the detail projection shares the list rule");
        machineDetail.GetProperty("oidcDiscoveryUrl").ValueKind.Should().Be(JsonValueKind.Null);

        var redirectDetail = SerializeForDashboard(await admin.GetClientDetailAsync("cli_redirect_oidc"));
        redirectDetail.GetProperty("oidcCapable").GetBoolean().Should().BeTrue();
        redirectDetail.GetProperty("oidcDiscoveryUrl").GetString()
            .Should().Be("https://localhost/sqlos/auth/.well-known/openid-configuration");
    }

    [TestMethod]
    public async Task DuplicateCount_UsesNormalizedRedirectUriFingerprint()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().AddRange(
            new SqlOSClientApplication
            {
                Id = "cli_fp_1",
                ClientId = "fp-client-1",
                Name = "Fingerprint One",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://a.example.test/callback\",\"https://b.example.test/callback\"]",
                RegistrationSource = "dcr",
                SoftwareId = "fp-suite",
                SoftwareVersion = "1.0.0",
                ClientUri = "https://fp.example.test",
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                IsActive = true
            },
            new SqlOSClientApplication
            {
                Id = "cli_fp_2",
                ClientId = "fp-client-2",
                Name = "Fingerprint Two",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://b.example.test/callback\",\"https://a.example.test/callback\"]",
                RegistrationSource = "dcr",
                SoftwareId = "fp-suite",
                SoftwareVersion = "1.0.0",
                ClientUri = "https://fp.example.test",
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                IsActive = true
            });
        await context.SaveChangesAsync();

        var list = SerializeForDashboard(await admin.ListClientsAsync(source: "dcr", pageSize: 10));
        foreach (var item in list.GetProperty("data").EnumerateArray())
        {
            item.GetProperty("duplicateCount").GetInt32().Should().Be(2);
        }

        var detail = SerializeForDashboard(await admin.GetClientDetailAsync("cli_fp_1"));
        detail.GetProperty("duplicateCount").GetInt32().Should().Be(2);
    }

    [TestMethod]
    public async Task ListUsersAsync_RejectsLegacyOffsetPages()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        var act = async () => await admin.ListUsersAsync(page: 2, pageSize: 10);
        await act.Should().ThrowAsync<SqlOSCursorException>()
            .WithMessage("*Offset pagination*");
    }

    private static JsonElement SerializeForDashboard(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}
