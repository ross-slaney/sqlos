using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// The explicit-wiring deployment (<c>AddSqlOS&lt;T&gt;(options)</c> plus a leftover
/// <c>app.MapAuthServer()</c>). The hosting reference calls that leftover call safe and idempotent,
/// but in 7.2.1 it makes SqlOS withdraw its whole core route set, and <c>MapAuthServer()</c> maps
/// only the auth server and its admin API back: the audit-log, transactional-email, and calendar
/// admin APIs and the calendar connect callback are gone. Not filed yet; recorded as it behaves.
/// The auth server it maps back still signs users in for the client it seeds with
/// <c>SeedBrowserClient</c>, which allows no scopes unless the host lists them.
/// </summary>
[TestClass]
public sealed class LegacyWiringScenarios
{
    private const string LegacyClientId = "legacy-web";
    private const string LegacyRedirectUri = "https://legacy.example.test/callback";

    [Scenario]
    [Covers("GET /sqlos/auth/.well-known/openid-configuration")]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/auth/userinfo")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/sessions")]
    public async Task The_auth_server_a_host_maps_itself_signs_a_user_in_to_its_seeded_browser_client()
    {
        // No audit observations: this deployment does not map the audit-log admin API.
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.LegacyHost);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(await t.Api.GetAsync("/sqlos/auth/.well-known/openid-configuration"), "discovery describes the auth server the host mapped");
        var request = t.Urls.Authorize(LegacyClientId, LegacyRedirectUri, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Observe(await t.GetAsync(request.Url), "the seeded browser client opens the hosted password page");
        var login = t.Observe(
            await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "the password signs Alice in and returns her to the client with a code");
        // SeedBrowserClient leaves AllowedScopes empty, so the grant keeps none of the requested scopes.
        var tokens = t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(login.NextUrlParameter("code"))),
            "the client redeems the code: the seeded client allows no scopes, so the grant drops them and issues no ID token");
        t.Observe(
            await t.Api.GetAsync("/sqlos/auth/userinfo", options => options.Bearer(tokens.JsonString("access_token"))),
            "UserInfo refuses the access token, which was not granted openid");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "the session the sign-in created");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/stats")]
    public async Task A_manual_MapAuthServer_call_withdraws_the_module_admin_apis_CurrentBehavior_KnownDefect_Unfiled()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.LegacyHost);

        t.Observe(await t.Operator.GetAsync("/sqlos/admin/auth/api/stats"), "the auth admin API that MapAuthServer maps answers");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/audit/api/events"), "the audit-log admin API is gone");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/audit/api/events/export.csv"), "and so is the audit export");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/email/api/templates"), "the email template admin API is gone");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/calendar/api/summary"), "the calendar admin API is gone");
        t.Observe(await t.GetAsync("/sqlos/auth/calendar/callback?state=unknown"), "the calendar connect callback is gone");

        await t.ApproveAsync();
    }
}
