using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
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
        codeProjection.Configuration["scopes"].Should().Be("[\"openid\",\"profile\"]",
            "AllowedScopes is the shared allowlist; empty means deny-all at grant time, not a separate operator policy");
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
        var dashboardClient = await dashboard.Context.Set<SqlOSClientApplication>().SingleAsync();
        var codeOrdinaryDisable = () => code.Admin.DisableClientAsync(codeClient.Id, "parity_disable");
        await codeOrdinaryDisable.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owned by the 'code'*");
        await service.Admin.DisableClientAsync(serviceClient.Id, "parity_disable");
        await dashboard.PostDashboardAsync($"{DashboardAdminContracts.Clients}/{dashboardClient.Id}/disable", new { reason = "parity_disable" });
        (await code.ProjectClientAsync("parity-client")).IsEnabled.Should().BeTrue("ordinary disable is rejected for code-owned clients");
        (await service.ProjectClientAsync("parity-client")).IsEnabled.Should().BeFalse();
        (await dashboard.ProjectClientAsync("parity-client")).IsEnabled.Should().BeFalse();

        await code.PostDashboardAsync($"{DashboardAdminContracts.Clients}/{codeClient.Id}/emergency-disable", new { });
        new[] { await code.ProjectClientAsync("parity-client"), await service.ProjectClientAsync("parity-client"), await dashboard.ProjectClientAsync("parity-client") }
            .Should().OnlyContain(x => !x.IsEnabled);

        var codeOrdinaryEnable = () => code.Admin.EnableClientAsync(codeClient.Id);
        await codeOrdinaryEnable.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owned by the 'code'*");
        using var emergencyEnabled = await code.Client.PostAsync($"{DashboardAdminContracts.Clients}/{codeClient.Id}/emergency-enable", null);
        emergencyEnabled.EnsureSuccessStatusCode();
        await service.Admin.EnableClientAsync(serviceClient.Id);
        using var enabled = await dashboard.Client.PostAsync($"{DashboardAdminContracts.Clients}/{dashboardClient.Id}/enable", null);
        enabled.EnsureSuccessStatusCode();
        new[] { await code.ProjectClientAsync("parity-client"), await service.ProjectClientAsync("parity-client"), await dashboard.ProjectClientAsync("parity-client") }
            .Should().OnlyContain(x => x.IsEnabled);
    }

    [TestMethod]
    public async Task OidcCapableClient_CodeServiceAndDashboard_ProjectTheSameDiscoveryCapability()
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

        var mintedIdTokens = new List<(string Plane, string Audience, string[] ClaimNames)>();
        foreach (var (harness, plane) in new[] { (code, "code"), (service, "service"), (dashboard, "dashboard") })
        {
            var stored = await harness.Context.Set<SqlOSClientApplication>()
                .AsNoTracking()
                .SingleAsync(x => x.ClientId == "parity-client");
            var detail = await harness.Client.GetFromJsonAsync<JsonElement>(
                $"{DashboardAdminContracts.Clients}/{stored.Id}");
            detail.GetProperty("oidcCapable").GetBoolean()
                .Should().BeTrue("all three control planes share the same OIDC capability projection");
            detail.GetProperty("oidcDiscoveryUrl").GetString()
                .Should().Be("https://auth.parity.test/sqlos/auth/.well-known/openid-configuration");

            using var authorize = await harness.Client.GetAsync(
                "/sqlos/auth/authorize?client_id=parity-client&redirect_uri=https%3A%2F%2Fapp.parity.test%2Fcallback&response_type=code&scope=openid&state=parity&code_challenge=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&code_challenge_method=S256");
            authorize.StatusCode.Should().NotBe(HttpStatusCode.BadRequest, "an OIDC-capable client must pass real authorization-request validation on every control plane");

            mintedIdTokens.Add(await IssueCodeAndExchangeForIdTokenAsync(harness, plane));
        }

        // Redacted runtime comparison: audience and claim-name sets only — no
        // generated identifiers, timestamps, or token values.
        mintedIdTokens.Should().OnlyContain(
            x => x.Audience == "parity-client",
            "the ID token audience is the relying party's client_id on every control plane");
        mintedIdTokens[1].ClaimNames.Should().Equal(
            mintedIdTokens[0].ClaimNames,
            "service-created and code-seeded clients must mint equivalent ID tokens");
        mintedIdTokens[2].ClaimNames.Should().Equal(
            mintedIdTokens[0].ClaimNames,
            "dashboard-created and code-seeded clients must mint equivalent ID tokens");
    }

    /// <summary>
    /// Drives the defining OIDC runtime boundary for one control plane: a real
    /// authorization request, code issuance, and PKCE-bound exchange whose token
    /// response must carry an ID token.
    /// </summary>
    private static async Task<(string Plane, string Audience, string[] ClaimNames)> IssueCodeAndExchangeForIdTokenAsync(
        ControlPlaneParityHarness harness,
        string plane)
    {
        await harness.Crypto.EnsureActiveSigningKeyAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Parity User",
            "parity-user@example.test",
            "P@ssword123!"));
        var codeVerifier = harness.Crypto.GenerateOpaqueToken();

        var request = await harness.Authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
            "code",
            "parity-client",
            Callback,
            "parity-runtime",
            "openid profile email",
            harness.Crypto.CreatePkceCodeChallenge(codeVerifier),
            "S256",
            null,
            null,
            null,
            null,
            "hosted",
            null));

        var redirect = await harness.Authorization.IssueAuthorizationRedirectAsync(
            request,
            user,
            null,
            "password",
            new DefaultHttpContext());
        var authorizationCode = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();
        authorizationCode.Should().NotBeNullOrWhiteSpace(
            $"the {plane} control plane must issue a real authorization code");

        var exchanged = await harness.Authorization.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                authorizationCode,
                Callback,
                "parity-client",
                codeVerifier,
                null,
                null),
            new DefaultHttpContext());

        exchanged.Tokens.IdToken.Should().NotBeNullOrWhiteSpace(
            $"the {plane} control plane's OIDC-capable client must mint an ID token at the runtime boundary");
        var jwt = new JwtSecurityTokenHandler { MapInboundClaims = false }.ReadJwtToken(exchanged.Tokens.IdToken);
        return (plane, jwt.Audiences.Single(), jwt.Payload.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
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
    public async Task AuthPageAndEmailBranding_CodeServiceAndDashboard_NormalizeAndKeepOwnershipExplicit()
    {
        await using var code = await ControlPlaneParityHarness.CreateAsync(options =>
        {
            ConfigureAuthPage(options.SeedAuthPage);
            ConfigureAuthEmail(options.SeedAuthEmails);
        });
        await using var service = await ControlPlaneParityHarness.CreateAsync();
        await using var dashboard = await ControlPlaneParityHarness.CreateAsync();
        await code.ReconcileStartupAsync();
        await service.Settings.UpdateAuthPageSettingsAsync(AuthPageRequest());
        await service.Settings.UpdateAuthEmailBrandingSettingsAsync(AuthEmailRequest());
        await dashboard.PutDashboardAsync(DashboardAdminContracts.AuthPageSettings, AuthPagePayload());
        await dashboard.PutDashboardAsync(DashboardAdminContracts.AuthEmailSettings, AuthEmailPayload());

        var codePage = await code.ProjectAuthPageAsync();
        var servicePage = await service.ProjectAuthPageAsync();
        var dashboardPage = await dashboard.ProjectAuthPageAsync();
        codePage.Configuration.Should().BeEquivalentTo(servicePage.Configuration);
        dashboardPage.Configuration.Should().BeEquivalentTo(servicePage.Configuration);
        codePage.Owner.Should().Be(SqlOSConfigurationOwners.Code);
        codePage.IsEditable.Should().BeFalse();
        servicePage.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        dashboardPage.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);

        var codeEmail = await code.ProjectAuthEmailAsync();
        var serviceEmail = await service.ProjectAuthEmailAsync();
        var dashboardEmail = await dashboard.ProjectAuthEmailAsync();
        codeEmail.Configuration.Should().BeEquivalentTo(serviceEmail.Configuration);
        dashboardEmail.Configuration.Should().BeEquivalentTo(serviceEmail.Configuration);
        codeEmail.Owner.Should().Be(SqlOSConfigurationOwners.Code);
        serviceEmail.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        dashboardEmail.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);

        using var codeLogin = await code.Client.GetAsync("/sqlos/auth/login");
        using var serviceLogin = await service.Client.GetAsync("/sqlos/auth/login");
        using var dashboardLogin = await dashboard.Client.GetAsync("/sqlos/auth/login");
        foreach (var login in new[] { codeLogin, serviceLogin, dashboardLogin })
        {
            login.IsSuccessStatusCode.Should().BeTrue();
            (await login.Content.ReadAsStringAsync()).Should().Contain("#0f766e");
        }

        var codeMutate = () => code.Settings.UpdateAuthPageSettingsAsync(AuthPageRequest(pageTitle: "Dashboard overwrite"));
        await codeMutate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owned by the 'code'*");
    }

    [TestMethod]
    public async Task AuthPageColors_CodeServiceAndDashboard_NormalizeAndRejectEquivalently()
    {
        await using var code = await ControlPlaneParityHarness.CreateAsync(options => options.SeedAuthPage(ConfigureColorAuthPage));
        await using var service = await ControlPlaneParityHarness.CreateAsync();
        await using var dashboard = await ControlPlaneParityHarness.CreateAsync();
        await code.Settings.UpsertSeededAuthPageSettingsAsync();
        await service.Settings.UpdateAuthPageSettingsAsync(AuthPageColorRequest());
        await dashboard.PutDashboardAsync(DashboardAdminContracts.AuthPageSettings, AuthPageColorPayload());

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
            ConfigureColorAuthPage(page);
            page.PrimaryColor = "</style><script>alert(1)</script>";
        }));
        var invalidSeed = () => invalidCode.Settings.UpsertSeededAuthPageSettingsAsync();
        (await invalidSeed.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("PrimaryColor");

        var serviceInvalid = () => service.Settings.UpdateAuthPageSettingsAsync(AuthPageColorRequest() with
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
    public async Task MachineClient_CodeServiceAndDashboard_ShareOwnershipAndEmergencyDisableSemantics()
    {
        const string codeSecret = "code-machine-secret-with-at-least-256-bits-123456789";
        const string audience = "https://api.parity.test/jobs";
        await using var code = await ControlPlaneParityHarness.CreateAsync(options => options.SeedMachineClient("parity-worker", (client, machine) =>
        {
            client.Name = "Parity worker";
            client.Audience = audience;
            client.AllowedScopes = ["jobs.run"];
            machine.SecretResolver = () => codeSecret;
        }));
        await using var service = await ControlPlaneParityHarness.CreateAsync();
        await using var dashboard = await ControlPlaneParityHarness.CreateAsync();

        await code.ReconcileStartupAsync();
        var serviceCreated = await service.Machines.CreateAsync(new SqlOSCreateMachineClientRequest(
            "parity-worker", "Parity worker", null, audience, ["jobs.run"], null, null, []));
        var dashboardCreated = await dashboard.PostDashboardAsync(DashboardAdminContracts.MachineClients, new
        {
            clientId = "parity-worker",
            displayName = "Parity worker",
            description = (string?)null,
            audience,
            scopes = new[] { "jobs.run" },
            organizationId = (string?)null,
            expiresAt = (DateTime?)null,
            grants = Array.Empty<object>()
        });
        var dashboardSecret = dashboardCreated.GetProperty("clientSecret").GetString();

        var projections = new[]
        {
            await code.ProjectMachineClientAsync("parity-worker"),
            await service.ProjectMachineClientAsync("parity-worker"),
            await dashboard.ProjectMachineClientAsync("parity-worker")
        };
        projections[1].Configuration.Should().BeEquivalentTo(projections[0].Configuration);
        projections[2].Configuration.Should().BeEquivalentTo(projections[0].Configuration);
        projections[0].Owner.Should().Be(SqlOSConfigurationOwners.Code);
        projections[0].IsEditable.Should().BeFalse();
        projections.Skip(1).Select(x => x.Owner).Should().OnlyContain(x => x == SqlOSConfigurationOwners.Dashboard);
        projections.Should().OnlyContain(x => x.IsEnabled && x.SecretIsRedacted);

        await FluentActions.Invoking(() => code.Machines.RevokeAsync("parity-worker"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*code-owned*");
        using var dashboardRevokeCodeOwned = await code.Client.PostAsync(DashboardAdminContracts.MachineClientRevoke("parity-worker"), null);
        dashboardRevokeCodeOwned.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await dashboardRevokeCodeOwned.Content.ReadAsStringAsync()).Should().Contain("code-owned");
        (await code.ProjectMachineClientAsync("parity-worker")).IsEnabled.Should().BeTrue();

        var issued = await IssueMachineClientTokenAsync(code, "parity-worker", codeSecret, audience);
        await AssertMachineClientAuthenticatesAsync(code, "parity-worker", codeSecret, audience);
        await AssertMachineClientAuthenticatesAsync(service, "parity-worker", serviceCreated.ClientSecret, audience);
        await AssertMachineClientAuthenticatesAsync(dashboard, "parity-worker", dashboardSecret!, audience);

        await code.Machines.EmergencyDisableAsync("parity-worker");
        await service.Machines.EmergencyDisableAsync("parity-worker");
        await dashboard.PostDashboardAsync(DashboardAdminContracts.MachineClientEmergencyDisable("parity-worker"), new { });
        await code.ReconcileStartupAsync();

        var disabled = new[]
        {
            await code.ProjectMachineClientAsync("parity-worker"),
            await service.ProjectMachineClientAsync("parity-worker"),
            await dashboard.ProjectMachineClientAsync("parity-worker")
        };
        disabled.Should().OnlyContain(x => !x.IsEnabled && x.Configuration["emergencyDisabled"] == bool.TrueString);
        disabled[0].Owner.Should().Be(SqlOSConfigurationOwners.Code);
        await FluentActions.Invoking(() => AssertMachineClientAuthenticatesAsync(code, "parity-worker", codeSecret, audience))
            .Should().ThrowAsync<SqlOSClientCredentialsException>();
        (await code.Crypto.ValidateAccessTokenAsync(issued.AccessToken, audience)).Should().BeNull();

        await code.Machines.EmergencyEnableAsync("parity-worker");
        await service.Machines.EmergencyEnableAsync("parity-worker");
        using var enabled = await dashboard.Client.PostAsync(DashboardAdminContracts.MachineClientEmergencyEnable("parity-worker"), null);
        enabled.EnsureSuccessStatusCode();
        await AssertMachineClientAuthenticatesAsync(code, "parity-worker", codeSecret, audience);

        await service.Machines.RevokeAsync("parity-worker");
        using var dashboardRevoke = await dashboard.Client.PostAsync(DashboardAdminContracts.MachineClientRevoke("parity-worker"), null);
        dashboardRevoke.EnsureSuccessStatusCode();
        (await service.ProjectMachineClientAsync("parity-worker")).IsEnabled.Should().BeFalse();
        (await dashboard.ProjectMachineClientAsync("parity-worker")).IsEnabled.Should().BeFalse();
        await FluentActions.Invoking(() => service.Machines.EmergencyEnableAsync("parity-worker"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*revoked*");
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
            "`${authApiBasePath}/clients/${encodeURIComponent(clientId)}/emergency-disable`",
            "`${authApiBasePath}/clients/${encodeURIComponent(clientId)}/emergency-enable`",
            "clientId", "redirectUris", "allowedScopes", "confidential", "clientSecret",
            "emptyAllowlistWarning", "data-empty-allowlist-warning", "Empty allowlist grants nothing.",
            "omittedOpenIdWarning", "data-omitted-openid-warning", "Allowlist omits openid.",
            "oidcCapable", "oidcDiscoveryUrl", "OIDC capable", "OIDC discovery");
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
            Section(javascript, "async function renderAuthPage()", "async function revokeSessionsWithPreview(request)"),
            "`${authApiBasePath}/settings/auth-page`",
            "`${authApiBasePath}/settings/email`",
            "ownership.isEditable",
            "SeedAuthPage",
            "SeedAuthEmails",
            "primaryColor", "accentColor", "backgroundColor",
            "#RGB", "#RRGGBB", "rgb()", "transparent");
        AssertDashboardContract(
            Section(javascript, "bindForm(\"create-scim-connection-form\"", "document.querySelectorAll(\".js-rotate-scim-token\")"),
            "`${authApiBasePath}/organizations/${organizationId}/scim-connections`",
            "displayName", "enabled");
        AssertDashboardContract(
            Section(javascript, "async function renderAuthMachineClients()", "async function renderAuthOidc()"),
            "`${authApiBasePath}/machine-clients`",
            "`${authApiBasePath}/machine-clients/${encodeURIComponent(button.dataset.machineRevoke)}/revoke`",
            "`${authApiBasePath}/machine-clients/${encodeURIComponent(button.dataset.machineEmergencyDisable)}/emergency-disable`",
            "`${authApiBasePath}/machine-clients/${encodeURIComponent(button.dataset.machineEmergencyEnable)}/emergency-enable`",
            "configurationOwner",
            "emergencyDisabled");
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

    private static Task<SqlOSClientCredentialsTokenResult> IssueMachineClientTokenAsync(
        ControlPlaneParityHarness harness,
        string clientId,
        string secret,
        string audience)
        => harness.ClientCredentials.ExchangeAsync(
            clientId,
            secret,
            audience,
            "jobs.run",
            new DefaultHttpContext(),
            default);

    private static async Task AssertMachineClientAuthenticatesAsync(
        ControlPlaneParityHarness harness,
        string clientId,
        string secret,
        string audience)
    {
        var issued = await IssueMachineClientTokenAsync(harness, clientId, secret, audience);
        issued.AccessToken.Should().NotBeNullOrWhiteSpace();
        (await harness.Crypto.ValidateAccessTokenAsync(issued.AccessToken, audience)).Should().NotBeNull();
    }

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

    private static void ConfigureColorAuthPage(SqlOSAuthPageSeedOptions page)
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

    private static SqlOSUpdateAuthPageSettingsRequest AuthPageColorRequest()
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

    private static object AuthPageColorPayload() => new
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

    private static void ConfigureAuthPage(Func<Action<SqlOSAuthPageSeedOptions>, SqlOSAuthServerOptions> seed)
        => seed(page =>
        {
            page.PageTitle = "Parity Sign in";
            page.PageSubtitle = "Parity workspace";
            page.PrimaryColor = "#0f766e";
            page.AccentColor = "#111827";
            page.BackgroundColor = "#f5f3ff";
            page.Layout = "stacked";
            page.EnablePasswordSignup = true;
            page.EnabledCredentialTypes = ["password"];
        });

    private static void ConfigureAuthEmail(Func<Action<SqlOSAuthEmailSeedOptions>, SqlOSAuthServerOptions> seed)
        => seed(email =>
        {
            email.ApplicationName = "Parity Mail";
            email.PrimaryColor = "#0f766e";
            email.AccentColor = "#111827";
            email.BackgroundColor = "#f5f3ff";
        });

    private static SqlOSUpdateAuthPageSettingsRequest AuthPageRequest(string pageTitle = "Parity Sign in")
        => new(null, "#0f766e", "#111827", "#f5f3ff", "stacked", pageTitle, "Parity workspace", true, ["password"]);

    private static SqlOSUpdateAuthEmailBrandingSettingsRequest AuthEmailRequest()
        => new("Parity Mail", null, "#0f766e", "#111827", "#f5f3ff");

    private static object AuthPagePayload() => new
    {
        logoBase64 = (string?)null,
        primaryColor = "#0f766e",
        accentColor = "#111827",
        backgroundColor = "#f5f3ff",
        layout = "stacked",
        pageTitle = "Parity Sign in",
        pageSubtitle = "Parity workspace",
        enablePasswordSignup = true,
        enabledCredentialTypes = new[] { "password" }
    };

    private static object AuthEmailPayload() => new
    {
        applicationName = "Parity Mail",
        logoBase64 = (string?)null,
        primaryColor = "#0f766e",
        accentColor = "#111827",
        backgroundColor = "#f5f3ff"
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
