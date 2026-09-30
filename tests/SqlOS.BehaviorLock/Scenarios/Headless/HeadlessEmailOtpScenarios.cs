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

    [GeneratedRegex(@"(?<!\d)\d{6}(?!\d)")]
    private static partial Regex OtpCode();
}
