using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using SqlOS.BehaviorLock.Scenarios.Probes;

namespace SqlOS.BehaviorLock.Scenarios.Dashboard;

/// <summary>
/// The Password-mode operator session (<c>/sqlos/dashboard-auth/*</c>): sign-in validation, the
/// per-address and global login throttles backed by the database rate-limit store, the session
/// cookie, the same-origin proof on logout, and what logout does and does not end.
/// </summary>
[TestClass]
public sealed class DashboardSessionScenarios
{
    private const string SessionCookie = "SqlOS.Dashboard.Session";
    private const string WrongPassword = "not-the-dashboard-password";

    // Scenarios sign in from their own addresses: the harness signs the operator in from the
    // default client address before every Password-mode scenario, and that sign-in has already
    // used the default address's budget.
    private const string AttackerAddress = "198.51.100.66";
    private const string OperatorAddress = "198.51.100.7";

    [Scenario]
    [Covers("POST /sqlos/dashboard-auth/login")]
    public async Task Login_requires_a_password_and_locks_an_address_out_after_five_failures()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardPassword);
        var attacker = t.NewBrowser("attacker");

        t.Observe(
            await attacker.PostJsonAsync("/sqlos/dashboard-auth/login", new { }, options => options.FromAddress(AttackerAddress)),
            "a body without a password is rejected before it counts");
        t.Observe(
            await attacker.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = "   " }, options => options.FromAddress(AttackerAddress)),
            "so is a blank password");
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            t.Observe(
                await attacker.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = WrongPassword }, options => options.FromAddress(AttackerAddress)),
                attempt == 5 ? "the fifth wrong password is refused and locks this address out" : $"wrong password, attempt {attempt}");
        }

        t.Observe(
            await attacker.PostJsonAsync(
                "/sqlos/dashboard-auth/login",
                new { password = BehaviorLockConstants.DashboardPassword },
                options => options.FromAddress(AttackerAddress)),
            "while locked out even the right password is refused before it is checked");
        t.Observe(
            await attacker.PostJsonAsync(
                "/sqlos/dashboard-auth/login",
                new { password = WrongPassword },
                options => options.FromAddress(AttackerAddress).Header("X-Forwarded-For", "198.51.100.99")),
            "a forwarded-for header the host does not trust does not change the throttled address");
        await ProbeCalls.ObserveOrUnhandledAsync(
            t,
            () => attacker.PostJsonAsync("/sqlos/dashboard-auth/login", "{\"password\":", options => options.FromAddress("198.51.100.67")),
            "a body that is not JSON");
        t.Observe(
            await t.NewBrowser("operator-browser").PostJsonAsync(
                "/sqlos/dashboard-auth/login",
                new { password = BehaviorLockConstants.DashboardPassword },
                options => options.FromAddress(OperatorAddress)),
            "another address still signs in: the global budget is not spent");

        await t.ObserveAuditAsync("failures, the lockout, the refused attempt, and the sign-in");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/dashboard-auth/login")]
    public async Task Failures_from_many_addresses_lock_every_address_out()
    {
        await using var t = await Transcript.StartAsync(
            HostProfiles.DashboardPassword,
            options => options.ConfigureSqlOS = sqlos =>
            {
                // Smaller budgets than the defaults (5 per address and 25 in total within five
                // minutes) keep the transcript short; the global branch is the same code either way.
                sqlos.Dashboard.LoginThrottling.MaxFailuresPerIp = 2;
                sqlos.Dashboard.LoginThrottling.MaxGlobalFailures = 3;
                sqlos.Dashboard.LoginThrottling.Window = TimeSpan.FromSeconds(10);
            });

        // The harness signed the operator in when the transcript started, which reserved one unit
        // of the global budget. Whether a successful sign-in gives its unit back varies from run to
        // run in 7.2.1 (the release matches the bucket's window start and does not always find it),
        // so the scenario lets that sign-in leave the ten-second window first.
        await Task.Delay(TimeSpan.FromSeconds(11));
        t.Note("Eleven seconds after the harness signed the operator in: its sign-in is outside the ten-second window.");
        var attacker = t.NewBrowser("attacker");

        t.Observe(
            await attacker.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = WrongPassword }, options => options.FromAddress("198.51.100.1")),
            "first address, first failure");
        t.Observe(
            await attacker.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = WrongPassword }, options => options.FromAddress("198.51.100.1")),
            "first address, second failure: that address is locked out");
        t.Observe(
            await attacker.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = WrongPassword }, options => options.FromAddress("198.51.100.2")),
            "a second address spends the last of the global budget: everyone is locked out");
        t.Observe(
            await t.NewBrowser("operator-browser").PostJsonAsync(
                "/sqlos/dashboard-auth/login",
                new { password = BehaviorLockConstants.DashboardPassword },
                options => options.FromAddress("198.51.100.3")),
            "a fresh address with the right password is refused by the global lockout");

        await t.ObserveAuditAsync("per-address and global lockouts");
        await t.ApproveAsync();
    }

    /// <summary>
    /// The ticket carries its own expiry: once it passes, the cookie is no session, even though the
    /// browser may still send it. (A three-second session keeps the wait short.)
    /// </summary>
    [Scenario]
    [Covers("POST /sqlos/dashboard-auth/login")]
    [Covers("GET /sqlos/dashboard-auth/session")]
    [Covers("GET /sqlos/admin/fga/api/stats")]
    public async Task An_expired_session_ticket_is_no_session()
    {
        await using var t = await Transcript.StartAsync(
            HostProfiles.DashboardPassword,
            options => options.ConfigureSqlOS = sqlos => sqlos.Dashboard.SessionLifetime = TimeSpan.FromSeconds(3));
        var browser = t.NewBrowser("operator-browser");

        var login = t.Observe(
            await browser.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = BehaviorLockConstants.DashboardPassword }, options => options.FromAddress(OperatorAddress)),
            "sign in to a three-second session");
        var cookie = login.SetCookieValue(SessionCookie)
            ?? throw new InvalidOperationException("The dashboard sign-in set no session cookie.");
        t.Observe(await browser.GetAsync("/sqlos/dashboard-auth/session"), "the session is active");
        await Task.Delay(TimeSpan.FromSeconds(4));
        t.Note("Four seconds later.");
        var stale = t.NewBrowser("stale-cookie");
        stale.SetCookie(SessionCookie, cookie);
        t.Observe(await stale.GetAsync("/sqlos/dashboard-auth/session"), "the expired ticket is no session");
        t.Observe(await stale.GetAsync("/sqlos/admin/fga/api/stats"), "and reads nothing");

        await t.ApproveAsync();
    }

    /// <summary>
    /// The session cookie is a Data Protection ticket that carries only its expiry. Logout deletes
    /// the browser's copy and writes an audit event, but the server keeps no session record to
    /// revoke, so a copy of the cookie taken before logout keeps working until it expires.
    /// </summary>
    [Scenario]
    [Covers("POST /sqlos/dashboard-auth/login")]
    [Covers("GET /sqlos/dashboard-auth/session")]
    [Covers("POST /sqlos/dashboard-auth/logout")]
    [Covers("GET /sqlos/admin/fga/api/stats")]
    public async Task Logout_needs_the_same_origin_proof_and_does_not_revoke_a_copied_cookie()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardPassword);
        var browser = t.NewBrowser("operator-browser");

        var login = t.Observe(
            await browser.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = BehaviorLockConstants.DashboardPassword }),
            "sign in");
        var cookie = login.SetCookieValue(SessionCookie)
            ?? throw new InvalidOperationException("The dashboard sign-in set no session cookie.");
        t.Observe(await browser.GetAsync("/sqlos/dashboard-auth/session"), "the session reports its expiry");
        t.Observe(await browser.GetAsync("/sqlos/admin/fga/api/stats"), "the session reads the FGA API");

        var tampered = t.NewBrowser("tampered-browser");
        tampered.SetCookie(SessionCookie, cookie[..^4] + "AAAA");
        t.Observe(await tampered.GetAsync("/sqlos/dashboard-auth/session"), "a tampered ticket is no session");
        t.Observe(await tampered.GetAsync("/sqlos/admin/fga/api/stats"), "and reads nothing");

        t.Observe(
            await browser.PostJsonAsync("/sqlos/dashboard-auth/logout", new { }),
            "logout with the session cookie but without X-SqlOS-Request is refused");
        t.Observe(
            await browser.PostJsonAsync(
                "/sqlos/dashboard-auth/logout",
                new { },
                options => options.Header("X-SqlOS-Request", "1").WithOrigin("https://evil.example")),
            "logout from a foreign origin is refused");
        t.Observe(
            await browser.PostJsonAsync("/sqlos/dashboard-auth/logout", new { }, options => options.Header("X-SqlOS-Request", "1")),
            "logout with the same-origin proof deletes the cookie");
        t.Observe(await browser.GetAsync("/sqlos/dashboard-auth/session"), "the browser is signed out");

        var copied = t.NewBrowser("copied-cookie");
        copied.SetCookie(SessionCookie, cookie);
        t.Observe(await copied.GetAsync("/sqlos/dashboard-auth/session"), "a copy of the cookie taken before logout is still a session");
        t.Observe(await copied.GetAsync("/sqlos/admin/fga/api/stats"), "and still reads the dashboard APIs");

        await t.ObserveAuditAsync("sign-in, refused logouts, and logout");
        await t.ApproveAsync();
    }
}
