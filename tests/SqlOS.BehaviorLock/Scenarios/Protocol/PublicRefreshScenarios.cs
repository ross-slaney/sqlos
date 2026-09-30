using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Protocol.ProtocolForms;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// <c>POST /sqlos/auth/token/refresh</c>, the JSON refresh API for first-party backends: its own
/// response shape, the only route that switches a session's organization, and its unhandled
/// failures.
/// </summary>
[TestClass]
public sealed class PublicRefreshScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task The_public_refresh_api_rotates_tokens_in_its_own_json_shape()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice);
        var backend = t.NewClient("app-backend");

        var rotated = t.ObserveTokens(
            await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = session.RefreshToken }),
            "camelCase tokens with the session, client, and expiry times, and no cache headers");
        t.ObserveTokens(
            await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = rotated.JsonString("refreshToken"), clientId = BehaviorLockConstants.AppClientId }),
            "clientId may name the token's client");
        await t.ObserveUnhandledAsync(
            async () => await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = "not-a-refresh-token" }),
            "an unknown refresh token");
        await t.ObserveUnhandledAsync(
            async () => await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { }),
            "no refresh token");
        await t.ObserveUnhandledAsync(
            async () => await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = rotated.JsonString("refreshToken"), clientId = "another-client" }),
            "a clientId that did not receive the token");
        t.Observe(
            await backend.SendAsync(HttpMethod.Post, "/sqlos/auth/token/refresh", new StringContent("{\"refreshToken\":", Encoding.UTF8, "application/json")),
            "malformed JSON is refused by the framework");
        t.Observe(
            await backend.SendAsync(HttpMethod.Post, "/sqlos/auth/token/refresh", new StringContent("refreshToken=abc", Encoding.UTF8, "application/x-www-form-urlencoded")),
            "a form body is not JSON");

        await t.ObserveAuditAsync("events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task A_grace_window_replay_on_the_public_refresh_api_returns_expiry_times_without_a_utc_marker_CurrentBehavior_KnownDefect_325()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice);
        var backend = t.NewClient("app-backend");

        t.ObserveTokens(
            await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = session.RefreshToken }),
            "a rotation computes its expiry times in memory: they carry Z");
        t.ObserveTokens(
            await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = session.RefreshToken }),
            "known defect #325: the grace-window replay reads the cached pair back from the database, and the same expiry times lose their UTC marker");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task The_public_refresh_api_refuses_a_confidential_clients_token_without_a_challenge()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize(AtlasClients.Confidential, AtlasClients.ConfidentialRedirectUri, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var code = await CodeWithPasswordAsync(t, alice, request, t.Browser);
        var tokens = t.Discard(await t.Api.PostFormAsync(
            "/sqlos/auth/token",
            request.TokenRequest(code).Append(new KeyValuePair<string, string>("client_secret", AtlasClients.ConfidentialSecret))));
        await t.SkipAuditAsync();
        var backend = t.NewClient("web-backend");

        await t.ObserveWithAuditAsync(
            await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = tokens.JsonString("refresh_token") }),
            "the confidential web client's refresh token: 401 invalid_client, with no WWW-Authenticate");
        await t.ObserveWithAuditAsync(
            await backend.PostJsonAsync(
                "/sqlos/auth/token/refresh",
                new { refreshToken = tokens.JsonString("refresh_token"), clientId = AtlasClients.Confidential },
                options => options.Header("Authorization", Basic(AtlasClients.Confidential, AtlasClients.ConfidentialSecret))),
            "credentials are not read on this route, so the client cannot authenticate");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token/refresh")]
    [Covers("GET /api/me")]
    public async Task Refreshing_into_another_organization_switches_the_access_token_but_not_the_session()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var (alice, acme, globex, session) = await SignInToAcmeAsync(t);
        var initech = await t.Setup.CreateOrganizationAsync("initech");
        var backend = t.NewClient("app-backend");

        var toGlobex = t.ObserveTokens(
            await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = session.RefreshToken, organizationId = globex.Id }),
            "refresh into Globex, where Alice is an admin");
        t.ObserveResource(
            await backend.GetAsync("/api/me", options => options.Bearer(toGlobex.JsonString("accessToken"))),
            "the API sees Globex");
        var back = t.ObserveTokens(
            await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = toGlobex.JsonString("refreshToken") }),
            "the next refresh without organizationId returns to Acme: the session never moved");
        await t.ObserveUnhandledAsync(
            async () => await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = back.JsonString("refreshToken"), organizationId = initech.Id }),
            "an organization Alice does not belong to");

        await t.ObserveAuditAsync("events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "Alice's one session still belongs to Acme");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token/refresh")]
    [Covers("GET /api/me")]
    public async Task Revoking_an_organization_misses_sessions_refreshed_into_it_CurrentBehavior_KnownDefect_427()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var (alice, acme, globex, session) = await SignInToAcmeAsync(t);
        var backend = t.NewClient("app-backend");
        var toGlobex = t.ObserveTokens(
            await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = session.RefreshToken, organizationId = globex.Id }),
            "Alice's session, started in Acme, refreshes into Globex");

        t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sessions/revocation/preview", new { organizationId = globex.Id, reason = "tenant incident" }),
            "known defect #427: revoking Globex's sessions matches none");
        t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sessions/revocation", new { organizationId = globex.Id, reason = "tenant incident", confirm = true, expectedMatchedSessions = 0 }),
            "confirming it finds nothing to revoke");
        t.ObserveResource(
            await backend.GetAsync("/api/me", options => options.Bearer(toGlobex.JsonString("accessToken"))),
            "known defect #427: the Globex access token still opens the API");
        t.ObserveTokens(
            await backend.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = toGlobex.JsonString("refreshToken"), organizationId = globex.Id }),
            "known defect #427: and the session keeps refreshing into Globex");

        await t.ObserveAuditAsync("events");
        await t.ApproveAsync();
    }

    /// <summary>Alice, a member of Acme and an admin of Globex, signs in on the hosted page and chooses Acme.</summary>
    private static async Task<(ScenarioUser Alice, ScenarioOrganization Acme, ScenarioOrganization Globex, SignedInSession Session)> SignInToAcmeAsync(Transcript t)
    {
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.Setup.AddMembershipAsync(globex, alice, "admin");
        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Discard(await t.GetAsync(request.Url));
        var chooser = t.Discard(await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)));
        var chosen = t.Discard(await t.SubmitAsync(chooser.Form("/login/select-organization").With("organizationId", acme.Id)));
        var tokens = t.Discard(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(chosen.NextUrlParameter("code"))));
        if (tokens.StatusCode != 200)
        {
            throw new InvalidOperationException($"Signing Alice in to Acme failed: {tokens.Describe()} {tokens.Preview()}");
        }

        await t.SkipAuditAsync();
        t.Note("Alice (member of Acme, admin of Globex) signed in on the hosted page and chose Acme.");
        return (alice, acme, globex, new SignedInSession(
            tokens.JsonString("access_token"),
            tokens.JsonString("refresh_token"),
            tokens.Json?["id_token"]?.GetValue<string>(),
            request));
    }
}
