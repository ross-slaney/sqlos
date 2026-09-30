using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// Hosted phone-code sign-in (<c>/login/phone-otp</c>, start and verify) through the Twilio Verify
/// channel the host replaces with a fake, and the branches the forms handle distinctly. Unlike
/// email codes, one failed provider check invalidates the challenge.
/// </summary>
[TestClass]
public sealed class HostedPhoneOtpScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/login/phone-otp")]
    [Covers("POST /sqlos/auth/login/phone-otp/start")]
    [Covers("POST /sqlos/auth/login/phone-otp/verify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Phone_code_sign_in_completes_the_authorization_request()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        await HostedFlows.SignUpWithPhoneAsync(t, "Heidi");
        var begun = await HostedFlows.BeginAsync(t);

        var page = t.Observe(
            await t.GetAsync($"/sqlos/auth/login/phone-otp?request={begun.RequestId}"),
            "follow 'Use a phone code instead'");
        var started = t.Observe(
            await t.SubmitAsync(page.Form("/login/phone-otp/start").With("phoneNumber", "(202) 555-0173")),
            "send the code to a nationally formatted number");
        var verified = t.Observe(
            await t.SubmitAsync(started.Form("/login/phone-otp/verify").With("code", HostedFlows.SmsCode(t))),
            "enter the code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", begun.Request.TokenRequest(verified.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("phone sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/phone-otp")]
    [Covers("POST /sqlos/auth/login/phone-otp/start")]
    [Covers("POST /sqlos/auth/login/phone-otp/verify")]
    public async Task Phone_code_without_an_authorization_request_starts_an_issuer_session()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        await HostedFlows.SignUpWithPhoneAsync(t, "Heidi");

        var page = t.Observe(
            await t.GetAsync($"/sqlos/auth/login/phone-otp?phoneNumber={Uri.EscapeDataString(HostedFlows.PhoneNumber)}"),
            "open the phone-code page directly with the number prefilled");
        var started = t.Observe(await t.SubmitAsync(page.Form("/login/phone-otp/start")), "send the code");
        t.Observe(
            await t.SubmitAsync(started.Form("/login/phone-otp/verify").With("code", HostedFlows.SmsCode(t))),
            "enter the code: signed in to SqlOS itself");

        await t.ObserveAuditAsync("standalone phone sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/phone-otp/start")]
    [Covers("POST /sqlos/auth/login/phone-otp/verify")]
    public async Task An_unknown_number_gets_the_same_answer_no_sms_and_an_unstarted_challenge()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/phone-otp?request={begun.RequestId}"));

        var started = t.Observe(
            await t.SubmitAsync(page.Form("/login/phone-otp/start").With("phoneNumber", HostedFlows.PhoneNumber)),
            "request a code for a number no account owns: no SMS");
        t.Observe(
            await t.SubmitAsync(started.Form("/login/phone-otp/verify").With("code", "123456")),
            "any code is refused because the provider never started a verification");

        await t.ObserveAuditAsync("the challenge was never started");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/phone-otp/verify")]
    public async Task One_wrong_code_invalidates_the_phone_challenge()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        await HostedFlows.SignUpWithPhoneAsync(t, "Heidi");
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/phone-otp?request={begun.RequestId}"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/login/phone-otp/start").With("phoneNumber", HostedFlows.PhoneNumber)));
        await t.SkipAuditAsync();
        var code = HostedFlows.SmsCode(t);
        var verify = started.Form("/login/phone-otp/verify");

        t.Observe(await t.SubmitAsync(verify.With("code", HostedFlows.WrongCode(t, code))), "a wrong code");
        t.Observe(await t.SubmitAsync(verify.With("code", code)), "the right code is refused afterwards");
        t.Observe(await t.SubmitAsync(verify.With("code", "no digits")), "a code without digits never reaches the provider");

        await t.ObserveAuditAsync("the provider rejection invalidates the challenge");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/phone-otp/verify")]
    public async Task A_phone_code_started_for_one_authorization_request_cannot_complete_another()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        await HostedFlows.SignUpWithPhoneAsync(t, "Heidi");
        var first = await HostedFlows.BeginAsync(t);
        var second = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/phone-otp?request={first.RequestId}"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/login/phone-otp/start").With("phoneNumber", HostedFlows.PhoneNumber)));
        await t.SkipAuditAsync();
        var code = HostedFlows.SmsCode(t);

        t.Observe(
            await t.SubmitAsync(started.Form("/login/phone-otp/verify").With("requestId", second.RequestId).With("code", code)),
            "present the code with another authorization request");
        t.Observe(
            await t.SubmitAsync(started.Form("/login/phone-otp/verify").With("code", code)),
            "the code still completes its own request: the mismatch never reached the provider");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/phone-otp/start")]
    public async Task Invalid_numbers_and_a_second_code_within_the_cooldown_are_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        await HostedFlows.SignUpWithPhoneAsync(t, "Heidi");
        var page = t.Discard(await t.GetAsync("/sqlos/auth/login/phone-otp"));
        var start = page.Form("/login/phone-otp/start");

        t.Observe(await t.SubmitAsync(start.With("phoneNumber", "")), "a blank number");
        t.Observe(await t.SubmitAsync(start.With("phoneNumber", "12345")), "a number that is not valid");
        t.Discard(await t.SubmitAsync(start.With("phoneNumber", HostedFlows.PhoneNumber)));
        t.Observe(await t.SubmitAsync(start.With("phoneNumber", HostedFlows.PhoneNumber)), "a second code straight away");

        await t.ObserveAuditAsync("phone start events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/phone-otp/start")]
    public async Task The_sixth_code_for_one_number_within_an_hour_is_rate_limited()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        for (var sent = 1; sent <= 5; sent++)
        {
            var begun = await HostedFlows.BeginAsync(t);
            var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/phone-otp?request={begun.RequestId}"));
            HostedFlows.EnsureStatus(
                t.Discard(await t.SubmitAsync(page.Form("/login/phone-otp/start").With("phoneNumber", HostedFlows.PhoneNumber))),
                200);
        }

        await t.SkipAuditAsync();
        var sixth = await HostedFlows.BeginAsync(t);
        var sixthPage = t.Discard(await t.GetAsync($"/sqlos/auth/login/phone-otp?request={sixth.RequestId}"));
        t.Observe(
            await t.SubmitAsync(sixthPage.Form("/login/phone-otp/start").With("phoneNumber", HostedFlows.PhoneNumber)),
            "a sixth code for the same number in the hour");

        await t.ObserveAuditAsync("the per-number admission rejects it");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/phone-otp")]
    [Covers("POST /sqlos/auth/login/phone-otp/start")]
    [Covers("POST /sqlos/auth/login/phone-otp/verify")]
    public async Task Phone_codes_are_unavailable_when_the_application_does_not_enable_them()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);

        var page = t.Observe(await t.GetAsync("/sqlos/auth/login/phone-otp"), "the phone-code page still renders");
        t.Observe(
            await t.SubmitAsync(page.Form("/login/phone-otp/start").With("phoneNumber", HostedFlows.PhoneNumber)),
            "sending a code is refused");
        t.Observe(
            await t.PostFormAsync("/sqlos/auth/login/phone-otp/verify", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = page.Form("/login/phone-otp/start")["__RequestVerificationToken"],
                ["phoneNumber"] = HostedFlows.PhoneNumber,
                ["challengeToken"] = "unused",
                ["code"] = "123456"
            }),
            "verifying is refused too");

        await t.ApproveAsync();
    }
}
