using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Dashboard;

/// <summary>
/// The dashboard shell, its assets, and its deep links as <c>SqlOSDashboardMiddleware</c> and
/// <c>SqlOSFgaDashboardMiddleware</c> serve them under each dashboard authorization model:
/// a host callback (Production), Development without a callback, Production without a callback,
/// and Password mode.
/// </summary>
[TestClass]
public sealed class DashboardShellScenarios
{
    [Scenario]
    [Covers("GET /sqlos")]
    [Covers("GET /sqlos/")]
    [Covers("GET /sqlos/login")]
    [Covers("GET /sqlos/app.js")]
    [Covers("GET /sqlos/style.css")]
    [Covers("GET /sqlos/admin/auth/{*page}")]
    [Covers("GET /sqlos/admin/audit/{*page}")]
    [Covers("GET /sqlos/admin/email/{*page}")]
    [Covers("GET /sqlos/admin/calendar/{*page}")]
    [Covers("GET /sqlos/admin/fga/{*page}")]
    [Covers("GET /sqlos/admin/fga/app.js")]
    [Covers("GET /sqlos/admin/fga/style.css")]
    [Covers("GET /sqlos/dashboard-auth/session")]
    public async Task The_host_callback_admits_the_operator_to_the_shell_deep_links_and_assets()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var op = t.Operator;

        t.Observe(await op.GetAsync("/sqlos"), "the bare prefix redirects to the shell");
        var shell = t.Observe(await op.GetAsync("/sqlos/"), "the shell carries a fresh CSP nonce and the security headers");
        t.ObserveDocument("the values the middleware writes into the shell's bootstrap script", DashboardPages.BootstrapScript(shell));
        t.Observe(await op.GetAsync("/sqlos/login"), "an authorized operator on the login route goes back to the shell");
        foreach (var (path, caption) in DashboardPages.DeepLinks)
        {
            t.Observe(await op.GetAsync(path), caption);
        }

        t.Observe(await op.GetAsync("/sqlos/admin/fga"), "the FGA area root serves the shell too");
        t.Observe(await op.GetAsync("/sqlos/app.js"), "the dashboard script");
        t.Observe(await op.GetAsync("/sqlos/style.css"), "the dashboard stylesheet");
        t.Observe(await op.GetAsync("/sqlos/admin/fga/app.js"), "the FGA dashboard script");
        t.Observe(await op.GetAsync("/sqlos/admin/fga/style.css"), "the FGA dashboard stylesheet");
        t.Observe(await op.GetAsync("/sqlos/dashboard-auth/session"), "callback mode reports the callback's decision and no session expiry");
        t.Observe(await op.GetAsync("/sqlos/no-such-page"), "an unknown path under the prefix falls through to routing and 404");
        t.Observe(await op.GetAsync("/sqlos/admin/auth/no-such-asset.css"), "an asset-like path under an admin area passes through to routing");
        t.Observe(await op.GetAsync("/sqlos/admin/fga/no-such-asset.js"), "the FGA middleware answers a missing asset with 404");
        t.Observe(await op.GetAsync("/sqlos/dashboard-auth"), "the dashboard-auth root is not an endpoint");

