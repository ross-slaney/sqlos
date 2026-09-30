using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

[TestClass]
public sealed partial class HeadlessEmailOtpScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("GET /sqlos/auth/headless/requests/{requestId}")]
    [Covers("POST /sqlos/auth/headless/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/email-otp/verify")]
    public async Task Email_code_sign_in_through_the_headless_api()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize();

        var authorize = t.Observe(await t.GetAsync(request.Url), "authorize redirects to the app's own sign-in UI");
        var requestId = authorize.NextUrlParameter("request");

        t.Observe(await t.GetAsync($"/sqlos/auth/headless/requests/{requestId}"), "the UI loads the request");
        var started = t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/start", new { requestId, email = alice.Email }),
            "request an email code");

        var code = OtpCode().Match(t.LatestEmailTo(alice.Email).TextBody ?? string.Empty).Value;
        var verified = t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/verify", new
            {
                requestId,
                challengeToken = started.JsonString("viewModel.challengeToken"),
                code
            }),
            "verify the code");

        var redirect = verified.JsonString("redirectUrl");
        var authorizationCode = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(authorizationCode)),
            "redeem the authorization code");

        await t.ObserveAuditAsync("email-code sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/email-otp/verify")]
    public async Task Wrong_codes_keep_the_verify_view_until_the_fifth_exhausts_the_challenge()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var requestId = await HeadlessJourney.OpenAuthorizeAsync(t, t.Urls.Authorize());
        var started = t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/start", new { requestId, email = alice.Email }),
            "request an email code");
        var challengeToken = started.JsonString("viewModel.challengeToken");
        var code = OtpCode().Match(t.LatestEmailTo(alice.Email).TextBody ?? string.Empty).Value;
        var wrong = code == "000000" ? "111111" : "000000";

        t.Note("Known defect #424 is a race between concurrent guesses; this sequential attempt budget is what its fix keeps.");
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            t.Observe(
                await t.PostJsonAsync("/sqlos/auth/headless/email-otp/verify", new { requestId, challengeToken, code = wrong }),
                $"wrong code {attempt} of 5");
        }

        t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/verify", new { requestId, challengeToken, code }),
            "the right code after five wrong ones: the challenge is exhausted");

        await t.ObserveAuditAsync("failed verification events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/email-otp/verify")]
    public async Task No_code_is_sent_to_an_address_without_an_account()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var requestId = await HeadlessJourney.OpenAuthorizeAsync(t, t.Urls.Authorize());
        var nobody = t.Unique.Email("nobody");

        var started = t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/start", new { requestId, email = nobody }),
            "request a code for an address with no account: the same verify view, and no email");
        t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/verify", new { requestId, challengeToken = started.JsonString("viewModel.challengeToken"), code = "123456" }),
            "any code for that challenge is rejected");
        t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/start", new { requestId, email = "not-an-email" }),
            "a malformed address");

        await t.ObserveAuditAsync("unsent challenge events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/email-otp/start")]
    public async Task Email_code_requests_are_throttled_by_the_resend_cooldown_and_per_address()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var firstRequestId = await HeadlessJourney.OpenAuthorizeAsync(t, t.Urls.Authorize());

        t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/start", new { requestId = firstRequestId, email = alice.Email }),
            "the first code for this request");
        t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/start", new { requestId = firstRequestId, email = alice.Email }),
            "resend on the same request inside the cooldown");
        for (var request = 2; request <= 6; request++)
        {
            var requestId = await HeadlessJourney.OpenAuthorizeAsync(t, t.Urls.Authorize());
            t.Observe(
                await t.PostJsonAsync("/sqlos/auth/headless/email-otp/start", new { requestId, email = alice.Email }),
                $"a code on authorization request {request}: five codes per address per hour");
        }

        await t.ObserveAuditAsync("throttling events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/email-otp/verify")]
    public async Task A_code_started_for_one_request_cannot_complete_another()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var requestId = await HeadlessJourney.OpenAuthorizeAsync(t, t.Urls.Authorize());
        var otherRequestId = await HeadlessJourney.OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: t.NewBrowser("attacker"));
        var started = t.Discard(await t.PostJsonAsync("/sqlos/auth/headless/email-otp/start", new { requestId, email = alice.Email }));
        var challengeToken = started.JsonString("viewModel.challengeToken");
        var code = OtpCode().Match(t.LatestEmailTo(alice.Email).TextBody ?? string.Empty).Value;

        t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/verify", new { requestId = otherRequestId, challengeToken, code }),
            "the right code and challenge presented with another authorization request: rejected");
        t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/verify", new { requestId, challengeToken, code }),
            "the same code still completes its own request: the mismatch did not spend an attempt");

        await t.ObserveAuditAsync("cross-request verification events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/email-otp/start")]
    public async Task An_invitation_for_an_address_without_an_account_asks_for_signup_instead_of_a_code()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var email = t.Unique.Email("erin");
        var invitationToken = await HeadlessInvitationScenarios.InviteAsync(t, acme, email);
        var requestId = await HeadlessJourney.OpenAuthorizeAsync(t, t.Urls.Authorize());

        t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/email-otp/start", new { requestId, email, invitationToken }),
            "an email code with the invitation for an address that has no account: the signup view, no code");

        await t.ObserveAuditAsync("no challenge events");
        await t.ApproveAsync();
    }

    [GeneratedRegex(@"(?<!\d)\d{6}(?!\d)")]
    private static partial Regex OtpCode();
}
