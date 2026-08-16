using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSControlPlaneParityTests
{
    private const string Callback = "https://app.parity.test/callback";

    [TestMethod]
    public async Task OAuthClient_CodeServiceAndDashboard_NormalizeAndAuthorizeEquivalently()
    {
        await using var code = await ControlPlaneParityHarness.CreateAsync(options => options.SeedClient(seed =>
        {
            seed.ClientId = "parity-client";
            seed.Name = "Parity Client";
            seed.RedirectUris = [Callback];
            seed.AllowedScopes = ["openid", "profile"];
        }));
        await using var service = await ControlPlaneParityHarness.CreateAsync();
        await using var dashboard = await ControlPlaneParityHarness.CreateAsync();

        await code.ReconcileStartupAsync();
        await service.Admin.CreateClientAsync(ClientRequest());
        await dashboard.PostDashboardAsync(DashboardAdminContracts.Clients, ClientPayload());

        var codeProjection = await code.ProjectClientAsync("parity-client");
        var serviceProjection = await service.ProjectClientAsync("parity-client");
        var dashboardProjection = await dashboard.ProjectClientAsync("parity-client");

        codeProjection.Configuration.Should().BeEquivalentTo(serviceProjection.Configuration);
        dashboardProjection.Configuration.Should().BeEquivalentTo(serviceProjection.Configuration);
        codeProjection.Owner.Should().Be(SqlOSConfigurationOwners.Code);
        codeProjection.IsEditable.Should().BeFalse();
        serviceProjection.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        dashboardProjection.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);

        foreach (var harness in new[] { code, service, dashboard })
        {
            using var authorize = await harness.Client.GetAsync(
                "/sqlos/auth/authorize?client_id=parity-client&redirect_uri=https%3A%2F%2Fapp.parity.test%2Fcallback&response_type=code&scope=openid&state=parity&code_challenge=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&code_challenge_method=S256");
            authorize.StatusCode.Should().NotBe(HttpStatusCode.BadRequest, "all three clients must pass real authorization-request client validation");
        }

        var codeClient = await code.Context.Set<SqlOSClientApplication>().SingleAsync();
        var serviceClient = await service.Context.Set<SqlOSClientApplication>().SingleAsync();
        await code.Admin.DisableClientAsync(codeClient.Id, "parity_disable");
        await service.Admin.DisableClientAsync(serviceClient.Id, "parity_disable");
        var dashboardClient = await dashboard.Context.Set<SqlOSClientApplication>().SingleAsync();
        await dashboard.PostDashboardAsync($"{DashboardAdminContracts.Clients}/{dashboardClient.Id}/disable", new { reason = "parity_disable" });
        new[] { await code.ProjectClientAsync("parity-client"), await service.ProjectClientAsync("parity-client"), await dashboard.ProjectClientAsync("parity-client") }
            .Should().OnlyContain(x => !x.IsEnabled);

        await code.Admin.EnableClientAsync(codeClient.Id);
        await service.Admin.EnableClientAsync(serviceClient.Id);
        using var enabled = await dashboard.Client.PostAsync($"{DashboardAdminContracts.Clients}/{dashboardClient.Id}/enable", null);
        enabled.EnsureSuccessStatusCode();
        new[] { await code.ProjectClientAsync("parity-client"), await service.ProjectClientAsync("parity-client"), await dashboard.ProjectClientAsync("parity-client") }
            .Should().OnlyContain(x => x.IsEnabled);
    }

    [TestMethod]
    public async Task ConfidentialOAuthClient_CredentialsAreEquivalentRedactedAndIndependentOfFga()
    {
        const string codeSecret = "code-secret-with-at-least-256-bits-of-entropy-123456789";
        await using var code = await ControlPlaneParityHarness.CreateAsync(options => options.SeedClient(seed =>
        {
            seed.ClientId = "confidential-parity-client";
            seed.Name = "Confidential Parity Client";
            seed.ClientType = "confidential";
            seed.RedirectUris = [Callback];
            seed.AllowedScopes = ["openid", "profile"];
            seed.ClientSecretResolver = () => codeSecret;
        }));
        await using var service = await ControlPlaneParityHarness.CreateAsync();
        await using var dashboard = await ControlPlaneParityHarness.CreateAsync();

        await code.ReconcileStartupAsync();
        var serviceClient = await service.Admin.CreateClientAsync(ConfidentialClientRequest());
        var serviceCredential = await service.ClientAuthentication.CreateCredentialAsync(
            serviceClient.Id,
            "Initial credential");
        var credentialsJson = JsonSerializer.Serialize(
            await service.ClientAuthentication.ListCredentialsAsync(serviceClient.ClientId),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var credentials = JsonDocument.Parse(credentialsJson);
        credentials.RootElement.GetProperty("data").EnumerateArray()
            .Should().ContainSingle(x => x.GetProperty("id").GetString() == serviceCredential.Credential.Id);
        var dashboardClient = await dashboard.PostDashboardAsync(
            DashboardAdminContracts.Clients,
            ConfidentialClientPayload());
        var dashboardCredential = await dashboard.PostDashboardAsync(
            DashboardAdminContracts.ClientCredentials(dashboardClient.GetProperty("id").GetString()!),
            new { displayName = "Initial credential", expiresAt = (DateTime?)null });
        var dashboardSecret = dashboardCredential.GetProperty("clientSecret").GetString();

        var projections = new[]
        {
            await code.ProjectClientAsync("confidential-parity-client"),
            await service.ProjectClientAsync("confidential-parity-client"),
            await dashboard.ProjectClientAsync("confidential-parity-client")
        };
        projections[1].Configuration.Should().BeEquivalentTo(projections[0].Configuration);
        projections[2].Configuration.Should().BeEquivalentTo(projections[0].Configuration);
        projections[0].Owner.Should().Be(SqlOSConfigurationOwners.Code);
        projections.Skip(1).Select(x => x.Owner)
            .Should().OnlyContain(x => x == SqlOSConfigurationOwners.Dashboard);

        await AssertClientAuthenticatesAsync(code, "confidential-parity-client", codeSecret);
        await AssertClientAuthenticatesAsync(
            service,
            "confidential-parity-client",
            serviceCredential.ClientSecret);
        await AssertClientAuthenticatesAsync(
            dashboard,
            "confidential-parity-client",
            dashboardSecret!);

        foreach (var harness in new[] { code, service, dashboard })
        {
            (await harness.Context.Set<SqlOS.Fga.Models.SqlOSFgaServiceAccount>().CountAsync())
                .Should().Be(0, "confidential OAuth clients do not imply an FGA identity");
            var client = await harness.Context.Set<SqlOSClientApplication>()
                .SingleAsync(x => x.ClientId == "confidential-parity-client");
            var listed = await harness.Client.GetStringAsync(
                DashboardAdminContracts.ClientCredentials(client.Id));
            listed.Should().NotContain("secretHash");
            listed.Should().NotContain("clientSecret");
            listed.Should().NotContain(codeSecret);
            listed.Should().NotContain(serviceCredential.ClientSecret);
            listed.Should().NotContain(dashboardSecret);
        }
    }

    [TestMethod]
    public async Task OidcConnection_CodeServiceAndDashboard_NormalizeAndReachRuntimeProviderList()
    {
        await using var code = await ControlPlaneParityHarness.CreateAsync(options =>
            options.SeedGoogleConnection("provider-client", "provider-secret", Callback));
        await using var service = await ControlPlaneParityHarness.CreateAsync();
        await using var dashboard = await ControlPlaneParityHarness.CreateAsync();

        await code.ReconcileStartupAsync();
        await service.Admin.CreateOidcConnectionAsync(OidcRequest());
        var create = await dashboard.PostDashboardAsync(DashboardAdminContracts.OidcConnections, OidcPayload());

        create.TryGetProperty("clientSecret", out _).Should().BeFalse("provider secrets are write-only");
        var projections = new[]
        {
            await code.ProjectOidcAsync("Google"),
            await service.ProjectOidcAsync("Google"),
            await dashboard.ProjectOidcAsync("Google")
        };
        projections[1].Configuration.Should().BeEquivalentTo(projections[0].Configuration);
        projections[2].Configuration.Should().BeEquivalentTo(projections[0].Configuration);
        projections.Select(x => x.SecretIsRedacted).Should().OnlyContain(x => x);
        projections[0].Owner.Should().Be(SqlOSConfigurationOwners.Code);
        projections.Skip(1).Select(x => x.Owner).Should().OnlyContain(x => x == SqlOSConfigurationOwners.Dashboard);

        foreach (var harness in new[] { code, service, dashboard })
        {
            (await harness.Oidc.ListEnabledProvidersAsync()).Should().ContainSingle(x => x.DisplayName == "Google");
        }
    }

    [TestMethod]
    public async Task ScimConnection_CodeServiceAndDashboard_NormalizeAuthenticateAndRedactSecrets()
    {
        await using var code = await ControlPlaneParityHarness.CreateAsync();
        await using var service = await ControlPlaneParityHarness.CreateAsync();
        await using var dashboard = await ControlPlaneParityHarness.CreateAsync();
        var harnesses = new[] { code, service, dashboard };
        foreach (var harness in harnesses)
        {
            await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Parity Org", "parity"));
        }

        var codeOrg = await code.Context.Set<SqlOSOrganization>().SingleAsync();
        code.Options.SeedScimConnection("parity", seed =>
        {
            seed.OrganizationId = codeOrg.Id;
            seed.DisplayName = "Parity Directory";
            seed.Token = "scim_parity_code_token_0123456789abcdef";
        });
        await code.ReconcileStartupAsync();

        var serviceOrg = await service.Context.Set<SqlOSOrganization>().SingleAsync();
        var serviceCreated = await service.Admin.CreateScimConnectionAsync(new SqlOSCreateScimConnectionRequest(serviceOrg.Id, "Parity Directory"));

        var dashboardOrg = await dashboard.Context.Set<SqlOSOrganization>().SingleAsync();
        var dashboardCreated = await dashboard.PostDashboardAsync(
            DashboardAdminContracts.OrganizationScimConnections(dashboardOrg.Id),
            new { displayName = "Parity Directory", enabled = true });
        var dashboardToken = dashboardCreated.GetProperty("token").GetString();
        dashboardToken.Should().NotBeNullOrWhiteSpace("creation is the one-time reveal boundary");

        var codeProjection = await code.ProjectScimAsync(codeOrg.Id);
        var serviceProjection = await service.ProjectScimAsync(serviceOrg.Id);
        var dashboardProjection = await dashboard.ProjectScimAsync(dashboardOrg.Id);
        codeProjection.Configuration.Should().BeEquivalentTo(serviceProjection.Configuration);
        dashboardProjection.Configuration.Should().BeEquivalentTo(serviceProjection.Configuration);
        codeProjection.Owner.Should().Be(SqlOSConfigurationOwners.Code);
        serviceProjection.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        dashboardProjection.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        new[] { codeProjection, serviceProjection, dashboardProjection }.Should().OnlyContain(x => x.SecretIsRedacted && x.IsEnabled);

        await AssertScimAuthenticatesAsync(code, "scim_parity_code_token_0123456789abcdef");
        await AssertScimAuthenticatesAsync(service, serviceCreated.Token);
        await AssertScimAuthenticatesAsync(dashboard, dashboardToken!);

        var dashboardList = await dashboard.Client.GetAsync(DashboardAdminContracts.OrganizationScimConnections(dashboardOrg.Id));
        var dashboardListBody = await dashboardList.Content.ReadAsStringAsync();
        dashboardListBody.Should().NotContain(dashboardToken!);
        dashboardListBody.Should().NotContain("tokenHash", "later reads expose only redacted token metadata");
    }

    [TestMethod]
    public async Task MfaSettings_CodeServiceAndDashboard_NormalizeAndKeepOwnershipExplicit()
    {
        await using var code = await ControlPlaneParityHarness.CreateAsync(options => options.SeedMfaPolicy(ConfigureMfa));
        await using var service = await ControlPlaneParityHarness.CreateAsync();
        await using var dashboard = await ControlPlaneParityHarness.CreateAsync();
        await code.ReconcileStartupAsync();
        await service.Settings.UpdateMfaSettingsAsync(MfaRequest());
        await dashboard.PutDashboardAsync(DashboardAdminContracts.MfaSettings, MfaPayload());

        var codeProjection = await code.ProjectMfaAsync();
        var serviceProjection = await service.ProjectMfaAsync();
        var dashboardProjection = await dashboard.ProjectMfaAsync();
        codeProjection.Configuration.Should().BeEquivalentTo(serviceProjection.Configuration);
        dashboardProjection.Configuration.Should().BeEquivalentTo(serviceProjection.Configuration);
        codeProjection.Owner.Should().Be(SqlOSConfigurationOwners.Code);
        codeProjection.IsEditable.Should().BeFalse();
        serviceProjection.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        dashboardProjection.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);

        var codeAudit = await code.Context.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == "configuration.reconciled");
        codeAudit.DataJson.Should().NotContain("secret", "reconciliation diagnostics must remain redacted");
        (await code.Settings.GetMfaSettingsAsync()).RequireForOwnersAndAdmins.Should().BeTrue();
        (await service.Settings.GetMfaSettingsAsync()).RequireForOwnersAndAdmins.Should().BeTrue();
        (await dashboard.Settings.GetMfaSettingsAsync()).RequireForOwnersAndAdmins.Should().BeTrue();
    }

    [TestMethod]
    public async Task AuthPageColors_CodeServiceAndDashboard_NormalizeAndRejectEquivalently()
    {
        await using var code = await ControlPlaneParityHarness.CreateAsync(options => options.SeedAuthPage(ConfigureAuthPage));
        await using var service = await ControlPlaneParityHarness.CreateAsync();
        await using var dashboard = await ControlPlaneParityHarness.CreateAsync();
        await code.Settings.UpsertSeededAuthPageSettingsAsync();
        await service.Settings.UpdateAuthPageSettingsAsync(AuthPageRequest());
        await dashboard.PutDashboardAsync(DashboardAdminContracts.AuthPageSettings, AuthPagePayload());

        var codeProjection = await code.ProjectAuthPageAsync();
        var serviceProjection = await service.ProjectAuthPageAsync();
        var dashboardProjection = await dashboard.ProjectAuthPageAsync();
        codeProjection.Configuration.Should().BeEquivalentTo(serviceProjection.Configuration);
        dashboardProjection.Configuration.Should().BeEquivalentTo(serviceProjection.Configuration);
        codeProjection.Owner.Should().Be(SqlOSConfigurationOwners.Code);
        serviceProjection.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        dashboardProjection.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);

        foreach (var harness in new[] { code, service, dashboard })
        {
            using var login = await harness.Client.GetAsync("/sqlos/auth/login");
            login.EnsureSuccessStatusCode();
            var html = await login.Content.ReadAsStringAsync();
            html.Should().Contain("--primary: #4f46e5");
            html.Should().Contain("--accent: #111827");
            html.Should().Contain("--page-bg: #f5f3ff");
            html.Should().MatchRegex("<style nonce=\"[A-Za-z0-9_-]+\">");
            html.Should().MatchRegex("<script nonce=\"[A-Za-z0-9_-]+\">");
        }

        await using var invalidCode = await ControlPlaneParityHarness.CreateAsync(options => options.SeedAuthPage(page =>
        {
            ConfigureAuthPage(page);
            page.PrimaryColor = "</style><script>alert(1)</script>";
        }));
        var invalidSeed = () => invalidCode.Settings.UpsertSeededAuthPageSettingsAsync();
        (await invalidSeed.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("PrimaryColor");

        var serviceInvalid = () => service.Settings.UpdateAuthPageSettingsAsync(AuthPageRequest() with
        {
            PrimaryColor = "url(https://evil.example)"
        });
        (await serviceInvalid.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("PrimaryColor");

        using var dashboardInvalid = await dashboard.Client.PutAsJsonAsync(
            DashboardAdminContracts.AuthPageSettings,
            new
            {
                logoBase64 = (string?)null,
                pageTitle = "Sign in to Parity",
                pageSubtitle = "Parity workspace",
                primaryColor = "red;}</style><script>alert(1)</script>",
                accentColor = "#111827",
                backgroundColor = "#f5f3ff",
                layout = "split",
                enablePasswordSignup = true,
                enabledCredentialTypes = new[] { "password" }
            });
        dashboardInvalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await dashboardInvalid.Content.ReadAsStringAsync()).Should().Contain("PrimaryColor");
    }

    [TestMethod]
    public async Task InvalidDuplicateClient_HasEquivalentServiceAndDashboardErrorSemantics()
    {
        await using var code = await ControlPlaneParityHarness.CreateAsync();
        await using var service = await ControlPlaneParityHarness.CreateAsync();
        await using var dashboard = await ControlPlaneParityHarness.CreateAsync();
        await code.Admin.CreateClientAsync(ClientRequest());
        code.Options.SeedClient(seed =>
        {
            seed.ClientId = "parity-client";
            seed.Name = "Parity Client";
            seed.RedirectUris = [Callback];
        });
        await service.Admin.CreateClientAsync(ClientRequest());
        await dashboard.PostDashboardAsync(DashboardAdminContracts.Clients, ClientPayload());

        var codeDuplicate = () => code.ReconcileStartupAsync();
        (await codeDuplicate.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should()
            .Contain("owned by 'dashboard'", "code-first reconciliation reports the cross-owner conflict rather than adopting it");

        var serviceDuplicate = () => service.Admin.CreateClientAsync(ClientRequest());
        (await serviceDuplicate.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("already exists");

        using var dashboardDuplicate = await dashboard.Client.PostAsJsonAsync(DashboardAdminContracts.Clients, ClientPayload());
        dashboardDuplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await dashboardDuplicate.Content.ReadAsStringAsync()).Should().Contain("already exists");
    }

    [TestMethod]
    public void DashboardJavascript_UsesTheParityHarnessRoutesAndPayloadFields()
    {
        var root = FindRepositoryRoot();
        var javascript = File.ReadAllText(Path.Combine(root, "src", "SqlOS", "Dashboard", "wwwroot", "app.js"));
        AssertDashboardContract(
            Section(javascript, "async function renderAuthClients(route)", "async function renderAuthOidc()"),
            "`${authApiBasePath}/clients`",
            "`${authApiBasePath}/clients/${encodeURIComponent(clientDetail.id)}/credentials`",
            "clientId", "redirectUris", "allowedScopes", "confidential", "clientSecret");
        AssertDashboardContract(
            Section(javascript, "function buildOidcPayload(form)", "function renderStatsGroup"),
            "providerType", "clientSecret", "displayName");
        AssertDashboardContract(
            Section(javascript, "async function renderAuthOidc()", "async function renderAuthSso()"),
            "`${authApiBasePath}/oidc-connections`");
        AssertDashboardContract(
            Section(javascript, "function createPagerState(defaultPageSize, filterKey = \"\")", "async function render()"),
            "cursors: [null]",
            "pagerQuery(pager)",
            "nextCursor",
            "hasNextPage");
        javascript.Should().NotContain("page=1&pageSize=500");
        javascript.Should().NotContain("page=1&pageSize=100");
        AssertDashboardContract(
            Section(javascript, "async function renderAuthMfa()", "async function renderAuthPage()"),
            "`${authApiBasePath}/settings/mfa`",
            "totpEnabled", "requireForOwnersAndAdmins");
        AssertDashboardContract(
            Section(javascript, "async function renderAuthPage()", "async function renderAuthSessions()"),
            "`${authApiBasePath}/settings/auth-page`",
            "primaryColor", "accentColor", "backgroundColor",
            "#RGB", "#RRGGBB", "rgb()", "transparent");
        AssertDashboardContract(
            Section(javascript, "bindForm(\"create-scim-connection-form\"", "document.querySelectorAll(\".js-rotate-scim-token\")"),
            "`${authApiBasePath}/organizations/${organizationId}/scim-connections`",
            "displayName", "enabled");
    }

    private static void AssertDashboardContract(string section, params string[] expected)
    {
        foreach (var value in expected)
        {
            section.Should().Contain(value, $"the feature-specific dashboard contract must retain {value}");
        }
    }

    private static string Section(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the dashboard must retain the {startMarker} contract boundary");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"the dashboard must retain the {endMarker} contract boundary");
        return source[start..end];
    }

    [TestMethod]
    public async Task Harnesses_RunInParallelWithDeterministicIsolationAndCleanup()
    {
        var harnesses = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            ControlPlaneParityHarness.CreateAsync(options => options.SeedClient(seed =>
            {
                seed.ClientId = $"parallel-{index}";
                seed.Name = $"Parallel {index}";
                seed.RedirectUris = [$"https://parallel-{index}.example.test/callback"];
            }))));
        try
        {
            await Task.WhenAll(harnesses.Select(x => x.ReconcileStartupAsync()));
            var counts = await Task.WhenAll(harnesses.Select(x => x.Context.Set<SqlOSClientApplication>().CountAsync()));
            counts.Should().OnlyContain(count => count == 1, "each harness owns a unique in-memory store");
        }
        finally
        {
            foreach (var harness in harnesses) await harness.DisposeAsync();
        }
    }

    private static SqlOSCreateClientRequest ClientRequest()
        => new("parity-client", "Parity Client", "sqlos", [Callback], AllowedScopes: ["openid", "profile"]);

    private static SqlOSCreateClientRequest ConfidentialClientRequest()
        => new(
            "confidential-parity-client",
            "Confidential Parity Client",
            "sqlos",
            [Callback],
            AllowedScopes: ["openid", "profile"],
            ClientType: "confidential");

    private static object ClientPayload() => new
    {
        clientId = "parity-client", name = "Parity Client", audience = "sqlos", redirectUris = new[] { Callback },
        description = (string?)null, allowedScopes = new[] { "openid", "profile" }, requirePkce = true,
        isFirstParty = false, allowNativeHeadlessAuth = false, allowDeviceAuthorization = false, clientType = "public_pkce"
    };

    private static object ConfidentialClientPayload() => new
    {
        clientId = "confidential-parity-client",
        name = "Confidential Parity Client",
        audience = "sqlos",
        redirectUris = new[] { Callback },
        description = (string?)null,
        allowedScopes = new[] { "openid", "profile" },
        requirePkce = true,
        isFirstParty = false,
        allowNativeHeadlessAuth = false,
        allowDeviceAuthorization = false,
        clientType = "confidential"
    };

    private static async Task AssertClientAuthenticatesAsync(
        ControlPlaneParityHarness harness,
        string clientId,
        string secret)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}"))).ToString();
        var authenticated = await harness.ClientAuthentication.AuthenticateTokenEndpointClientAsync(
            new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["client_id"] = clientId
            }),
            context);
        authenticated.ClientId.Should().Be(clientId);
    }

    private static SqlOSCreateOidcConnectionRequest OidcRequest()
        => new(
            ProviderType: SqlOSOidcProviderType.Google,
            DisplayName: "Google",
            ClientId: "provider-client",
            ClientSecret: "provider-secret",
            AllowedCallbackUris: [Callback],
            UseDiscovery: true,
            DiscoveryUrl: null,
            Issuer: null,
            AuthorizationEndpoint: null,
            TokenEndpoint: null,
            UserInfoEndpoint: null,
            JwksUri: null,
            MicrosoftTenant: null,
            Scopes: null,
            ClaimMapping: null,
            ClientAuthMethod: null,
            UseUserInfo: null);

    private static object OidcPayload() => new
    {
        providerType = "Google", displayName = "Google", clientId = "provider-client", clientSecret = "provider-secret",
        allowedCallbackUris = new[] { Callback }, useDiscovery = true, discoveryUrl = (string?)null, issuer = (string?)null,
        authorizationEndpoint = (string?)null, tokenEndpoint = (string?)null, userInfoEndpoint = (string?)null,
        jwksUri = (string?)null, microsoftTenant = (string?)null, scopes = (string[]?)null, claimMapping = (object?)null,
        clientAuthMethod = (string?)null, useUserInfo = (bool?)null
    };

    private static void ConfigureAuthPage(SqlOSAuthPageSeedOptions page)
    {
        page.PageTitle = "Sign in to Parity";
        page.PageSubtitle = "Parity workspace";
        page.PrimaryColor = "#4F46E5";
        page.AccentColor = "#111827";
        page.BackgroundColor = "#F5F3FF";
        page.Layout = "split";
        page.EnablePasswordSignup = true;
        page.EnabledCredentialTypes = ["password"];
    }

    private static SqlOSUpdateAuthPageSettingsRequest AuthPageRequest()
        => new(
            null,
            "#4F46E5",
            "#111827",
            "#F5F3FF",
            "split",
            "Sign in to Parity",
            "Parity workspace",
            true,
            ["password"]);

    private static object AuthPagePayload() => new
    {
        logoBase64 = (string?)null,
        pageTitle = "Sign in to Parity",
        pageSubtitle = "Parity workspace",
        primaryColor = "#4F46E5",
        accentColor = "#111827",
        backgroundColor = "#F5F3FF",
        layout = "split",
        enablePasswordSignup = true,
        enabledCredentialTypes = new[] { "password" }
    };

    private static void ConfigureMfa(SqlOSMfaSeedOptions seed)
    {
        seed.Enabled = true;
        seed.TotpEnabled = true;
        seed.UserSelfEnrollmentEnabled = true;
        seed.RecoveryCodesEnabled = true;
        seed.RequireForOwnersAndAdmins = true;
        seed.RequiredRoles = ["owner", "admin"];
        seed.AvailableFactors = ["totp", "recovery_code"];
    }

    private static SqlOSUpdateMfaSettingsRequest MfaRequest()
        => new(true, true, true, true, false, true, ["owner", "admin"], ["totp", "recovery_code"]);

    private static object MfaPayload() => new
    {
        enabled = true, totpEnabled = true, userSelfEnrollmentEnabled = true, recoveryCodesEnabled = true,
        requireForAllUsers = false, requireForOwnersAndAdmins = true,
        requiredRoles = new[] { "owner", "admin" }, availableFactors = new[] { "totp", "recovery_code" }
    };

    private static async Task AssertScimAuthenticatesAsync(ControlPlaneParityHarness harness, string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";
        (await harness.Scim.AuthenticateAsync(context)).DisplayName.Should().Be("Parity Directory");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SqlOS.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
