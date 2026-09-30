using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>SMS one-time codes through the headless API: phone signup and phone sign-in.</summary>
[TestClass]
public sealed class HeadlessPhoneOtpScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/headless/signup/phone-otp/start")]
    [Covers("POST /sqlos/auth/headless/signup/phone-otp/verify")]
    [Covers("POST /sqlos/auth/headless/phone-otp/start")]
    [Covers("POST /sqlos/auth/headless/phone-otp/verify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Phone_sign_up_then_phone_sign_in_on_a_new_request()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var signupRequest = t.Urls.Authorize();
        var signupRequestId = await OpenAuthorizeAsync(t, signupRequest);

        var signupStarted = t.Observe(
            await t.PostJsonAsync($"{Api}/signup/phone-otp/start", new
            {
                requestId = signupRequestId,
                displayName = "Hana",
                phoneNumber = PhoneNumber,
                customFields = new { }
            }),
            "start phone signup: SqlOS texts a sign-up code");
        var signedUp = t.Observe(
            await t.PostJsonAsync($"{Api}/signup/phone-otp/verify", new
            {
                requestId = signupRequestId,
                signupToken = signupStarted.JsonString("viewModel.signupToken"),
                challengeToken = signupStarted.JsonString("viewModel.challengeToken"),
                code = t.LatestSmsCodeTo(PhoneNumber)
            }),
            "verify the SMS code: the account is created and signed in");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", signupRequest.TokenRequest(RedirectCode(signedUp))),
            "redeem the signup authorization code");

        var device = t.NewBrowser("second-device");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request, browser: device);
        var started = t.Observe(
            await device.PostJsonAsync($"{Api}/phone-otp/start", new { requestId, phoneNumber = "(202) 555-0148" }),
            "on another device, request a sign-in code for the same number in national format");
        var verified = t.Observe(
            await device.PostJsonAsync($"{Api}/phone-otp/verify", new
            {
                requestId,
                challengeToken = started.JsonString("viewModel.challengeToken"),
                code = t.LatestSmsCodeTo(PhoneNumber)
            }),
            "verify the SMS code: a redirect with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(verified))),
            "redeem the sign-in authorization code");

        await t.ObserveAuditAsync("phone signup and sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/phone-otp/start")]
    [Covers("POST /sqlos/auth/headless/phone-otp/verify")]
    public async Task Unknown_and_invalid_numbers_get_no_sms_and_a_wrong_code_voids_the_challenge()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await SignUpByPhoneAsync(t, PhoneNumber, "Hana");
        var device = t.NewBrowser("phone-user");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: device);

        t.Observe(
            await device.PostJsonAsync($"{Api}/phone-otp/start", new { requestId, phoneNumber = "+1 555" }),
            "a number that cannot be parsed");
        t.Observe(
            await device.PostJsonAsync($"{Api}/phone-otp/start", new { requestId, phoneNumber = SecondPhoneNumber }),
            "a valid number with no account: the same verify view, and no SMS");
        var started = t.Observe(
            await device.PostJsonAsync($"{Api}/phone-otp/start", new { requestId, phoneNumber = PhoneNumber }),
            "the account's number: the SMS is sent");
        var challengeToken = started.JsonString("viewModel.challengeToken");
        var code = t.LatestSmsCodeTo(PhoneNumber);
        var wrong = code == "000000" ? "111111" : "000000";
        t.Observe(
            await device.PostJsonAsync($"{Api}/phone-otp/verify", new { requestId, challengeToken, code = wrong }),
            "a wrong code");
        t.Observe(
            await device.PostJsonAsync($"{Api}/phone-otp/verify", new { requestId, challengeToken, code }),
            "the right code afterwards: the challenge was voided by the first wrong code");
        t.Observe(
            await device.PostJsonAsync($"{Api}/phone-otp/start", new { requestId, phoneNumber = PhoneNumber }),
            "ask for a new code on the same request after the voided one");

        await t.ObserveAuditAsync("phone sign-in failures");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/signup/phone-otp/start")]
    [Covers("POST /sqlos/auth/headless/signup/phone-otp/verify")]
    public async Task Phone_signup_refuses_a_registered_number_an_invitation_and_a_wrong_code()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await SignUpByPhoneAsync(t, PhoneNumber, "Hana");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var invitationToken = await HeadlessInvitationScenarios.InviteAsync(t, acme, t.Unique.Email("erin"));
        var browser = t.NewBrowser("new-user");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: browser);

        t.Observe(
            await browser.PostJsonAsync($"{Api}/signup/phone-otp/start", new { requestId, displayName = "Imposter", phoneNumber = PhoneNumber }),
            "sign up with a number that already has an account");
        t.Observe(
            await browser.PostJsonAsync($"{Api}/signup/phone-otp/start", new { requestId, displayName = "Erin", phoneNumber = SecondPhoneNumber, invitationToken }),
            "sign up by phone while carrying an email invitation");
        var invitedRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: browser);
        var started = t.Observe(
            await browser.PostJsonAsync($"{Api}/signup/phone-otp/start", new { requestId = invitedRequestId, displayName = "Ines", phoneNumber = SecondPhoneNumber }),
            "sign up with a new number on a fresh request");
        var code = t.LatestSmsCodeTo(SecondPhoneNumber);
        t.Observe(
            await browser.PostJsonAsync($"{Api}/signup/phone-otp/verify", new
            {
                requestId = invitedRequestId,
                signupToken = started.JsonString("viewModel.signupToken"),
                challengeToken = started.JsonString("viewModel.challengeToken"),
                code = code == "000000" ? "111111" : "000000"
            }),
            "a wrong code: no account is created");

        await t.ObserveAuditAsync("phone signup refusals");
        await t.ApproveAsync();
    }

    /// <summary>Creates an account by phone signup as a precondition (nothing is recorded).</summary>
    private static async Task SignUpByPhoneAsync(Transcript t, string phoneNumber, string displayName)
    {
        var browser = t.NewBrowser("setup");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request, browser: browser);
        var started = t.Discard(await browser.PostJsonAsync($"{Api}/signup/phone-otp/start", new { requestId, displayName, phoneNumber }));
        var verified = t.Discard(await browser.PostJsonAsync($"{Api}/signup/phone-otp/verify", new
        {
            requestId,
            signupToken = started.JsonString("viewModel.signupToken"),
            challengeToken = started.JsonString("viewModel.challengeToken"),
            code = t.LatestSmsCodeTo(phoneNumber)
        }));
        if (verified.Json?["type"]?.GetValue<string>() != "redirect")
        {
            throw new InvalidOperationException($"Setup phone signup failed: {verified.Describe()} {verified.Preview()}");
        }

        await t.SkipAuditAsync();
    }
}
