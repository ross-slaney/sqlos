using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Host.Support;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>TOTP enrollment and verification when an MFA policy applies to a headless sign-in.</summary>
[TestClass]
public sealed class HeadlessMfaScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/headless/mfa/totp/enroll/start")]
    [Covers("POST /sqlos/auth/headless/mfa/totp/enroll/verify")]
    [Covers("POST /sqlos/auth/headless/mfa/verify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Required_totp_is_enrolled_on_first_sign_in_and_challenged_on_the_next_CurrentBehavior_KnownDefect_415()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await RequireTotpForAllUsersAsync(t);
        var grace = await t.Setup.CreateUserAsync("grace");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);

        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = grace.Email, password = grace.Password }),
            "sign in: the policy requires TOTP and grace has none, so enrollment starts");
        var mfaToken = login.JsonString("viewModel.mfaToken");
        var restarted = t.Observe(
            await t.PostJsonAsync($"{Api}/mfa/totp/enroll/start", new { requestId, mfaToken, displayName = "Work phone" }),
            "restart enrollment with a name for the authenticator");
        var step = await Totp.StableCurrentStepAsync();
        var enrolled = t.Observe(
            await t.PostJsonAsync($"{Api}/mfa/totp/enroll/verify", new
            {
                requestId,
                mfaToken,
                enrollmentToken = restarted.JsonString("viewModel.totpEnrollment.enrollmentToken"),
                code = Totp.Code(restarted.JsonString("viewModel.totpEnrollment.secret"), step)
            }),
            "confirm the authenticator with its current code: a redirect with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(enrolled))),
            "redeem the authorization code");

        var laptop = t.NewBrowser("laptop");
        var next = t.Urls.Authorize();
        var nextRequestId = await OpenAuthorizeAsync(t, next, browser: laptop);
        var challenge = t.Observe(
            await laptop.PostJsonAsync($"{Api}/password/login", new { requestId = nextRequestId, email = grace.Email, password = grace.Password }),
            "on another browser, sign in again: the enrolled factor is challenged");
        var nextStep = await Totp.NextUnusedStepAsync(step);
        var verified = t.Observe(
            await laptop.PostJsonAsync($"{Api}/mfa/verify", new
            {
                requestId = nextRequestId,
                mfaToken = challenge.JsonString("viewModel.mfaToken"),
                code = Totp.Code(restarted.JsonString("viewModel.totpEnrollment.secret"), nextStep)
            }),
            "answer the challenge with a fresh code: a redirect with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", next.TokenRequest(RedirectCode(verified))),
            "redeem the authorization code");

        t.Note("Known defect #415: enrolling the authenticator writes no audit event; only user.login.mfa is recorded.");
        await t.ObserveAuditAsync("MFA enrollment and challenge events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/mfa/totp/enroll/verify")]
    [Covers("POST /sqlos/auth/headless/mfa/totp/enroll/start")]
    [Covers("GET /sqlos/auth/headless/requests/{requestId}")]
    [Covers("POST /sqlos/auth/headless/mfa/verify")]
    public async Task Wrong_codes_and_foreign_mfa_tokens_keep_the_user_on_the_challenge()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await RequireTotpForAllUsersAsync(t);
        var grace = await t.Setup.CreateUserAsync("grace");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        var login = t.Discard(await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = grace.Email, password = grace.Password }));
        var mfaToken = login.JsonString("viewModel.mfaToken");
        var firstSecret = login.JsonString("viewModel.totpEnrollment.secret");

        var enrollStep = await Totp.StableCurrentStepAsync();
        t.Observe(
            await t.PostJsonAsync($"{Api}/mfa/totp/enroll/verify", new
            {
                requestId,
                mfaToken,
                enrollmentToken = login.JsonString("viewModel.totpEnrollment.enrollmentToken"),
                code = WrongCode(firstSecret, enrollStep)
            }),
            "confirm the authenticator with a wrong code: the enrollment view with an error");
        t.Observe(
            await t.PostJsonAsync($"{Api}/mfa/totp/enroll/start", new { requestId, mfaToken = "not-an-mfa-token" }),
            "restart enrollment with an MFA token that does not exist");
        var reloaded = t.Observe(
            await t.GetAsync($"{Api}/requests/{requestId}?mfaToken={Uri.EscapeDataString(mfaToken)}"),
            "reload the request with the MFA token: the enrollment view with a new authenticator");
        var secret = reloaded.JsonString("totpEnrollment.secret");
        enrollStep = await Totp.StableCurrentStepAsync();
        t.Observe(
            await t.PostJsonAsync($"{Api}/mfa/totp/enroll/verify", new
            {
                requestId,
                mfaToken,
                enrollmentToken = reloaded.JsonString("totpEnrollment.enrollmentToken"),
                code = Totp.Code(secret, enrollStep)
            }),
            "confirm the reloaded authenticator: a redirect with a code");

        var laptop = t.NewBrowser("laptop");
        var nextRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: laptop);
        var challenge = t.Discard(await laptop.PostJsonAsync($"{Api}/password/login", new { requestId = nextRequestId, email = grace.Email, password = grace.Password }));
        var challengeToken = challenge.JsonString("viewModel.mfaToken");
        var step = await Totp.NextUnusedStepAsync(enrollStep);
        t.Observe(
            await laptop.PostJsonAsync($"{Api}/mfa/verify", new { requestId = nextRequestId, mfaToken = challengeToken, code = WrongCode(secret, step) }),
            "answer the challenge with a wrong code: the MFA view with an error");
        t.Observe(
            await laptop.GetAsync($"{Api}/requests/{nextRequestId}?mfaToken={Uri.EscapeDataString(challengeToken)}"),
            "reload the request with the MFA token: the challenge view");
        t.Observe(
            await laptop.PostJsonAsync($"{Api}/mfa/verify", new { requestId = nextRequestId, mfaToken = challengeToken, code = Totp.Code(secret, step) }),
            "the right code still completes the challenge");

        await t.ObserveAuditAsync("MFA failure events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/headless/organization/select")]
    [Covers("POST /sqlos/auth/headless/mfa/totp/enroll/verify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task With_mfa_required_an_organization_pick_continues_to_the_challenge_and_completes()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await RequireTotpForAllUsersAsync(t);
        var frank = await t.Setup.CreateUserAsync("frank");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        await t.Setup.AddMembershipAsync(acme, frank);
        await t.Setup.AddMembershipAsync(globex, frank);
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);

        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = frank.Email, password = frank.Password }),
            "sign in: the organization is asked before MFA");
        var selected = t.Observe(
            await t.PostJsonAsync($"{Api}/organization/select", new { pendingToken = login.JsonString("viewModel.pendingToken"), organizationId = globex.Id }),
            "pick globex: MFA enrollment is required, so the pick answers with the enrollment view");
        var step = await Totp.StableCurrentStepAsync();
        var enrolled = t.Observe(
            await t.PostJsonAsync($"{Api}/mfa/totp/enroll/verify", new
            {
                requestId,
                mfaToken = selected.JsonString("viewModel.mfaToken"),
                enrollmentToken = selected.JsonString("viewModel.totpEnrollment.enrollmentToken"),
                code = Totp.Code(selected.JsonString("viewModel.totpEnrollment.secret"), step)
            }),
            "confirm the authenticator: a redirect with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(enrolled))),
            "redeem the authorization code: bound to the picked organization");

        await t.ObserveAuditAsync("organization and MFA events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/mfa/verify")]
    [Covers("POST /sqlos/auth/headless/mfa/totp/enroll/verify")]
    [Covers("GET /sqlos/auth/headless/requests/{requestId}")]
    public async Task Mfa_verify_refuses_enrollment_challenges_and_completes_the_token_s_own_request_whatever_request_id_is_sent()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await RequireTotpForAllUsersAsync(t);
        var grace = await t.Setup.CreateUserAsync("grace");
        var enrollRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        var enrollment = t.Discard(await t.PostJsonAsync($"{Api}/password/login", new { requestId = enrollRequestId, email = grace.Email, password = grace.Password }));
        var secret = enrollment.JsonString("viewModel.totpEnrollment.secret");

        t.Observe(
            await t.PostJsonAsync($"{Api}/mfa/verify", new { requestId = enrollRequestId, mfaToken = enrollment.JsonString("viewModel.mfaToken"), code = "000000" }),
            "answer an enrollment challenge on the verify route: refused, enrollment needs its own proof");
        var step = await Totp.StableCurrentStepAsync();
        t.Observe(
            await t.PostJsonAsync($"{Api}/mfa/totp/enroll/verify", new
            {
                requestId = enrollRequestId,
                mfaToken = enrollment.JsonString("viewModel.mfaToken"),
                enrollmentToken = enrollment.JsonString("viewModel.totpEnrollment.enrollmentToken"),
                code = Totp.Code(secret, step)
            }),
            "enroll properly: a redirect with a code");

        var laptop = t.NewBrowser("laptop");
        var challengedRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: laptop);
        var challenge = t.Discard(await laptop.PostJsonAsync($"{Api}/password/login", new { requestId = challengedRequestId, email = grace.Email, password = grace.Password }));
        var otherRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: laptop);
        var nextStep = await Totp.NextUnusedStepAsync(step);
        t.Observe(
            await laptop.PostJsonAsync($"{Api}/mfa/verify", new { requestId = otherRequestId, mfaToken = challenge.JsonString("viewModel.mfaToken"), code = Totp.Code(secret, nextStep) }),
            "answer the challenge naming another open request: the redirect completes the challenge's own request");
        t.Observe(
            await laptop.GetAsync($"{Api}/requests/{challengedRequestId}"),
            "the challenge's own request is the one that finished");
        t.Observe(
            await laptop.GetAsync($"{Api}/requests/{otherRequestId}"),
            "the request named in the body is still open");

        await t.ObserveAuditAsync("MFA binding events");
        await t.ApproveAsync();
    }

    /// <summary>A six-digit code that no step within the verifier's drift window accepts.</summary>
    private static string WrongCode(string secret, long step)
    {
        var accepted = new[] { step - 1, step, step + 1 }.Select(candidate => Totp.Code(secret, candidate)).ToHashSet(StringComparer.Ordinal);
        return new[] { "000000", "111111", "222222" }.First(candidate => !accepted.Contains(candidate));
    }
}
