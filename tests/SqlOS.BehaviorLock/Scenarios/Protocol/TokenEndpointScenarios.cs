using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Protocol.ProtocolForms;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// <c>POST /sqlos/auth/token</c> for every grant it serves. Most failures answer the same opaque
/// <c>invalid_grant</c>; the <c>auth.public_error.mapped</c> audit event after each one records the
/// internal reason, which is how the transcripts tell the branches apart.
/// </summary>
[TestClass]
public sealed class TokenEndpointScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/token")]
    public async Task An_authorization_code_redeems_once_and_each_mismatch_is_an_opaque_invalid_grant()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice);
        var client = t.NewClient("client");
        var request = t.Urls.Authorize();
        var code = await CodeWithSessionAsync(t, request);
        var wrongVerifier = t.Urls.Authorize().CodeVerifier;
        t.Note("One code for every attempt: SqlOS consumes a code only after all of its checks pass.");

        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Form(("grant_type", "authorization_code"), ("client_id", request.ClientId), ("redirect_uri", request.RedirectUri), ("code_verifier", request.CodeVerifier))),
            "no code");
        await t.ObserveWithAuditAsync(await client.PostFormAsync("/sqlos/auth/token", request.TokenRequest("not-a-code")), "an unknown code");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Replace(request.TokenRequest(code), "redirect_uri", "https://sqlos.example.test/other-callback")),
            "a different redirect_uri");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Replace(request.TokenRequest(code), "code_verifier", wrongVerifier)),
            "another request's PKCE verifier");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Replace(request.TokenRequest(code), "client_id", "unknown-client")),
            "an unknown client fails client authentication before the code is examined");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", request.TokenRequest(code).Append(new KeyValuePair<string, string>("client_secret", "a-secret-the-public-client-never-had"))),
            "a public client that presents a secret fails client authentication");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", request.TokenRequest(code), options => options.Header("Authorization", Basic(request.ClientId, "a-secret-the-public-client-never-had"))),
            "so does a public client that presents an Authorization header");

        var redeemed = await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", request.TokenRequest(code, resource: BehaviorLockConstants.McpAudience)),
            "the code redeems; with resource indicators off (no app.Mcp) the resource parameter is ignored");
        await t.ObserveWithAuditAsync(await client.PostFormAsync("/sqlos/auth/token", request.TokenRequest(code)), "the same code again");
        t.ObserveTokens(
            await client.PostFormAsync("/sqlos/auth/token", Refresh(request.ClientId, redeemed.JsonString("refresh_token"))),
            "the replay revoked nothing: the first redemption's refresh token still works");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token")]
    public async Task With_resource_indicators_on_a_code_is_bound_to_the_resource_it_was_issued_for()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Mcp);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice);
        var client = t.NewClient("client");

        var bound = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["resource"] = BehaviorLockConstants.McpAudience });
        var boundCode = await CodeWithSessionAsync(t, bound);
        await t.ObserveWithAuditAsync(await client.PostFormAsync("/sqlos/auth/token", bound.TokenRequest(boundCode)), "a code issued for the MCP resource, redeemed without it");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", bound.TokenRequest(boundCode, resource: BehaviorLockConstants.ApiAudience)),
            "redeemed for another resource");
        var boundTokens = await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", bound.TokenRequest(boundCode, resource: BehaviorLockConstants.McpAudience)),
            "redeemed for its own resource: the access token's audience is the MCP resource");
        var appClient = BehaviorLockConstants.AppClientId;
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, boundTokens.JsonString("refresh_token"), ("resource", BehaviorLockConstants.ApiAudience))),
            "a refresh that names another resource");
        var refreshed = await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, boundTokens.JsonString("refresh_token"))),
            "a refresh that names none keeps the original resource");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, refreshed.JsonString("refresh_token"), ("resource", BehaviorLockConstants.McpAudience))),
            "a refresh that names the original resource");

        var unbound = t.Urls.Authorize();
        var unboundCode = await CodeWithSessionAsync(t, unbound);
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", unbound.TokenRequest(unboundCode, resource: BehaviorLockConstants.McpAudience)),
            "a code issued without a resource cannot gain one at the token endpoint");
        await t.ObserveWithAuditAsync(await client.PostFormAsync("/sqlos/auth/token", unbound.TokenRequest(unboundCode)), "without one it redeems for the client's default audience");

        await t.ApproveAsync();
    }

    private static IEnumerable<KeyValuePair<string, string>> Replace(IEnumerable<KeyValuePair<string, string>> form, string name, string value)
        => form.Select(field => field.Key == name ? new KeyValuePair<string, string>(name, value) : field);

    [Scenario]
    [Covers("POST /sqlos/auth/token")]
    public async Task Unsupported_grant_types_and_bodies()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var client = t.NewClient("client");

        await t.ObserveWithAuditAsync(await client.PostFormAsync("/sqlos/auth/token", Form(("client_id", BehaviorLockConstants.AppClientId))), "no grant_type");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Form(("grant_type", "password"), ("client_id", BehaviorLockConstants.AppClientId), ("username", "alice"), ("password", "secret"))),
            "the resource owner password grant");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Form(("grant_type", "AUTHORIZATION_CODE"), ("client_id", BehaviorLockConstants.AppClientId))),
            "grant_type is case-sensitive");
        await t.ObserveUnhandledAsync(
            async () => await client.PostJsonAsync("/sqlos/auth/token", new { grant_type = "authorization_code" }),
            "a JSON body instead of a form");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token")]
    public async Task Refresh_grant_rejections_name_their_reason_only_in_the_audit()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice);
        var client = t.NewClient("client");
        var appClient = BehaviorLockConstants.AppClientId;

        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Form(("grant_type", "refresh_token"), ("refresh_token", "not-a-refresh-token"))),
            "an unknown refresh token without client_id falls through to client authentication");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, "not-a-refresh-token")),
            "with the public client's client_id it is invalid_grant");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, "")),
            "an empty refresh token");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Refresh("another-client", session.RefreshToken)),
            "a client_id that did not receive the token");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, session.RefreshToken, ("client_secret", "a-secret-the-public-client-never-had"))),
            "a public client that presents a secret");
        var first = await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, session.RefreshToken, ("resource", BehaviorLockConstants.McpAudience))),
            "with resource indicators off (no app.Mcp) a resource parameter is ignored and the token rotates");
        var rotated = await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, first.JsonString("refresh_token"), ("scope", "openid"), ("organization_id", "org_0000000000000000"))),
            "scope and organization_id are ignored on this route: the full grant rotates again");

        t.Discard(await t.Api.PostJsonAsync("/sqlos/auth/logout", new { refreshToken = rotated.JsonString("refresh_token") }));
        await t.SkipAuditAsync();
        t.Note("Alice signs out with the rotated refresh token (the public logout API).");
        await t.ObserveWithAuditAsync(
            await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, rotated.JsonString("refresh_token"))),
            "a refresh token of a signed-out session");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_grace_window_replay_returns_the_cached_pair_even_when_its_refresh_token_was_rotated_again()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice);
        var client = t.NewClient("client");
        var appClient = BehaviorLockConstants.AppClientId;

        var second = t.ObserveTokens(await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, session.RefreshToken)), "rotate the first refresh token");
        var third = t.ObserveTokens(await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, second.JsonString("refresh_token"))), "rotate the second");
        t.ObserveTokens(await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, session.RefreshToken)), "replay the first inside the 30-second grace window: the second, already rotated, comes back");
        t.ObserveTokens(await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, second.JsonString("refresh_token"))), "replay the second: the third comes back");
        t.ObserveTokens(await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, third.JsonString("refresh_token"))), "the third still rotates normally");

        await t.ObserveAuditAsync("grace-window replays write no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /api/me")]
    public async Task With_the_grace_window_off_a_replayed_refresh_token_revokes_the_session()
    {
        await using var t = await Transcript.StartAsync(
            HostProfiles.Hosted,
            options => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.RefreshTokenGraceWindowSeconds = 0);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice);
        var client = t.NewClient("client");
        var appClient = BehaviorLockConstants.AppClientId;
        t.Note("RefreshTokenGraceWindowSeconds = 0: SqlOS copies it into its settings at first start.");

        var rotated = t.ObserveTokens(await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, session.RefreshToken)), "rotate the refresh token");
        await t.ObserveWithAuditAsync(await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, session.RefreshToken)), "replay the rotated one: reuse detected");
        await t.ObserveWithAuditAsync(await client.PostFormAsync("/sqlos/auth/token", Refresh(appClient, rotated.JsonString("refresh_token"))), "the legitimate successor dies with the family");
        t.Observe(
            await client.GetAsync("/api/me", options => options.Bearer(rotated.JsonString("access_token"))),
            "and so does the successor's unexpired access token");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "Alice's session is revoked for refresh-token reuse");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_confidential_web_client_authenticates_with_client_secret_post()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize(AtlasClients.Confidential, AtlasClients.ConfidentialRedirectUri, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var code = await CodeWithPasswordAsync(t, alice, request, t.Browser);
        var web = t.NewClient("web-backend");
        IEnumerable<KeyValuePair<string, string>> WithSecret(IEnumerable<KeyValuePair<string, string>> form, string secret)
            => form.Append(new KeyValuePair<string, string>("client_secret", secret));

        await t.ObserveWithAuditAsync(
            await web.PostFormAsync("/sqlos/auth/token", Replace(request.TokenRequest(code), "client_id", AtlasClients.Portal)),
            "the code presented by another (public) client: authentication passes, the code is not that client's");
        await t.ObserveWithAuditAsync(await web.PostFormAsync("/sqlos/auth/token", request.TokenRequest(code)), "no secret");
        await t.ObserveWithAuditAsync(await web.PostFormAsync("/sqlos/auth/token", WithSecret(request.TokenRequest(code), "a-wrong-secret-that-is-long-enough-to-be-checked-0000")), "a wrong secret");
        await t.ObserveWithAuditAsync(
            await web.PostFormAsync("/sqlos/auth/token", request.TokenRequest(code), options => options.Header("Authorization", Basic(AtlasClients.Confidential, AtlasClients.ConfidentialSecret))),
            "the right secret with HTTP Basic, which this client did not register");
        var tokens = await t.ObserveWithAuditAsync(
            await web.PostFormAsync("/sqlos/auth/token", WithSecret(request.TokenRequest(code), AtlasClients.ConfidentialSecret)),
            "client_secret_post, as registered");

        var refreshToken = tokens.JsonString("refresh_token");
        await t.ObserveWithAuditAsync(await web.PostFormAsync("/sqlos/auth/token", Refresh(AtlasClients.Confidential, refreshToken)), "refresh without the secret");
        await t.ObserveWithAuditAsync(
            await web.PostFormAsync("/sqlos/auth/token", Refresh(AtlasClients.Confidential, refreshToken, ("client_secret", AtlasClients.ConfidentialSecret))),
            "refresh with the secret");
        await t.ObserveWithAuditAsync(
            await web.PostFormAsync("/sqlos/auth/token", Form(("grant_type", "client_credentials"), ("client_id", AtlasClients.Confidential), ("client_secret", AtlasClients.ConfidentialSecret), ("resource", BehaviorLockConstants.ResourceApiAudience))),
            "a client_secret_post client authenticates but may not use client_credentials");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /resource-api/me")]
    [Covers("GET /sqlos/auth/userinfo")]
    public async Task A_machine_client_gets_an_access_token_with_client_credentials()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var worker = t.NewClient("worker");
        var basic = Basic(AtlasClients.Worker, AtlasClients.WorkerSecret);
        IEnumerable<KeyValuePair<string, string>> Grant(params (string Name, string Value)[] extra)
            => Form(("grant_type", "client_credentials")).Concat(Form(extra));

        var issued = await t.ObserveWithAuditAsync(
            await worker.PostFormAsync("/sqlos/auth/token", Grant(("resource", BehaviorLockConstants.ResourceApiAudience), ("scope", BehaviorLockAuthorization.ReadPermission)), options => options.Header("Authorization", basic)),
            "HTTP Basic, the client's audience as resource, an allowed scope");
        await t.ObserveWithAuditAsync(
            await worker.PostFormAsync("/sqlos/auth/token", Grant(("scope", BehaviorLockAuthorization.ReadPermission)), options => options.Header("Authorization", basic)),
            "no resource: the machine grant requires the client's audience");
        await t.ObserveWithAuditAsync(
            await worker.PostFormAsync("/sqlos/auth/token", Grant(("resource", BehaviorLockConstants.BillingAudience)), options => options.Header("Authorization", basic)),
            "another audience");
        await t.ObserveWithAuditAsync(
            await worker.PostFormAsync("/sqlos/auth/token", Grant(("resource", BehaviorLockConstants.ResourceApiAudience), ("scope", "workspace.write admin")), options => options.Header("Authorization", basic)),
            "scopes outside the allowlist are dropped, not refused");
        await t.ObserveWithAuditAsync(
            await worker.PostFormAsync("/sqlos/auth/token", Grant(("resource", BehaviorLockConstants.ResourceApiAudience)), options => options.Header("Authorization", Basic(AtlasClients.Worker, "a-wrong-secret-that-is-long-enough-to-be-checked-0000"))),
            "a wrong secret");
        await t.ObserveWithAuditAsync(
            await worker.PostFormAsync("/sqlos/auth/token", Grant(("client_id", AtlasClients.Worker), ("client_secret", AtlasClients.WorkerSecret), ("resource", BehaviorLockConstants.ResourceApiAudience))),
            "the right secret in the form body, which this client did not register");
        await t.ObserveWithAuditAsync(
            await worker.PostFormAsync("/sqlos/auth/token", Grant(("client_id", AtlasClients.Portal), ("resource", BehaviorLockConstants.ResourceApiAudience))),
            "a public client asking for client_credentials");

        var token = issued.JsonString("access_token");
        t.Observe(await worker.GetAsync("/resource-api/me", options => options.Bearer(token)), "the separate resource API accepts the machine token");
        t.Observe(await worker.GetAsync("/sqlos/auth/userinfo", options => options.Bearer(token)), "UserInfo has no user for a machine token");

        await t.ApproveAsync();
    }
}
