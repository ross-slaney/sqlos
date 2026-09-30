using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Host.Support;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>
/// What an operator's session revocation reaches in a headless deployment. Known defect #427:
/// revoking a user's sessions ends OAuth sessions and refresh tokens, but not the browser's
/// issuer session or the user's pending sign-in artifacts.
/// </summary>
[TestClass]
public sealed class HeadlessRevocationScenarios
{
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation/preview")]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/headless/device/approve")]
    public async Task Revoking_a_user_leaves_the_browser_issuer_session_signing_in_and_approving_devices_CurrentBehavior_KnownDefect_427()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreateCliClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await SignInAsync(t, alice);

        await RevokeUserSessionsAsync(t, alice, "alice");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = session.RefreshToken,
                ["client_id"] = AppClientId
            }),
            "alice's refresh token is revoked");
        t.Note("Known defect #427: the browser's issuer session survives the revocation.");
        var request = t.Urls.Authorize();
        var silent = t.Observe(
            await t.GetAsync(request.Url),
            "the same browser authorizes again: signed in silently, a code without any credential");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(silent.NextUrlParameter("code"))),
            "the code redeems into a new session");
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", new Dictionary<string, string>
        {
            ["client_id"] = CliClientId,
            ["scope"] = "openid profile offline_access"
        }));
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { userCode = started.JsonString("user_code") }),
            "the surviving issuer session also approves a device");
        t.Observe(
            await cli.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = DeviceGrant,
                ["device_code"] = started.JsonString("device_code"),
                ["client_id"] = CliClientId
            }),
            "the CLI receives tokens for alice");

        await t.ObserveAuditAsync("revocation and reuse events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation/preview")]
    [Covers("POST /sqlos/admin/auth/api/sessions/revocation")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/headless/mfa/verify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Revoking_a_user_leaves_a_pending_mfa_challenge_usable_CurrentBehavior_KnownDefect_427()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await RequireTotpForAllUsersAsync(t);
        var grace = await t.Setup.CreateUserAsync("grace");
        var (secret, enrollStep) = await EnrollTotpAsync(t, grace);

        var laptop = t.NewBrowser("laptop");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request, browser: laptop);
        var challenge = t.Observe(
            await laptop.PostJsonAsync($"{Api}/password/login", new { requestId, email = grace.Email, password = grace.Password }),
            "a sign-in on the laptop stops at the MFA challenge");
        await RevokeUserSessionsAsync(t, grace, "grace");
        t.Note("Known defect #427: the pending MFA challenge survives the revocation.");
        var step = await Totp.NextUnusedStepAsync(enrollStep);
        var verified = t.Observe(
            await laptop.PostJsonAsync($"{Api}/mfa/verify", new { requestId, mfaToken = challenge.JsonString("viewModel.mfaToken"), code = Totp.Code(secret, step) }),
            "the challenge still completes after the revocation: a redirect with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(verified))),
            "the code redeems into a new session");

        await t.ObserveAuditAsync("revocation and challenge events");
        await t.ApproveAsync();
    }

    /// <summary>
    /// The operator's documented revocation flow, recorded: preview the user's sessions, then
    /// confirm with the previewed count.
    /// </summary>
    private static async Task RevokeUserSessionsAsync(Transcript t, ScenarioUser user, string name)
    {
        var preview = t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sessions/revocation/preview", new { userId = user.Id, reason = "incident response" }),
            $"the operator previews revoking every session of {name}");
        t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sessions/revocation", new
            {
                userId = user.Id,
                reason = "incident response",
                operationId = $"incident-{name}",
                confirm = true,
                expectedMatchedSessions = preview.Json!["matchedSessions"]!.GetValue<int>()
            }),
            $"the operator confirms: every session of {name} is revoked");
    }

    /// <summary>
    /// Enrolls TOTP for <paramref name="user"/> through a first headless sign-in and redeems the
    /// code (unrecorded), returning the authenticator secret and the step its code used.
    /// </summary>
    private static async Task<(string Secret, long Step)> EnrollTotpAsync(Transcript t, ScenarioUser user)
    {
        var browser = t.NewBrowser("enrollment");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request, browser: browser);
        var login = t.Discard(await browser.PostJsonAsync($"{Api}/password/login", new { requestId, email = user.Email, password = user.Password }));
        var secret = login.JsonString("viewModel.totpEnrollment.secret");
        var step = await Totp.StableCurrentStepAsync();
        var enrolled = t.Discard(await browser.PostJsonAsync($"{Api}/mfa/totp/enroll/verify", new
        {
            requestId,
            mfaToken = login.JsonString("viewModel.mfaToken"),
            enrollmentToken = login.JsonString("viewModel.totpEnrollment.enrollmentToken"),
            code = Totp.Code(secret, step)
        }));
        var token = t.Discard(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(enrolled))));
        if (token.StatusCode != 200)
        {
            throw new InvalidOperationException($"Setup enrollment failed: {token.Describe()} {token.Preview()}");
        }

        await t.SkipAuditAsync();
        return (secret, step);
    }
}
