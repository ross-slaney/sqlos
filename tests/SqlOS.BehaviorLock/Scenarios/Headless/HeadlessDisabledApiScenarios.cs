using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>
/// A hosted-AuthPage host that turns the unused headless API off
/// (<c>AuthServer.Headless.EnableApi = false</c>, documented in custom-login-ui.mdx and
/// api-reference.mdx): the routes stay mapped but every one answers 404.
/// </summary>
[TestClass]
public sealed class HeadlessDisabledApiScenarios
{
    private static readonly string[] PostRoutes =
    [
        "/start",
        "/invitations/resolve",
        "/device/resolve",
        "/device/approve",
        "/device/deny",
        "/consent/approve",
        "/consent/deny",
        "/identify",
        "/password/login",
        "/password/forgot",
        "/password/reset",
        "/email-otp/start",
        "/email-otp/verify",
        "/magic-link/start",
        "/magic-link/complete",
        "/signup/email-otp/start",
        "/signup/email-otp/verify",
        "/invitations/signup",
        "/phone-otp/start",
        "/phone-otp/verify",
        "/signup/phone-otp/start",
        "/signup/phone-otp/verify",
        "/signup",
        "/organization/select",
        "/mfa/verify",
        "/mfa/totp/enroll/start",
        "/mfa/totp/enroll/verify",
        "/provider/start"
    ];

    [Scenario]
    [Covers("GET /sqlos/auth/headless/requests/{requestId}")]
    [Covers("POST /sqlos/auth/headless/start")]
    [Covers("POST /sqlos/auth/headless/invitations/resolve")]
    [Covers("POST /sqlos/auth/headless/device/resolve")]
    [Covers("POST /sqlos/auth/headless/device/approve")]
    [Covers("POST /sqlos/auth/headless/device/deny")]
    [Covers("POST /sqlos/auth/headless/consent/approve")]
    [Covers("POST /sqlos/auth/headless/consent/deny")]
    [Covers("POST /sqlos/auth/headless/identify")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/headless/password/forgot")]
    [Covers("POST /sqlos/auth/headless/password/reset")]
    [Covers("POST /sqlos/auth/headless/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/email-otp/verify")]
    [Covers("POST /sqlos/auth/headless/magic-link/start")]
    [Covers("POST /sqlos/auth/headless/magic-link/complete")]
    [Covers("POST /sqlos/auth/headless/signup/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/signup/email-otp/verify")]
    [Covers("POST /sqlos/auth/headless/invitations/signup")]
    [Covers("POST /sqlos/auth/headless/phone-otp/start")]
    [Covers("POST /sqlos/auth/headless/phone-otp/verify")]
    [Covers("POST /sqlos/auth/headless/signup/phone-otp/start")]
    [Covers("POST /sqlos/auth/headless/signup/phone-otp/verify")]
    [Covers("POST /sqlos/auth/headless/signup")]
    [Covers("POST /sqlos/auth/headless/organization/select")]
    [Covers("POST /sqlos/auth/headless/mfa/verify")]
    [Covers("POST /sqlos/auth/headless/mfa/totp/enroll/start")]
    [Covers("POST /sqlos/auth/headless/mfa/totp/enroll/verify")]
    [Covers("POST /sqlos/auth/headless/provider/start")]
    public async Task Every_headless_route_answers_404_when_the_host_disables_the_api()
    {
        await using var t = await Transcript.StartAsync(
            HostProfiles.Hosted,
            options => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.Headless.EnableApi = false);
        var request = t.Urls.Authorize();
        var page = t.Discard(await t.GetAsync(request.Url));
        var requestId = page.Form("/login/identify")["requestId"];

        t.Observe(
            await t.GetAsync($"{Api}/requests/{requestId}"),
            "loading a live hosted request through the disabled API");
        foreach (var route in PostRoutes)
        {
            t.Observe(await t.PostJsonAsync(Api + route, new { requestId }), $"POST {route}");
        }

        await t.ObserveAuditAsync("no events");
        await t.ApproveAsync();
    }
}