        await t.ObserveAuditAsync("reading the dashboard writes no audit events");
        await t.ApproveAsync();
    }

    /// <summary>
    /// Without the operator callback's approval every dashboard path answers 404, except the prefix
    /// redirect and the dashboard-auth routes: the session probe answers, and logout succeeds and
    /// writes an audit event for anyone who calls it, because it clears a cookie without checking
    /// who is asking.
    /// </summary>
    [Scenario]
    [Covers("GET /sqlos")]
    [Covers("GET /sqlos/")]
    [Covers("GET /sqlos/login")]
    [Covers("GET /sqlos/app.js")]
    [Covers("GET /sqlos/style.css")]
    [Covers("GET /sqlos/admin/auth/{*page}")]
    [Covers("GET /sqlos/admin/fga/{*page}")]
    [Covers("GET /sqlos/admin/fga/app.js")]
    [Covers("GET /sqlos/admin/fga/style.css")]
    [Covers("GET /sqlos/admin/fga/api/stats")]
    [Covers("GET /sqlos/dashboard-auth/session")]
    [Covers("POST /sqlos/dashboard-auth/login")]
    [Covers("POST /sqlos/dashboard-auth/logout")]
    public async Task Without_the_operator_callback_the_dashboard_answers_404()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var visitor = t.NewBrowser("visitor");

        t.Observe(await visitor.GetAsync("/sqlos"), "the prefix redirect runs before authorization");
        t.Observe(await visitor.GetAsync("/sqlos/"), "the shell is hidden");
        t.Observe(await visitor.GetAsync("/sqlos/login"), "so is the login route outside Password mode");
        t.Observe(await visitor.GetAsync("/sqlos/app.js"), "assets are hidden");
        t.Observe(await visitor.GetAsync("/sqlos/style.css"), "stylesheet hidden");
        t.Observe(await visitor.GetAsync("/sqlos/admin/auth/users"), "deep links are hidden");
        t.Observe(await visitor.GetAsync("/sqlos/admin/fga/resources"), "FGA deep links are hidden");
        t.Observe(await visitor.GetAsync("/sqlos/admin/fga/app.js"), "the FGA middleware hides its script");
        t.Observe(await visitor.GetAsync("/sqlos/admin/fga/style.css"), "and its stylesheet");
        t.Observe(await visitor.GetAsync("/sqlos/admin/fga/api/stats"), "and its API");
        t.Observe(
            await visitor.GetAsync("/sqlos/", options => options.Header(BehaviorLockConstants.OperatorHeader, "not-the-operator-secret")),
            "the callback rejects a wrong operator credential");
        t.Observe(await visitor.GetAsync("/sqlos/dashboard-auth/session"), "the session probe answers and reports no access");
        t.Observe(
            await visitor.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = "anything" }),
            "password login does not exist outside Password mode");
        t.Observe(
            await visitor.PostJsonAsync("/sqlos/dashboard-auth/logout", new { }),
            "logout succeeds for an anonymous caller and clears the cookie");

        await t.ObserveAuditAsync("the anonymous logout is audited as a dashboard logout");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/")]
    [Covers("GET /sqlos/login")]
    [Covers("GET /sqlos/app.js")]
    [Covers("GET /sqlos/admin/fga/app.js")]
    [Covers("GET /sqlos/admin/fga/api/stats")]
    [Covers("GET /sqlos/dashboard-auth/session")]
    [Covers("POST /sqlos/dashboard-auth/login")]
    [Covers("POST /sqlos/dashboard-auth/logout")]
    public async Task Development_without_a_callback_opens_the_dashboard_to_every_caller()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardDevelopment);
        var visitor = t.NewBrowser("visitor");

        t.Observe(await visitor.GetAsync("/sqlos/"), "any caller gets the shell");
        t.Observe(await visitor.GetAsync("/sqlos/login"), "and is sent from the login route to the shell");
        t.Observe(await visitor.GetAsync("/sqlos/app.js"), "the dashboard script");
        t.Observe(await visitor.GetAsync("/sqlos/admin/fga/app.js"), "the FGA dashboard script");
        t.Observe(await visitor.GetAsync("/sqlos/admin/fga/api/stats"), "the FGA API");
        t.Observe(await visitor.GetAsync("/sqlos/admin/auth/api/stats"), "the admin API");
        t.Observe(await visitor.GetAsync("/sqlos/dashboard-auth/session"), "the session probe reports access without a session");
        t.Observe(
            await visitor.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = "anything" }),
            "password login does not exist outside Password mode");
        t.Observe(await visitor.PostJsonAsync("/sqlos/dashboard-auth/logout", new { }), "logout clears the cookie");

        await t.ObserveAuditAsync("logout events");
        await t.ApproveAsync();
    }

    /// <summary>
    /// The default production deployment: no callback outside Development hides the dashboard and
    /// the admin APIs from everyone. The profile has no operator, so no audit readback.
    /// </summary>
    [Scenario]
    [Covers("GET /sqlos")]
    [Covers("GET /sqlos/")]
    [Covers("GET /sqlos/login")]
    [Covers("GET /sqlos/style.css")]
    [Covers("GET /sqlos/admin/calendar/{*page}")]
    [Covers("GET /sqlos/admin/fga/style.css")]
    [Covers("GET /sqlos/admin/fga/api/stats")]
    [Covers("GET /sqlos/dashboard-auth/session")]
    [Covers("POST /sqlos/dashboard-auth/login")]
    [Covers("POST /sqlos/dashboard-auth/logout")]
    public async Task Production_without_a_callback_hides_the_dashboard_from_everyone()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardOff);
        var visitor = t.NewBrowser("visitor");

        t.Observe(await visitor.GetAsync("/sqlos"), "the prefix redirect still runs");
        t.Observe(await visitor.GetAsync("/sqlos/"), "the shell is hidden");
        t.Observe(await visitor.GetAsync("/sqlos/login"), "the login route is hidden");
        t.Observe(await visitor.GetAsync("/sqlos/style.css"), "assets are hidden");
        t.Observe(await visitor.GetAsync("/sqlos/admin/calendar/connections"), "deep links are hidden");
        t.Observe(await visitor.GetAsync("/sqlos/admin/fga/style.css"), "FGA assets are hidden");
        t.Observe(await visitor.GetAsync("/sqlos/admin/fga/api/stats"), "the FGA API is hidden");
        t.Observe(await visitor.GetAsync("/sqlos/admin/auth/api/stats"), "the admin API is hidden");
        t.Observe(
            await visitor.GetAsync("/sqlos/", options => options.Header(BehaviorLockConstants.OperatorHeader, BehaviorLockConstants.OperatorSecret)),
            "no header opens it: there is no callback to ask");
        t.Observe(await visitor.GetAsync("/sqlos/dashboard-auth/session"), "the session probe reports no access");
        t.Observe(
            await visitor.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = "anything" }),
            "password login does not exist outside Password mode");
        t.Observe(await visitor.PostJsonAsync("/sqlos/dashboard-auth/logout", new { }), "logout still answers 204 and clears the cookie");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/")]
    public async Task The_shell_advertises_scim_when_the_auth_server_enables_it()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);

        var shell = t.Observe(await t.Operator.GetAsync("/sqlos/"), "the enterprise shell");
        t.ObserveDocument("the bootstrap script turns the SCIM capability on", DashboardPages.BootstrapScript(shell));

        await t.ApproveAsync();
    }

    /// <summary>
    /// Known defect #447 (7.2.1), in Password mode: the dashboard middleware claims every path under
    /// <c>/sqlos</c> it does not pass through, including the default SCIM base path, so a directory
    /// with a valid SCIM token gets the dashboard's 401 or login redirect instead of SCIM.
    /// (<c>ScimUserScenarios</c> records the 404 the callback mode answers.)
    /// </summary>
    [Scenario]
    [Covers("GET /sqlos/")]
    public async Task Scim_under_the_dashboard_prefix_gets_the_dashboard_login_in_password_mode_CurrentBehavior_KnownDefect_447()
    {
        await using var t = await Transcript.StartAsync(
            HostProfiles.DashboardPassword,
            options => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.EnableScim = true);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var directory = await t.Setup.CreateScimConnectionAsync(acme);
        var scim = t.NewClient("scim");

        var shell = t.Observe(await t.Operator.GetAsync("/sqlos/"), "the shell advertises SCIM");
        t.ObserveDocument("the bootstrap script turns the SCIM capability on", DashboardPages.BootstrapScript(shell));
        t.Observe(
            await scim.GetAsync("/sqlos/scim/v2/ServiceProviderConfig", options => options.Bearer(directory.Token)),
            "a directory reading the SCIM configuration is redirected to the dashboard login");
        t.Observe(
            await scim.SendAsync(
                HttpMethod.Post,
                "/sqlos/scim/v2/Users",
                new StringContent("""{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"blocked@example.test","active":true}""", System.Text.Encoding.UTF8, "application/scim+json"),
                options => options.Bearer(directory.Token)),
            "and creating a user gets the dashboard's 401");

        await t.ApproveAsync();
    }

    /// <summary>
    /// Password mode: anonymous browsers are sent to the login shell with the page they asked for,
    /// the root dashboard assets stay public so the login page renders, and the FGA middleware
    /// (which serves its own assets) redirects them to the login page instead.
    /// </summary>
    [Scenario]
    [Covers("GET /sqlos")]
    [Covers("GET /sqlos/")]
    [Covers("GET /sqlos/login")]
    [Covers("GET /sqlos/app.js")]
    [Covers("GET /sqlos/style.css")]
    [Covers("GET /sqlos/admin/auth/{*page}")]
    [Covers("GET /sqlos/admin/email/{*page}")]
    [Covers("GET /sqlos/admin/fga/{*page}")]
    [Covers("GET /sqlos/admin/fga/app.js")]
    [Covers("GET /sqlos/admin/fga/style.css")]
    [Covers("GET /sqlos/admin/fga/api/stats")]
    [Covers("GET /sqlos/dashboard-auth/session")]
    [Covers("POST /sqlos/dashboard-auth/login")]
    public async Task Password_mode_sends_anonymous_browsers_to_the_login_shell()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardPassword);
        var browser = t.NewBrowser("operator-browser");

        t.Observe(await browser.GetAsync("/sqlos"), "the prefix redirect runs first");
        t.Observe(await browser.GetAsync("/sqlos/"), "the shell redirects to the login route");
        t.Observe(await browser.GetAsync("/sqlos/login"), "the login route serves the shell without a session");
        t.Observe(await browser.GetAsync("/sqlos/app.js"), "the dashboard script is public so the login page can render");
        t.Observe(await browser.GetAsync("/sqlos/style.css"), "so is the stylesheet");
        t.Observe(await browser.GetAsync("/sqlos/admin/fga/app.js"), "the FGA script redirects to the login route");
        t.Observe(await browser.GetAsync("/sqlos/admin/fga/style.css"), "so does the FGA stylesheet");
        t.Observe(
            await browser.GetAsync("/sqlos/admin/auth/users?tab=sessions&page=2"),
            "a deep link keeps its path and query in the login redirect");
        t.Observe(await browser.GetAsync("/sqlos/admin/fga/resources"), "FGA deep links redirect the same way");
        t.Observe(await browser.GetAsync("/sqlos/admin/fga/api/stats"), "the FGA API answers 401");
        t.Observe(await browser.GetAsync("/sqlos/admin/auth/api/stats"), "the admin API answers 404");
        t.Observe(await browser.GetAsync("/sqlos/admin/email/templates"), "an email deep link redirects");
        t.Observe(
            await browser.SendAsync(HttpMethod.Head, "/sqlos/admin/email/templates", content: null),
            "HEAD redirects like GET");
        t.Observe(await browser.PostJsonAsync("/sqlos/admin/email/templates", new { }), "any other method on a shell path answers 401");
        t.Observe(await browser.GetAsync("/sqlos/dashboard-auth/session"), "the session probe reports no session");
        t.Observe(await browser.GetAsync("/sqlos/dashboard-auth/login"), "sign-in only answers POST");
        t.Observe(await browser.PostJsonAsync("/sqlos/dashboard-auth/session", new { }), "the session probe only answers GET");

        t.Observe(
            await browser.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = BehaviorLockConstants.DashboardPassword }),
            "sign in");
        t.Observe(await browser.GetAsync("/sqlos/login"), "a signed-in operator on the login route goes to the shell");
        t.Observe(await browser.GetAsync("/sqlos/"), "the shell");
        t.Observe(await browser.GetAsync("/sqlos/admin/fga/app.js"), "the FGA script now loads");

        await t.ObserveAuditAsync("sign-in events");
        await t.ApproveAsync();
    }
}
