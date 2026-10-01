using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// The host's own routes locked with ASP.NET authorization: the same-process <c>SqlOS</c> scheme on
/// <c>/api</c>, the <c>SqlOS.Mcp</c> policy on <c>/mcp</c>, a second same-process audience with
/// <c>AddSqlOSJwt</c>, and a separate resource API validating with <c>AddJwtBearer</c> against the
/// JWKS. SqlOS decides each challenge; the host decides which routes are locked.
/// </summary>
[TestClass]
public sealed class ResourceRouteScenarios
{
    [Scenario]
    [Covers("GET /api/me")]
    public async Task The_first_party_api_accepts_its_own_audience_and_challenges_every_other_bearer()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice);
        var api = t.NewClient("api-client");
        // Name the ID token for what it is: it first appears in an Authorization header, which
        // the renderer would otherwise label as an access token.
        t.Scrub(session.IdToken!, "id-token");

        t.Observe(await api.GetAsync("/api/me"), "no Authorization header: 401 with the SqlOS Bearer challenge and the resource metadata URL");
        t.Observe(await api.GetAsync("/api/me", options => options.Header("Authorization", "Basic YWxpY2U6c2VjcmV0")), "a non-Bearer credential counts as no token");
        t.Observe(await api.GetAsync("/api/me", options => options.Bearer("not-a-jwt")), "a malformed bearer token is invalid_token");
        t.Observe(await api.GetAsync("/api/me", options => options.Bearer(session.IdToken!)), "the ID token is not an access token for the API");
        t.Observe(await api.GetAsync("/api/me", options => options.Bearer(session.AccessToken)), "the access token minted for the API: the route reads the validated token");

        t.Discard(await t.Api.PostJsonAsync("/sqlos/auth/logout", new { refreshToken = session.RefreshToken }));
        await t.SkipAuditAsync();
        t.Note("Alice signs out with her refresh token (the public logout API). The SqlOS scheme looks the session up on every request.");
        t.Observe(await api.GetAsync("/api/me", options => options.Bearer(session.AccessToken)), "the same unexpired access token is rejected once its session is revoked");

        await t.ObserveAuditAsync("resource access writes no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /mcp")]
    [Covers("GET /api/me")]
    public async Task The_mcp_route_accepts_only_tokens_minted_for_the_mcp_resource()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Mcp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var apiSession = await t.Setup.SignInWithPasswordAsync(alice);
        var mcpRequest = t.Urls.Authorize(extra: new Dictionary<string, string?>
        {
            ["view"] = "password",
            ["resource"] = BehaviorLockConstants.McpAudience
        });
        var mcpSession = await ProtocolForms.SignInWithPasswordAsync(t, alice, mcpRequest, t.NewBrowser("mcp-host-browser"), BehaviorLockConstants.McpAudience);
        var mcp = t.NewClient("mcp-client");

        t.Observe(await mcp.PostJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }), "no token: 401 with the MCP realm and the MCP resource metadata URL");
        t.Observe(
            await mcp.PostJsonAsync("/mcp", new { jsonrpc = "2.0", id = 2, method = "tools/list" }, options => options.Bearer(apiSession.AccessToken)),
            "a token minted for /api is refused by the MCP policy");
        t.Observe(
            await mcp.PostJsonAsync("/mcp", new { jsonrpc = "2.0", id = 3, method = "tools/list" }, options => options.Bearer(mcpSession.AccessToken)),
            "a token minted for the MCP resource (resource indicator on /authorize) is accepted");
        t.Observe(await mcp.GetAsync("/api/me", options => options.Bearer(mcpSession.AccessToken)), "the MCP token does not open the first-party API");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /resource-api/me")]
    [Covers("GET /billing/me")]
    public async Task A_separate_resource_api_trusts_the_jwks_until_expiry_while_a_same_process_audience_checks_the_session()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/clients", new
        {
            clientId = "atlas-billing",
            name = "Atlas Billing",
            audience = BehaviorLockConstants.BillingAudience,
            redirectUris = new[] { "https://billing.example.test/callback" },
            allowedScopes = new[] { "openid", "profile", "email", "offline_access" },
            isFirstParty = true
        });
        var portal = await t.Setup.SignInWithPasswordAsync(
            alice,
            t.Urls.Authorize("atlas-portal", "https://portal.example.test/auth/callback", extra: new Dictionary<string, string?> { ["view"] = "password" }));
        var billing = await t.Setup.SignInWithPasswordAsync(
            alice,
            t.Urls.Authorize("atlas-billing", "https://billing.example.test/callback", extra: new Dictionary<string, string?> { ["view"] = "password" }),
            t.NewBrowser("billing-browser"));
        var api = t.NewClient("resource-client");

        t.Observe(await api.GetAsync("/resource-api/me"), "AddJwtBearer challenges a request without a token");
        t.Observe(await api.GetAsync("/resource-api/me", options => options.Bearer(portal.AccessToken)), "AddJwtBearer validates the portal token against discovery and the JWKS");
        t.Observe(await api.GetAsync("/billing/me"), "AddSqlOSJwt(\"Billing\") challenges with its own realm and no resource metadata");
        t.Observe(await api.GetAsync("/billing/me", options => options.Bearer(portal.AccessToken)), "the portal token is for another audience");
        t.Observe(await api.GetAsync("/billing/me", options => options.Bearer(billing.AccessToken)), "a token minted for the billing client's audience is accepted");

        t.Discard(await t.Api.PostJsonAsync("/__probe/auth/logout-all", new { userId = alice.Id }));
        t.Note("Every session of Alice is revoked (SqlOSAuthService.LogoutAllAsync).");
        t.Observe(await api.GetAsync("/resource-api/me", options => options.Bearer(portal.AccessToken)), "the JWKS-validated API still accepts the unexpired token: revoke-at-exp");
        t.Observe(await api.GetAsync("/billing/me", options => options.Bearer(billing.AccessToken)), "the same-process scheme looks the session up and rejects the revoked token");

        await t.ApproveAsync();
    }
}
