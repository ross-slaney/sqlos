using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// Sessions after a direct login: the public-client refresh route
/// (<c>POST /sqlos/auth/token/refresh</c>, JSON, no client authentication), and sign-out by refresh
/// token (<c>POST /sqlos/auth/logout</c> for one session, <c>POST /sqlos/auth/logout-all</c> for
/// every session of the user). Access tokens are checked with the documented
/// <c>ValidateAccessTokenAsync</c> through the host's probe.
/// </summary>
[TestClass]
public sealed class PublicSessionScenarios
{
    private const string Client = BehaviorLockConstants.AppClientId;

    [Scenario]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task A_replay_inside_the_grace_window_returns_the_cached_pair_with_database_timestamps_CurrentBehavior_KnownDefect_325()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var login = await t.PasswordLoginAsync(alice);
        var first = login["tokens"]!["refreshToken"]!.GetValue<string>();

        var rotated = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = first }),
            "refresh: a new pair; its expiry times are UTC (Z)");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = first }),
            "replay the first refresh token inside the grace window: the cached pair, with expiry times read back from the database without a UTC marker (#325)");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = rotated.JsonString("refreshToken"), clientId = Client }),
            "the rotated token refreshes, naming its client");

        await t.ObserveAuditAsync("refresh writes no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task Refresh_refuses_unknown_tokens_other_clients_and_organizations_outside_the_session()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var outsider = await t.Setup.CreateOrganizationAsync("initech");
        await t.Setup.AddMembershipAsync(acme, alice);
        var login = await t.PasswordLoginAsync(alice, organizationId: acme.Id);
        var refreshToken = login["tokens"]!["refreshToken"]!.GetValue<string>();

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = "not-a-refresh-token" }),
            "an unknown refresh token escapes the endpoint");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken, clientId = "another-client" }),
            "a client id other than the session's escapes the endpoint");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken, organizationId = outsider.Id }),
            "refreshing into an organization Alice does not belong to is a lifecycle denial");
        var refreshed = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken }),
            "none of those consumed the token: it still refreshes");
        var ignored = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = refreshed.JsonString("refreshToken"), resource = "https://other.example.test" }),
            "this host declares no MCP resource, so resource indicators are off: a requested resource is ignored");
        await t.ObserveAuditAsync("only the lifecycle denial is audited");

        await t.RequireOrganizationMfaAsync(acme);
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = ignored.JsonString("refreshToken") }),
            "after the operator requires MFA for Acme, a password-only session cannot refresh into it");

        await t.ObserveAuditAsync("the MFA step-up refusal is not audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task With_resource_indicators_a_refresh_cannot_switch_to_another_resource()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Mcp);
        var alice = await t.Setup.CreateUserAsync("alice");

        var login = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId }),
            "direct login binds no resource: the token is for the client's audience");
        var refreshToken = login.JsonString("tokens.refreshToken");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken, resource = BehaviorLockConstants.McpAudience }),
            "declaring app.Mcp turns resource indicators on: asking for the MCP resource the session was not issued for escapes the endpoint");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken }),
            "the refusal did not consume the token: without a resource it refreshes");

        await t.ObserveAuditAsync("sign-in events; the refusal is not audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token/refresh")]
    [Covers("POST /__probe/auth/validate")]
    public async Task A_replay_outside_the_grace_window_revokes_the_whole_session()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.SetRefreshTokenGraceWindowAsync(0);
        var login = await t.PasswordLoginAsync(alice);
        var first = login["tokens"]!["refreshToken"]!.GetValue<string>();

        var rotated = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = first }),
            "refresh with no grace window configured");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = first }),
            "replaying the consumed token is treated as theft: the family is revoked");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = rotated.JsonString("refreshToken") }),
            "the legitimate rotated token now fails too");
        await t.ObserveAccessTokenValidationAsync(
            rotated.JsonString("accessToken"),
            "and its access token no longer validates (the session is revoked)");

        await t.ObserveAuditAsync("reuse revocation writes no audit event");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "the session is revoked for token reuse");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/token/refresh")]
    [Covers("POST /sqlos/auth/token")]
    public async Task The_public_refresh_route_refuses_a_confidential_clients_token()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.CreateClientAsync("web-portal", "Web Portal", isFirstParty: true, "https://web.example.test/signin-oidc", clientType: "confidential");
        var secret = await t.CreateClientSecretAsync("web-portal");

        var login = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = "web-portal" }),
            "direct login names the confidential first-party client; it does not authenticate the client");
        var refreshToken = login.JsonString("tokens.refreshToken");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken }),
            "the public refresh route carries no client credentials, so it refuses the confidential client's token");
        t.Observe(
            await t.Api.PostFormAsync(
                "/sqlos/auth/token",
                new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken },
                options => options.Header("Authorization", "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("web-portal:" + secret)))),
            "the refusal did not consume it: the token endpoint with the client's secret rotates it");

        await t.ObserveAuditAsync("the refused refresh is audited as a client authentication failure");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/token/refresh")]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation/preview")]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation")]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}")]
    public async Task Refreshing_into_another_organization_survives_revoking_that_organization_CurrentBehavior_KnownDefect_427()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var organizations = await t.CreateOrganizationsInIdOrderAsync("acme", "globex");
        var (acme, globex) = (organizations[0], organizations[1]);
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.Setup.AddMembershipAsync(globex, alice);

        var login = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client, organizationId = acme.Id }),
            "sign in to Acme");
        var switched = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = login.JsonString("tokens.refreshToken"), organizationId = globex.Id }),
            "refresh into Globex: the tokens carry Globex, the session still records Acme");
        var preview = t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sessions/revocation/preview", new { organizationId = globex.Id }),
            "the operator previews revoking Globex's sessions: none match");
        t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sessions/revocation", new
            {
                organizationId = globex.Id,
                confirm = true,
                reason = "incident",
                expectedMatchedSessions = preview.Json!["matchedSessions"]!.GetValue<int>()
            }),
            "executing it matches nothing to revoke");
        var survived = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = switched.JsonString("refreshToken"), organizationId = globex.Id }),
            "Alice keeps getting Globex tokens after the revocation (#427)");
        t.Observe(
            await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/organizations/{globex.Id}", new { name = globex.Name, slug = globex.Slug, isActive = false }),
            "the operator deactivates Globex, which revokes the sessions it finds for Globex");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = survived.JsonString("refreshToken"), organizationId = globex.Id }),
            "a Globex refresh is now a lifecycle denial");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = survived.JsonString("refreshToken"), organizationId = acme.Id }),
            "but the session itself was never revoked: it still refreshes into Acme");

        await t.ObserveAuditAsync("sign-in, revocation, deactivation, and denial events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "the session is still active");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task Refresh_is_refused_once_the_client_stops_admitting_the_user()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.CreateClientAsync("ops-console", "Ops Console", isFirstParty: true, "https://ops.example.test/callback");

        var login = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = "ops-console" }),
            "the ops console admits everyone, so Alice signs in");
        await t.ObserveAuditAsync("sign-in events");
        await t.SetApplicationAccessModeAsync("ops-console", "selected_users_groups_roles");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = login.JsonString("tokens.refreshToken") }),
            "after the operator restricts it to selected users, Alice's session cannot refresh");

        await t.ObserveAuditAsync("the refresh denial is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/logout")]
    [Covers("POST /sqlos/auth/token/refresh")]
    [Covers("POST /__probe/auth/validate")]
    public async Task Logout_revokes_one_session_and_answers_the_same_for_any_token()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var laptop = await t.PasswordLoginAsync(alice);
        var phone = await t.PasswordLoginAsync(alice);
        var laptopRefresh = laptop["tokens"]!["refreshToken"]!.GetValue<string>();

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/logout", new { refreshToken = laptopRefresh }),
            "sign the laptop out by its refresh token");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = laptopRefresh }),
            "the laptop's refresh token no longer works");
        await t.ObserveAccessTokenValidationAsync(
            laptop["tokens"]!["accessToken"]!.GetValue<string>(),
            "nor does its access token");
        await t.ObserveAccessTokenValidationAsync(
            phone["tokens"]!["accessToken"]!.GetValue<string>(),
            "the phone's session is untouched");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/logout", new { refreshToken = laptopRefresh }),
            "signing out again answers the same");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/logout", new { refreshToken = "not-a-refresh-token" }),
            "an unknown token answers the same");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/logout", new { }),
            "no token answers the same");
        t.Observe(
            await t.Api.SendAsync(HttpMethod.Post, "/sqlos/auth/logout", content: null),
            "no body at all escapes the endpoint");

        await t.ObserveAuditAsync("one logout event");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "one session revoked, one active");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/logout-all")]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task Logout_all_revokes_every_session_of_the_user()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var laptop = await t.PasswordLoginAsync(alice);
        var phone = await t.PasswordLoginAsync(alice);
        var laptopRefresh = laptop["tokens"]!["refreshToken"]!.GetValue<string>();

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/logout-all", new { refreshToken = "not-a-refresh-token" }),
            "an unknown token is unauthorized");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/logout-all", new { refreshToken = laptopRefresh }),
            "sign out everywhere with the laptop's refresh token");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = phone["tokens"]!["refreshToken"]!.GetValue<string>() }),
            "the phone's refresh token is revoked too");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/logout-all", new { refreshToken = laptopRefresh }),
            "a revoked token can no longer sign out everywhere");

        await t.ObserveAuditAsync("one logout-all event");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "every session is revoked");
        await t.ApproveAsync();
    }
}
