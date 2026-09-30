using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.PhoneOtp;

[TestClass]
public sealed class PhoneOtpScenarios
{
    private const string PhoneNumber = "+12025550148";

    [Scenario]
    [Covers("GET /sqlos/auth/signup/phone-otp")]
    [Covers("POST /sqlos/auth/signup/phone-otp/start")]
    [Covers("POST /sqlos/auth/signup/phone-otp/verify")]
    public async Task Phone_code_sign_up_sends_an_sms_and_creates_a_user()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var request = t.Urls.Authorize();
        var authorize = t.Discard(await t.GetAsync(request.Url));
        var requestId = authorize.Form("/login/identify")["requestId"];

        var signup = t.Observe(await t.GetAsync($"/sqlos/auth/signup/phone-otp?request={requestId}"), "open phone sign-up");
        var started = t.Observe(
            await t.SubmitAsync(signup.Form("/signup/phone-otp/start")
                .With("displayName", "Heidi")
                .With("phoneNumber", PhoneNumber)),
            "send the sign-up code by SMS");

        var verified = t.Observe(
            await t.SubmitAsync(started.Form("/signup/phone-otp/verify").With("code", t.LatestSmsCodeTo(PhoneNumber))),
            "verify the SMS code");

        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(verified.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("phone sign-up events");
        await t.ApproveAsync();
    }
}
