using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// The hosted-form antiforgery filter every AuthPage POST shares: a same-origin browser source,
/// form content, the path-scoped CSRF cookie, and a request token bound to that cookie. Each of
/// this surface's form routes is shown refusing a post without the token, and one route walks
/// through every distinct source and cookie branch.
/// </summary>
[TestClass]
public sealed class HostedFormProtectionScenarios
{
    private static readonly string[] FormRoutes =
    [
        "/sqlos/auth/login/identify",
        "/sqlos/auth/login/password",
        "/sqlos/auth/login/email-otp/start",
        "/sqlos/auth/login/email-otp/verify",
        "/sqlos/auth/login/magic-link/start",
        "/sqlos/auth/login/magic-link/complete",
        "/sqlos/auth/login/phone-otp/start",
        "/sqlos/auth/login/phone-otp/verify",
        "/sqlos/auth/login/select-organization",
        "/sqlos/auth/mfa/verify",
        "/sqlos/auth/mfa/totp/enroll/verify",
        "/sqlos/auth/password/forgot/submit",
        "/sqlos/auth/password/reset/submit",
        "/sqlos/auth/signup/submit",
        "/sqlos/auth/signup/invitation/submit",
        "/sqlos/auth/signup/email-otp/start",
        "/sqlos/auth/signup/email-otp/verify",
        "/sqlos/auth/signup/phone-otp/start",
        "/sqlos/auth/signup/phone-otp/verify"
    ];

    [Scenario]
    [Covers("POST /sqlos/auth/login/identify")]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    [Covers("POST /sqlos/auth/login/magic-link/complete")]
    [Covers("POST /sqlos/auth/login/phone-otp/start")]
    [Covers("POST /sqlos/auth/login/phone-otp/verify")]
    [Covers("POST /sqlos/auth/login/select-organization")]
    [Covers("POST /sqlos/auth/mfa/verify")]
    [Covers("POST /sqlos/auth/mfa/totp/enroll/verify")]
    [Covers("POST /sqlos/auth/password/forgot/submit")]
    [Covers("POST /sqlos/auth/password/reset/submit")]
    [Covers("POST /sqlos/auth/signup/submit")]
    [Covers("POST /sqlos/auth/signup/invitation/submit")]
    [Covers("POST /sqlos/auth/signup/email-otp/start")]
    [Covers("POST /sqlos/auth/signup/email-otp/verify")]
    [Covers("POST /sqlos/auth/signup/phone-otp/start")]
    [Covers("POST /sqlos/auth/signup/phone-otp/verify")]
    public async Task Every_hosted_form_refuses_a_post_without_its_antiforgery_token()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        // The browser holds the CSRF cookie from a real page, but the posts carry no token.
        t.Discard(await t.GetAsync("/sqlos/auth/login"));

        foreach (var route in FormRoutes)
        {
            t.Observe(
                await t.PostFormAsync(route, new Dictionary<string, string> { ["email"] = "someone@example.test" }),
                route);
        }

        await t.ObserveAuditAsync("refusals are not audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/password")]
    public async Task The_antiforgery_check_follows_the_browser_source_and_the_cookie_binding()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        // Wrong credentials: a post that passes the filter answers with the password page and 400.
        var form = begun.Page.Form("/login/password").With("email", alice.Email).With("password", "Not-The-Password-1");
        var other = t.NewBrowser("other-browser");
        var otherPage = t.Discard(await other.GetAsync(t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" }).Url));

        t.Observe(await t.SubmitAsync(form, options => options.WithOrigin("https://evil.example.test")), "a cross-site Origin");
        t.Observe(await t.SubmitAsync(form, options => options.WithOrigin("null")), "an opaque Origin: null");
        t.Observe(
            await t.SubmitAsync(form, options => options.WithoutOrigin().Header("Sec-Fetch-Site", "cross-site")),
            "no Origin or Referer, and Sec-Fetch-Site says cross-site");
        t.Observe(
            await t.SubmitAsync(form, options => options.WithoutOrigin().Header("Referer", "https://evil.example.test/page")),
            "no Origin, and a cross-site Referer");
        t.Observe(
            await t.SubmitAsync(form, options => options.WithoutOrigin().Header("Referer", "https://sqlos.example.test/sqlos/auth/authorize")),
            "no Origin, and a same-origin Referer: accepted");
        t.Observe(
            await t.SubmitAsync(form, options => options.WithoutOrigin()),
            "no source headers at all, as a non-browser client sends: accepted");
        t.Observe(
            await t.SubmitAsync(form.With("__RequestVerificationToken", otherPage.Form("/login/password")["__RequestVerificationToken"])),
            "a token issued for another browser's cookie");
        t.Observe(await t.SubmitAsync(form, options => options.WithoutCookies()), "the token without its cookie");
        t.Observe(
            await t.PostJsonAsync("/sqlos/auth/login/password", new
            {
                __RequestVerificationToken = form["__RequestVerificationToken"],
                email = alice.Email,
                password = alice.Password
            }),
            "JSON instead of a form");

        await t.ObserveAuditAsync("only the two accepted posts reach the password check");
        await t.ApproveAsync();
    }
}
