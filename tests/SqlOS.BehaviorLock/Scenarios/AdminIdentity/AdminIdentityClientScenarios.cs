using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminIdentity;

/// <summary>
/// OAuth client administration: registering clients through the admin API, listing and reading
/// them (including code-owned seeds and dynamically registered clients), the ordinary and
/// emergency lifecycle switches, session revocation, and what an operator-registered client may do
/// at the authorization server.
/// </summary>
[TestClass]
public sealed class AdminIdentityClientScenarios
{
    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/clients")]
    [Covers("GET /sqlos/admin/auth/api/clients")]
    [Covers("GET /sqlos/admin/auth/api/clients/{clientId}")]
    public async Task Operator_registers_public_confidential_and_device_clients_and_reads_them_back()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var clients = AdminIdentity.Api + "/clients";

        var portal = t.Observe(
            await t.Operator.PostJsonAsync(clients, new
            {
                clientId = "portal",
                name = "Portal",
                audience = "https://portal.example.test/api",
                redirectUris = new[] { "https://portal.example.test/callback" },
                allowedScopes = new[] { "openid", "profile", "email", "offline_access" },
                isFirstParty = true
            }),
            "register a first-party public PKCE client");
        t.Observe(
            await t.Operator.PostJsonAsync(clients, new
            {
                clientId = "web",
                name = "Web",
                description = "  Server-rendered web app  ",
                audience = "https://web.example.test/api",
                redirectUris = new[] { "https://web.example.test/signin" },
                allowedScopes = new[] { "openid", "profile" },
                clientType = "confidential",
                requirePkce = false
            }),
            "a confidential client authenticates with client_secret_basic");
        t.Observe(
            await t.Operator.PostJsonAsync(clients, new
            {
                clientId = "cli",
                name = "CLI",
                audience = "https://cli.example.test/api",
                redirectUris = Array.Empty<string>(),
                allowDeviceAuthorization = true,
                clientType = "public_cli"
            }),
            "a device-flow client needs no redirect URI");
        t.Observe(
            await t.Operator.PostJsonAsync(clients, new
            {
                clientId = "  minimal  ",
                name = "  Minimal  ",
                redirectUris = new[] { "https://minimal.example.test/cb", "https://minimal.example.test/cb", "  " }
            }),
            "identifiers are trimmed, blank and duplicate redirect URIs dropped, and the audience defaults");
        t.Observe(
            await t.Operator.PostJsonAsync(clients, new
            {
                clientId = "loose",
                name = "Loose",
                redirectUris = new[] { "javascript:alert(1)", "http://insecure.example.test/callback" },
                allowNativeHeadlessAuth = true
            }),
            "redirect URIs are not validated beyond being present");
        await t.ObserveAuditAsync("client registration writes no audit event");

        t.Observe(await t.Operator.GetAsync(clients), "clients by name, with the first-page summary counts");
        t.Observe(await t.Operator.GetAsync(clients + "?source=seeded"), "the code-seeded application client");
        t.Observe(await t.Operator.GetAsync(clients + "?source=manual&search=CLI"), "operator-registered clients matching a search");
        t.Observe(await t.Operator.GetAsync(clients + "?search=seeded"), "the source label is searchable as a keyword");
        t.Observe(await t.Operator.GetAsync(clients + "?status=disabled"), "no client is disabled");
        var page = t.Observe(await t.Operator.GetAsync(clients + "?pageSize=2"), "two to a page");
        t.Observe(
            await t.Operator.GetAsync(clients + "?pageSize=2&cursor=" + Uri.EscapeDataString(page.JsonString("nextCursor"))),
            "a later page carries no summary");
        t.Observe(await t.Operator.GetAsync($"{clients}/{portal.JsonString("id")}"), "a client read by its internal ID");
        t.Observe(await t.Operator.GetAsync($"{clients}/{BehaviorLockConstants.AppClientId}"), "the seeded client read by its client ID");
        t.Observe(await t.Operator.GetAsync(clients + "/cli_missing"), "an unknown client is not found");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/clients")]
    public async Task Client_registration_rejects_incomplete_or_conflicting_definitions()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var clients = AdminIdentity.Api + "/clients";
        var redirect = new[] { "https://portal.example.test/callback" };
        await t.Setup.OperatorPostAsync(clients, new { clientId = "portal", name = "Portal", redirectUris = redirect });

