using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// The session list and the preview-then-confirm session revocation API (the incident-response
/// tool): selectors, operation IDs and replays, every guard, the runtime effect on tokens, and the
/// two reach gaps of #427 recorded as they behave in 7.2.1: the browser's issuer session and
/// unredeemed codes survive a revocation by user, and a revocation by organization misses a
/// session that switched into that organization on refresh.
/// </summary>
[TestClass]
public sealed class SessionRevocationAdminScenarios
{
    private const string SessionsRoute = "/sqlos/admin/auth/api/sessions";
    private const string PreviewRoute = "/sqlos/admin/auth/api/sessions/revocation/preview";
    private const string RevocationRoute = "/sqlos/admin/auth/api/sessions/revocation";

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/sessions")]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation/preview")]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation")]
    public async Task Operator_previews_and_revokes_a_users_sessions()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var aliceSession = await t.Setup.SignInWithPasswordAsync(alice);
        var bobSession = await t.Setup.SignInWithPasswordAsync(bob, browser: t.NewBrowser("bob-browser"));

        t.Observe(
            await t.Operator.GetAsync(SessionsRoute, options => options.WithoutCredentials()),
            "without operator credentials the session list is not found");
        t.Observe(
            await t.Operator.PostJsonAsync(PreviewRoute, new { userId = alice.Id }, options => options.WithoutCredentials()),
            "nor can a revocation be previewed");
        t.Observe(await t.Operator.GetAsync(SessionsRoute), "two active sessions, newest first");
        t.Observe(
            await t.Operator.PostJsonAsync(PreviewRoute, new { clientApplicationId = BehaviorLockConstants.AppClientId }),
            "a client selector matches every session of the application");
        t.Observe(
            await t.Operator.PostJsonAsync(PreviewRoute, new { userId = alice.Id }),
            "preview Alice's sessions: nothing is revoked yet");
        t.Observe(
            await t.Operator.PostJsonAsync(RevocationRoute, new
            {
                userId = alice.Id,
                reason = " stolen laptop ",
                operationId = "incident-42",
                confirm = true,
                expectedMatchedSessions = 1
            }),
            "confirm the revocation with the previewed count");
        t.Observe(
            await t.Operator.PostJsonAsync(RevocationRoute, new
            {
                userId = alice.Id,
                reason = " stolen laptop ",
                operationId = "incident-42",
                confirm = true,
                expectedMatchedSessions = 1
            }),
            "replaying the same operation revokes nothing new and returns the original audit event");

        var probe = t.NewClient("probe");
        // The validation probe names its input "token"; register the JWTs as access tokens first.
        t.Scrub(aliceSession.AccessToken, "access-token");
        t.Scrub(bobSession.AccessToken, "access-token");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/validate", new { token = aliceSession.AccessToken, audience = BehaviorLockConstants.ApiAudience }),
            "Alice's access token no longer validates");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/validate", new { token = bobSession.AccessToken, audience = BehaviorLockConstants.ApiAudience }),
            "Bob's still does");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = aliceSession.RefreshToken,
                ["client_id"] = BehaviorLockConstants.AppClientId
            }),
            "Alice's refresh token is rejected");
        t.Observe(await t.Operator.GetAsync(SessionsRoute), "the session list shows the revocation");

        await t.ObserveAuditAsync("one audit event for the operation");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/sessions")]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation/preview")]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation")]
    public async Task Session_revocation_guards_reject_unsafe_requests()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice);

        var sessions = t.Observe(await t.Operator.GetAsync(SessionsRoute), "Alice's session");
        var sessionId = sessions.JsonString("data.0.id");

        t.Observe(await t.Operator.PostJsonAsync(PreviewRoute, new { }), "a preview needs a selector");
        t.Observe(await t.Operator.PostJsonAsync(PreviewRoute, new { userId = new string('u', 257) }), "an over-long identifier");
        t.Observe(await t.Operator.PostJsonAsync(PreviewRoute, new { userId = alice.Id, reason = new string('r', 257) }), "an over-long reason");
        t.Observe(await t.Operator.PostJsonAsync(PreviewRoute, new { userId = alice.Id, expectedMatchedSessions = -1 }), "a negative expected count");
        t.Observe(
            await t.Operator.PostJsonAsync(RevocationRoute, new { userId = alice.Id, expectedMatchedSessions = 1 }),
            "a revocation without explicit confirmation");
        t.Observe(
            await t.Operator.PostJsonAsync(RevocationRoute, new { userId = alice.Id, confirm = true }),
            "a broad revocation without the previewed count");
        t.Observe(
            await t.Operator.PostJsonAsync(RevocationRoute, new { userId = alice.Id, confirm = true, expectedMatchedSessions = 2 }),
            "a previewed count that no longer matches");
        t.Observe(
            await t.Operator.PostJsonAsync(RevocationRoute, new { userId = alice.Id, confirm = true, expectedMatchedSessions = 1, operationId = new string('o', 129) }),
            "an over-long operation ID");
        t.Observe(
            await t.Operator.PostJsonAsync(RevocationRoute, new { userId = "usr_00000000000000000000000000000000", confirm = true, expectedMatchedSessions = 0 }),
            "a confirmed revocation that matches no session is not found");
        t.Observe(
            await t.Operator.PostJsonAsync(RevocationRoute, new { sessionId, confirm = true, operationId = "incident-7" }),
            "a single-session revocation needs no previewed count");
        t.Observe(
            await t.Operator.PostJsonAsync(RevocationRoute, new { userId = alice.Id, confirm = true, expectedMatchedSessions = 1, operationId = "incident-7" }),
            "an operation ID cannot be reused for a different scope");

        await t.ObserveAuditAsync("only the single-session revocation is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation/preview")]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation")]
    [Covers("GET /sqlos/admin/auth/api/sessions")]
    public async Task Admin_revocation_by_user_leaves_the_browser_signed_in_CurrentBehavior_KnownDefect_427()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice);

        var held = t.Urls.Authorize();
        var silent = t.Observe(await t.GetAsync(held.Url), "Alice's browser session answers a new authorization request without a sign-in");
        var heldCode = silent.NextUrlParameter("code");

        var preview = t.Observe(await t.Operator.PostJsonAsync(PreviewRoute, new { userId = alice.Id }), "the operator previews revoking Alice");
        t.Observe(
            await t.Operator.PostJsonAsync(RevocationRoute, new
            {
                userId = alice.Id,
                reason = "compromised browser",
                confirm = true,
                expectedMatchedSessions = preview.Json!["matchedSessions"]!.GetValue<int>()
            }),
            "and revokes her sessions");

        var again = t.Urls.Authorize();
        var afterRevocation = t.Observe(
            await t.GetAsync(again.Url),
            "known defect #427: the same browser is still signed in and gets a new code without a credential");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", held.TokenRequest(heldCode)),
            "known defect #427: a code issued before the revocation still redeems");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", again.TokenRequest(afterRevocation.NextUrlParameter("code"))),
            "and so does the code issued after it");
        t.Observe(await t.Operator.GetAsync(SessionsRoute), "Alice has new active sessions beside the revoked one");

        await t.ObserveAuditAsync("only the revocation is audited; the silent sign-ins after it write no events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation/preview")]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation")]
    public async Task Admin_revocation_by_organization_misses_a_session_switched_into_it_CurrentBehavior_KnownDefect_427()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var alpha = await t.Setup.CreateOrganizationAsync("alpha");
        var beta = await t.Setup.CreateOrganizationAsync("beta");
        await t.Setup.AddMembershipAsync(alpha, alice);
        var app = t.NewClient("app");
        var probe = t.NewClient("probe");

        var login = t.Observe(
            await app.PostJsonAsync("/sqlos/auth/password/login", new
            {
                email = alice.Email,
                password = alice.Password,
                clientId = BehaviorLockConstants.AppClientId,
                organizationId = alpha.Id
            }),
            "Alice signs in to Alpha");
        // Joined after sign-in: the login response lists memberships from an unordered query
        // (SqlOSAdminService.GetUserOrganizationsAsync), so two memberships come back in varying order.
        await t.Setup.AddMembershipAsync(beta, alice);
        var switched = t.Observe(
            await app.PostJsonAsync("/sqlos/auth/token/refresh", new
            {
                refreshToken = login.JsonString("tokens.refreshToken"),
                organizationId = beta.Id,
                clientId = BehaviorLockConstants.AppClientId
            }),
            "after joining Beta she switches the session to it on refresh");

        t.Observe(
            await t.Operator.PostJsonAsync(PreviewRoute, new { organizationId = beta.Id }),
            "known defect #427: revoking Beta's sessions matches none, because the session row still records Alpha");
        t.Observe(
            await t.Operator.PostJsonAsync(RevocationRoute, new { organizationId = beta.Id, confirm = true, expectedMatchedSessions = 0 }),
            "so the confirmed revocation is not found");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/validate", new { token = switched.JsonString("accessToken"), audience = BehaviorLockConstants.ApiAudience }),
            "Alice's Beta access token still validates");
        t.Observe(
            await app.PostJsonAsync("/sqlos/auth/token/refresh", new
            {
                refreshToken = switched.JsonString("refreshToken"),
                organizationId = beta.Id,
                clientId = BehaviorLockConstants.AppClientId
            }),
            "and she keeps refreshing into Beta");
        t.Observe(
            await t.Operator.PostJsonAsync(PreviewRoute, new { organizationId = alpha.Id }),
            "the session is only reachable through Alpha");

        await t.ObserveAuditAsync("the sign-in is audited; no revocation is recorded");
        await t.ApproveAsync();
    }
}
