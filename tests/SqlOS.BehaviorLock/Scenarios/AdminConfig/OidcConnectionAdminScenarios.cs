using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// The social and custom OIDC connection admin API (<c>/sqlos/admin/auth/api/oidc-connections</c>):
/// dashboard-owned create, edit, and enable/disable, per-provider normalization, the validation
/// and ownership branches, and cursor paging. The enabled-provider list the sign-in surfaces read
/// (<c>SqlOSOidcAuthService.ListEnabledProvidersAsync</c>, through its probe) shows the runtime effect.
/// </summary>
[TestClass]
public sealed class OidcConnectionAdminScenarios
{
    private const string ConnectionsRoute = "/sqlos/admin/auth/api/oidc-connections";
    private const string ProvidersProbe = "/__probe/auth/providers";

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/oidc-connections")]
    [Covers("POST /sqlos/admin/auth/api/oidc-connections")]
    [Covers("PUT /sqlos/admin/auth/api/oidc-connections/{connectionId}")]
    [Covers("POST /sqlos/admin/auth/api/oidc-connections/{connectionId}/disable")]
    [Covers("POST /sqlos/admin/auth/api/oidc-connections/{connectionId}/enable")]
    public async Task Operator_creates_edits_and_toggles_a_dashboard_owned_google_connection()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);
        var probe = t.NewClient("probe");

        t.Observe(
            await t.Operator.GetAsync(ConnectionsRoute, options => options.WithoutCredentials()),
            "without operator credentials the connection list is not found");
        t.Observe(await t.Operator.GetAsync(ConnectionsRoute), "no connections are configured yet");

        var created = t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, new
            {
                providerType = "google",
                displayName = "Google Workspace",
                clientId = "  google-admin-client  ",
                clientSecret = "google-admin-secret",
                allowedCallbackUris = new[] { BehaviorLockConstants.SocialCallbackUri },
                useDiscovery = false,
                scopes = new[] { "profile", "openid", "email", "PROFILE" },
                trustUpstreamMfa = true,
                acceptedAmrValues = new[] { "mfa", " hwk ", "mfa" }
            }),
            "create a Google connection: the client secret is stored but never returned");
        var connectionId = created.JsonString("id");

        t.Observe(await probe.GetAsync(ProvidersProbe), "the sign-in surfaces now offer Google");

        t.Observe(
            await t.Operator.PutJsonAsync($"{ConnectionsRoute}/{connectionId}", new
            {
                displayName = "Google (staff)",
                clientId = "google-admin-client",
                allowedCallbackUris = new[]
                {
                    BehaviorLockConstants.SocialCallbackUri + "/{connectionId}",
                    BehaviorLockConstants.SocialCallbackUri,
                    BehaviorLockConstants.SocialCallbackUri
                },
                useDiscovery = false,
                scopes = new[] { "openid", "email" },
                clientAuthMethod = "clientsecretbasic",
                trustUpstreamMfa = false
            }),
            "edit it without resending the secret: callbacks are deduplicated, sorted, and {connectionId} is expanded");

        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/disable", new { }),
            "disable the connection");
        t.Observe(await probe.GetAsync(ProvidersProbe), "a disabled connection is not offered for sign-in");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/enable", new { }),
            "enable it again");
        t.Observe(await t.Operator.GetAsync(ConnectionsRoute), "the dashboard-owned connection as the list reads it");

        await t.ObserveAuditAsync("OIDC connection changes write no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/oidc-connections")]
    [Covers("GET /sqlos/admin/auth/api/oidc-connections")]
    public async Task Each_provider_type_is_normalized_when_an_operator_adds_it()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);

        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, new
            {
                providerType = "Microsoft",
                displayName = "Microsoft Entra",
                clientId = "entra-client",
                clientSecret = "entra-secret",
                allowedCallbackUris = new[] { BehaviorLockConstants.SocialCallbackUri },
                useDiscovery = false,
                microsoftTenant = " contoso.example ",
                discoveryUrl = "https://ignored.example.test/.well-known/openid-configuration"
            }),
            "Microsoft: discovery is forced and derived from the tenant");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, new
            {
                providerType = "GitHub",
                displayName = "GitHub",
                clientId = "github-client",
                clientSecret = "github-secret",
                allowedCallbackUris = new[] { BehaviorLockConstants.SocialCallbackUri },
                useDiscovery = true,
                useUserInfo = false
            }),
            "GitHub: an OAuth profile provider with fixed endpoints, default scopes, and its own claim mapping");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, new
            {
                providerType = "Apple",
                displayName = "Apple",
                clientId = "com.example.behavior-lock",
                allowedCallbackUris = new[] { BehaviorLockConstants.SocialCallbackUri },
                useDiscovery = false,
                appleTeamId = " TEAM123456 ",
                appleKeyId = "KEY1234567",
                applePrivateKeyPem = "-----BEGIN PRIVATE KEY-----\r\nbehavior-lock-apple-key\r\n-----END PRIVATE KEY-----",
                clientAuthMethod = "ClientSecretBasic"
            }),
            "Apple: a team ID, key ID, and private key replace the client secret; the key is never returned");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, new
            {
                providerType = "Custom",
                displayName = "Acme SSO",
                clientId = "acme-client",
                clientSecret = "acme-secret",
                allowedCallbackUris = new[] { BehaviorLockConstants.SocialCallbackUri },
                useDiscovery = true,
                discoveryUrl = " https://sso.acme.example.test/.well-known/openid-configuration ",
                logoDataUrl = "data:image/png;base64,iVBORw0KGgo=",
                claimMapping = new { subjectClaim = " oid ", emailClaim = "", displayNameClaim = "display_name" }
            }),
            "Custom with discovery: the claim mapping keeps defaults for blank claims and a custom logo overrides the built-in one");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, new
            {
                providerType = "Custom",
                displayName = "Beta Manual",
                clientId = "beta-client",
                clientSecret = "beta-secret",
                allowedCallbackUris = new[] { BehaviorLockConstants.SocialCallbackUri },
                useDiscovery = false,
                issuer = "https://beta.example.test",
                authorizationEndpoint = "https://beta.example.test/authorize",
                tokenEndpoint = "https://beta.example.test/token",
                userInfoEndpoint = " ",
                jwksUri = "https://beta.example.test/jwks",
                clientAuthMethod = "ClientSecretBasic",
                useUserInfo = false,
                acceptedAcrValues = new[] { "urn:beta:acr:mfa" }
            }),
            "Custom with manual endpoints");
        t.Observe(await t.Operator.GetAsync(ConnectionsRoute), "the list orders connections by display name");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/oidc-connections")]
    [Covers("PUT /sqlos/admin/auth/api/oidc-connections/{connectionId}")]
    [Covers("POST /sqlos/admin/auth/api/oidc-connections/{connectionId}/enable")]
    [Covers("POST /sqlos/admin/auth/api/oidc-connections/{connectionId}/disable")]
    public async Task Invalid_oidc_connection_changes_are_rejected()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);
        var google = await t.Setup.OperatorPostAsync(ConnectionsRoute, new
        {
            providerType = "Google",
            displayName = "Google",
            clientId = "google-client",
            clientSecret = "google-secret",
            allowedCallbackUris = new[] { BehaviorLockConstants.SocialCallbackUri },
            useDiscovery = true
        });
        var googleId = google.JsonString("id");

        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection("Okta")),
            "an unknown provider type is a bad request");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection("Custom", discoveryUrl: "https://sso.example.test/.well-known/openid-configuration", clientAuthMethod: "private_key_jwt")),
            "an unsupported client authentication method is a bad request");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, "{\"providerType\":"),
            "a malformed body is rejected by request binding");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection("Google")),
            "a second Google connection escapes the endpoint as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection("Microsoft", callbacks: [" ", ""])),
            "blank callback URIs escape as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection("Microsoft", clientSecret: " ")),
            "a social connection without a client secret escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection("Apple", clientSecret: null, appleTeamId: "TEAM123456", appleKeyId: "KEY1234567")),
            "Apple without a private key escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection("Custom")),
            "custom discovery without a discovery URL escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection(
                "Custom",
                useDiscovery: false,
                issuer: "https://manual.example.test",
                authorizationEndpoint: "https://manual.example.test/authorize",
                jwksUri: "https://manual.example.test/jwks")),
            "manual endpoints without a token endpoint escape as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection(
                "Custom",
                discoveryUrl: "https://sso.example.test/.well-known/openid-configuration",
                acceptedAmrValues: Enumerable.Range(1, 33).Select(index => $"amr-{index}").ToArray())),
            "more than 32 trusted upstream MFA values escape as a server error");

        t.Observe(
            await t.Operator.PutJsonAsync($"{ConnectionsRoute}/{googleId}", Update(clientAuthMethod: "tls_client_auth")),
            "an edit with an unsupported client authentication method is a bad request");
        t.Observe(
            await t.Operator.PutJsonAsync($"{ConnectionsRoute}/oidc_00000000000000000000000000000000", Update()),
            "editing an unknown connection escapes as a server error");
        t.Observe(
            await t.Operator.PutJsonAsync($"{ConnectionsRoute}/{googleId}", Update(callbacks: [])),
            "an edit that removes every callback URI escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/oidc_00000000000000000000000000000000/enable", new { }),
            "enabling an unknown connection escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/oidc_00000000000000000000000000000000/disable", new { }),
            "disabling an unknown connection escapes as a server error");

        await t.ObserveStateAsync(ConnectionsRoute, "only the original Google connection exists, unchanged");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/oidc-connections")]
    [Covers("PUT /sqlos/admin/auth/api/oidc-connections/{connectionId}")]
    [Covers("POST /sqlos/admin/auth/api/oidc-connections/{connectionId}/disable")]
    [Covers("POST /sqlos/admin/auth/api/oidc-connections/{connectionId}/enable")]
    public async Task A_code_owned_connection_can_be_disabled_in_an_emergency_but_not_edited()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        var probe = t.NewClient("probe");

        var list = t.Observe(await t.Operator.GetAsync(ConnectionsRoute), "startup seeded Google and Microsoft as code-owned connections");
        var googleId = list.JsonString("data.0.id");

        t.Observe(
            await t.Operator.PutJsonAsync($"{ConnectionsRoute}/{googleId}", Update(displayName: "Renamed in the dashboard")),
            "editing a code-owned connection escapes as a server error that names its owner");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{googleId}/disable", new { }),
            "an operator can still disable it");
        t.Observe(await probe.GetAsync(ProvidersProbe), "sign-in no longer offers Google");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{googleId}/enable", new { }),
            "and enable it again");
        t.Observe(await probe.GetAsync(ProvidersProbe), "Google is offered again");

        await t.ObserveAuditAsync("toggling a code-owned connection writes no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/oidc-connections")]
    [Covers("GET /sqlos/admin/email/api/templates")]
    public async Task Oidc_connection_lists_page_with_opaque_cursors()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Hosted);

        var first = t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}?pageSize=2&page=1"), "the first page of two, in display-name order");
        var cursor = first.JsonString("nextCursor");
        t.Observe(
            await t.Operator.GetAsync($"{ConnectionsRoute}?pageSize=2&cursor={Uri.EscapeDataString(cursor)}"),
            "the cursor continues after the last row");
        t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}?pageSize=0"), "a page size below one is raised to one");
        t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}?page=2"), "offset paging beyond the first page is refused");
        t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}?cursor=not-a-cursor"), "a malformed cursor is refused");

        var templates = t.Observe(
            await t.Operator.GetAsync("/sqlos/admin/email/api/templates?pageSize=1"),
            "a cursor from another admin list");
        t.Observe(
            await t.Operator.GetAsync($"{ConnectionsRoute}?cursor={Uri.EscapeDataString(templates.JsonString("nextCursor"))}"),
            "is refused by the connection list");

        await t.ApproveAsync();
    }

    private static object Connection(
        string providerType,
        string[]? callbacks = null,
        string? clientSecret = "connection-secret",
        bool useDiscovery = true,
        string? discoveryUrl = null,
        string? issuer = null,
        string? authorizationEndpoint = null,
        string? tokenEndpoint = null,
        string? jwksUri = null,
        string? clientAuthMethod = null,
        string? appleTeamId = null,
        string? appleKeyId = null,
        string[]? acceptedAmrValues = null)
        => new
        {
            providerType,
            displayName = $"{providerType} connection",
            clientId = $"{providerType.ToLowerInvariant()}-client",
            clientSecret,
            allowedCallbackUris = callbacks ?? [BehaviorLockConstants.SocialCallbackUri],
            useDiscovery,
            discoveryUrl,
            issuer,
            authorizationEndpoint,
            tokenEndpoint,
            jwksUri,
            clientAuthMethod,
            appleTeamId,
            appleKeyId,
            acceptedAmrValues
        };

    private static object Update(string displayName = "Google", string[]? callbacks = null, string? clientAuthMethod = null)
        => new
        {
            displayName,
            clientId = "google-client",
            allowedCallbackUris = callbacks ?? [BehaviorLockConstants.SocialCallbackUri],
            useDiscovery = true,
            clientAuthMethod
        };
}
