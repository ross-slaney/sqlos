using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// Operators signed in with the dashboard password carry a session cookie, so every admin
/// mutation must prove it came from the dashboard itself: the <c>X-SqlOS-Request: 1</c> header plus
/// a trusted Origin, Referer, or <c>Sec-Fetch-Site: same-origin</c>. The filter is attached per
/// route group, so this locks it on each configuration group that accepts mutations (auth admin,
/// email admin, calendar admin), together with the module pages' signed-out redirect.
/// </summary>
[TestClass]
public sealed class AdminCsrfScenarios
{
    private const string SecurityRoute = "/sqlos/admin/auth/api/settings/security";

    [Scenario]
    [Covers("GET /sqlos/admin/email/{*page}")]
    [Covers("GET /sqlos/admin/calendar/{*page}")]
    [Covers("GET /sqlos/admin/auth/api/settings/security")]
    [Covers("PUT /sqlos/admin/auth/api/settings/security")]
    [Covers("POST /sqlos/admin/email/api/templates")]
    [Covers("POST /sqlos/admin/calendar/api/connections/{connectionId}/sync")]
    public async Task Cookie_authenticated_admin_mutations_need_the_same_origin_proof()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardPassword);
        var browser = t.NewBrowser("operator-browser");

        t.Observe(
            await browser.GetAsync("/sqlos/admin/email/templates"),
            "a signed-out operator opening the email page is sent to the dashboard sign-in");
        t.Observe(
            await browser.PostJsonAsync("/sqlos/admin/email/api/templates", Template("signed.out")),
            "a signed-out mutation is not found");
        t.Discard(await browser.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = BehaviorLockConstants.DashboardPassword }));
        t.Note("The operator browser signed in with the dashboard password and now carries the session cookie.");

        t.Observe(await browser.GetAsync("/sqlos/admin/calendar/connections"), "signed in, the calendar page is the dashboard shell");
        t.Observe(await browser.GetAsync(SecurityRoute), "reads need no same-origin proof");
        t.Observe(
            await browser.PutJsonAsync(SecurityRoute, Security()),
            "a settings change without the X-SqlOS-Request header is refused");
        t.Observe(
            await browser.PutJsonAsync(SecurityRoute, Security(), options => options.Header("X-SqlOS-Request", "1").WithOrigin("https://evil.example.test")),
            "a cross-site Origin is refused even with the header");
        t.Observe(
            await browser.PutJsonAsync(SecurityRoute, Security(), options => options.Header("X-SqlOS-Request", "1").WithOrigin("null")),
            "an opaque Origin is refused");
        t.Observe(
            await browser.PutJsonAsync(SecurityRoute, Security(), options => options.Header("X-SqlOS-Request", "1").WithoutOrigin()),
            "with no Origin, Referer, or Sec-Fetch-Site the source is untrusted");
        t.Observe(
            await browser.PutJsonAsync(SecurityRoute, Security(), options => options
                .Header("X-SqlOS-Request", "1")
                .WithoutOrigin()
                .Header("Referer", BehaviorLockConstants.PublicOrigin + "/sqlos/admin/auth/security")),
            "a same-origin Referer is trusted");
        t.Observe(
            await browser.PutJsonAsync(SecurityRoute, Security(refreshTokenLifetimeMinutes: 20000), options => options
                .Header("X-SqlOS-Request", "1")
                .WithoutOrigin()
                .Header("Sec-Fetch-Site", "same-origin")),
            "so is Sec-Fetch-Site: same-origin when no Origin or Referer is sent");
        t.Observe(
            await browser.PostJsonAsync("/sqlos/admin/email/api/templates", Template("billing.receipt")),
            "the email admin API refuses a mutation without the header");
        t.Observe(
            await browser.PostJsonAsync("/sqlos/admin/calendar/api/connections/cal_00000000000000000000000000000000/sync", new { }),
            "so does the calendar admin API, before looking up the connection");
        t.Observe(
            await browser.PostJsonAsync("/sqlos/admin/email/api/templates", Template("billing.receipt"), options => options.Header("X-SqlOS-Request", "1")),
            "with the header and the browser's own Origin the mutation proceeds");

        await t.ObserveAuditAsync("each refused mutation is audited with its reason");
        await t.ApproveAsync();
    }

    private static object Security(int refreshTokenLifetimeMinutes = 43200)
        => new
        {
            refreshTokenLifetimeMinutes,
            sessionIdleTimeoutMinutes = 1440,
            sessionAbsoluteLifetimeMinutes = 20160,
            signingKeyRotationIntervalDays = 30,
            signingKeyGraceWindowDays = 5,
            signingKeyRetiredCleanupDays = 10,
            refreshTokenGraceWindowSeconds = 30
        };

    private static object Template(string key)
        => new { key, displayName = "Receipt", subjectTemplate = "Receipt", htmlBodyTemplate = "<p>Receipt</p>", textBodyTemplate = "Receipt" };
}