        t.Observe(await t.Operator.PostJsonAsync(clients, new { name = "No ID", redirectUris = redirect }), "a client ID is required");
        t.Observe(await t.Operator.PostJsonAsync(clients, new { clientId = "nameless", redirectUris = redirect }), "a name is required");
        t.Observe(
            await t.Operator.PostJsonAsync(clients, new { clientId = "spa", name = "SPA", redirectUris = redirect, clientType = "spa" }),
            "an unknown client type is refused");
        t.Observe(await t.Operator.PostJsonAsync(clients, new { clientId = "nowhere", name = "Nowhere" }), "a client without redirect URIs or device flow is refused");
        t.Observe(
            await t.Operator.PostJsonAsync(clients, new { clientId = "tv", name = "TV", clientType = "confidential", allowDeviceAuthorization = true }),
            "a confidential device client is refused");
        t.Observe(await t.Operator.PostJsonAsync(clients, new { clientId = "portal", name = "Portal Two", redirectUris = redirect }), "a taken client ID is refused");
        t.Observe(
            await t.Operator.PostJsonAsync(clients, new { clientId = BehaviorLockConstants.AppClientId, name = "Shadow", redirectUris = redirect }),
            "so is the seeded application's client ID");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/revoke")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/disable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/enable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/emergency-disable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/emergency-enable")]
    [Covers("GET /sqlos/admin/auth/api/clients")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task Operator_revokes_sessions_and_disables_an_operator_registered_client()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var portalId = await AdminIdentity.CreateClientAsync(t, new
        {
            clientId = "portal",
            name = "Portal",
            audience = "https://portal.example.test/api",
            redirectUris = new[] { "https://portal.example.test/callback" },
            allowedScopes = new[] { "openid", "profile", "email", "offline_access" },
            isFirstParty = true
        });
        var client = $"{AdminIdentity.Api}/clients/{portalId}";
        var aliceSession = await AdminIdentity.PasswordLoginAsync(t, alice, clientId: "portal");
        await AdminIdentity.PasswordLoginAsync(t, bob, clientId: "portal");

        t.Observe(await t.Operator.PostJsonAsync(client + "/revoke", new { reason = "  incident review  " }), "revoke every session of the client");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", AdminIdentity.Refresh(aliceSession.RefreshToken, "portal")),
            "alice's refresh token is dead");
        t.Observe(await t.Operator.PostJsonAsync(client + "/revoke", new { }), "revoking again finds nothing and uses the default reason");
        await t.ObserveAuditAsync("session revocation is audited each time");

        var fresh = await AdminIdentity.PasswordLoginAsync(t, alice, clientId: "portal");
        t.Observe(await t.Operator.PostJsonAsync(client + "/disable", new { reason = "  Compromised  " }), "disable the client with a reason");
        t.Observe(await t.Operator.PostJsonAsync(client + "/disable", new { reason = "again" }), "disabling it again changes nothing");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", AdminIdentity.Refresh(fresh.RefreshToken, "portal")),
            "a refresh for the disabled client is refused as invalid_client");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = "portal" }),
            "a disabled client cannot sign anyone in");
        t.Observe(await t.Operator.GetAsync(AdminIdentity.Api + "/clients?status=disabled"), "the disabled client in the disabled filter");
        t.Observe(await t.Operator.PostJsonAsync(client + "/emergency-disable", new { }), "emergency switches are only for code-owned clients");
        t.Observe(await t.Operator.PostJsonAsync(client + "/emergency-enable", new { }), "in both directions");
        t.Observe(await t.Operator.PostJsonAsync(client + "/enable", new { }), "enable the client");
        t.Observe(await t.Operator.PostJsonAsync(client + "/enable", new { }), "enabling an enabled client succeeds again");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = "portal" }),
            "the client signs users in again");
        await t.ObserveAuditAsync("disable and each enable are audited");

        foreach (var action in new[] { "revoke", "disable", "enable", "emergency-disable", "emergency-enable" })
        {
            t.Observe(
                await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/clients/cli_missing/{action}", new { }),
                $"{action} on an unknown client is refused");
        }

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/disable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/enable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/emergency-disable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/emergency-enable")]
    [Covers("GET /sqlos/admin/auth/api/clients/{clientId}")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task A_code_owned_client_takes_only_the_emergency_switches()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var alice = await t.Setup.CreateUserAsync("alice");
        var client = $"{AdminIdentity.Api}/clients/{BehaviorLockConstants.AppClientId}";

        // Read before any sign-in: the detail embeds recent audit events as raw JSON strings, and
        // password-login events carry resetScopes in an order SqlOS does not define.
        t.Observe(await t.Operator.GetAsync(client), "the seeded client's detail: code-owned, managed by the startup seed, with its reconciliation event");
        var session = await AdminIdentity.PasswordLoginAsync(t, alice);
        t.Observe(await t.Operator.PostJsonAsync(client + "/disable", new { reason = "no" }), "the seeded client refuses the ordinary disable");
        t.Observe(await t.Operator.PostJsonAsync(client + "/enable", new { }), "and the ordinary enable");
        t.Observe(await t.Operator.PostJsonAsync(client + "/emergency-enable", new { }), "emergency enable of an active client is a no-op");
        t.Observe(await t.Operator.PostJsonAsync(client + "/emergency-disable", new { }), "emergency disable takes it down");
        t.Observe(await t.Operator.PostJsonAsync(client + "/emergency-disable", new { }), "a second emergency disable changes nothing");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", AdminIdentity.Refresh(session.RefreshToken)),
            "its sessions were revoked");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId }),
            "and nobody can sign in through it");
        t.Observe(await t.Operator.PostJsonAsync(client + "/enable", new { }), "the ordinary enable still refuses");
        t.Observe(await t.Operator.PostJsonAsync(client + "/emergency-enable", new { }), "emergency enable restores it");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId }),
            "sign-in works again");
        await t.ObserveAuditAsync("the emergency switches are audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/clients/{clientId}")]
    [Covers("GET /sqlos/admin/auth/api/clients")]
    public async Task Without_the_openid_provider_role_no_client_is_openid_capable()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.OAuthOnly);

        t.Observe(
            await t.Operator.GetAsync($"{AdminIdentity.Api}/clients/{BehaviorLockConstants.AppClientId}"),
            "the application client is not OpenID-capable and has no discovery URL");
        t.Observe(await t.Operator.GetAsync(AdminIdentity.Api + "/clients?search=behavior"), "the list agrees");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/clients")]
    [Covers("GET /sqlos/admin/auth/api/clients/{clientId}")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/disable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/emergency-disable")]
    public async Task Dynamically_registered_clients_are_listed_as_registered_with_duplicate_counts()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);
        // Same software, version, and redirect URIs (the duplicate fingerprint); different names,
        // so the list order (by name, then random ID) is stable.
        Dictionary<string, object> Registration(string name) => new()
        {
            ["client_name"] = name,
            ["redirect_uris"] = new[] { "http://127.0.0.1/callback/taskrail" },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
            ["software_id"] = "taskrail-desktop",
            ["software_version"] = "4.2.0"
        };
        var registrar = t.NewClient("mcp-client");
        var first = t.Discard(await registrar.PostJsonAsync("/sqlos/auth/register", Registration("Taskrail Desktop (laptop)"))).JsonString("client_id");
        t.Discard(await registrar.PostJsonAsync("/sqlos/auth/register", Registration("Taskrail Desktop (workstation)")));
        await t.SkipAuditAsync();
        var clients = AdminIdentity.Api + "/clients";

        t.Observe(await t.Operator.GetAsync(clients + "?source=registered"), "two identical registrations are flagged as duplicates");
        t.Observe(await t.Operator.GetAsync(clients + "?search=registered"), "the registered keyword finds them too");
        t.Observe(await t.Operator.GetAsync($"{clients}/{first}"), "a registered client's detail keeps its metadata");
        t.Observe(await t.Operator.PostJsonAsync($"{clients}/{first}/emergency-disable", new { }), "a dynamic client is not code-owned");
        t.Observe(await t.Operator.PostJsonAsync($"{clients}/{first}/disable", new { }), "the ordinary disable applies to it");
        t.Observe(await t.Operator.GetAsync(clients + "?source=registered&status=active"), "one active registration remains");
        await t.ObserveAuditAsync("the disable is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/clients")]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task An_operator_registered_third_party_client_cannot_skip_consent_through_direct_login()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var alice = await t.Setup.CreateUserAsync("alice");
        var browser = t.NewBrowser("alice-browser");
        await AdminIdentity.SignInAsync(t, alice, browser: browser);

        t.Note("The 7.2.1 fix for #419: direct-login routes refuse clients that are not first party, so they cannot skip the consent screen.");
        t.Observe(
            await t.Operator.PostJsonAsync(AdminIdentity.Api + "/clients", new
            {
                clientId = "partner",
                name = "Partner",
                audience = "https://partner.example.test/api",
                redirectUris = new[] { "https://partner.example.test/callback" },
                allowedScopes = new[] { "openid", "profile", "email", "offline_access" }
            }),
            "register a client that is not first party (the default)");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = "partner" }),
            "direct password login for it is refused");
        var silent = t.Urls.Authorize("partner", "https://partner.example.test/callback", extra: new Dictionary<string, string?> { ["prompt"] = "none" });
        t.Observe(await browser.GetAsync(silent.Url), "a silent authorization from a signed-in browser needs consent");
        await t.ObserveAuditAsync("audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/clients")]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("POST /__probe/auth/validate")]
    public async Task An_operator_registered_client_obtains_a_token_for_another_resource_CurrentBehavior_KnownDefect_429()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Mcp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var probe = t.NewClient("probe");

        t.Note("Known defect #429, recorded as it behaves today: /authorize accepts any resource, so a client registered for the MCP audience receives a token for the first-party API audience.");
        t.Observe(
            await t.Operator.PostJsonAsync(AdminIdentity.Api + "/clients", new
            {
                clientId = "mcp-tool",
                name = "MCP Tool",
                audience = BehaviorLockConstants.McpAudience,
                redirectUris = new[] { "https://tool.example.test/callback" },
                allowedScopes = new[] { "openid", "profile", "email", "offline_access" },
                isFirstParty = true
            }),
            "register a client for the MCP resource");
        var request = t.Urls.Authorize("mcp-tool", "https://tool.example.test/callback", extra: new Dictionary<string, string?>
        {
            ["view"] = "password",
            ["resource"] = BehaviorLockConstants.ApiAudience
        });
        var browser = t.NewBrowser("alice-browser");
        var page = t.Observe(await browser.GetAsync(request.Url), "/authorize accepts resource set to the first-party API");
        var login = t.Observe(
            await browser.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "sign in");
        var token = t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(login.NextUrlParameter("code"), BehaviorLockConstants.ApiAudience)),
            "the token's audience is the API, not the client's MCP audience");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/validate", new { token = token.JsonString("access_token"), audience = BehaviorLockConstants.ApiAudience }),
            "the API's SqlOS validation accepts it");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/validate", new { token = token.JsonString("access_token"), audience = BehaviorLockConstants.McpAudience }),
            "while the MCP audience the client was registered for does not");
        await t.ApproveAsync();
    }
}
