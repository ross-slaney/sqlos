using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Dashboard;

[TestClass]
public sealed class DashboardPasswordScenarios
{
    [Scenario]
    [Covers("GET /sqlos/admin/fga/api/stats")]
    [Covers("POST /sqlos/dashboard-auth/login")]
    [Covers("GET /sqlos/dashboard-auth/session")]
    [Covers("GET /sqlos/admin/auth/api/stats")]
    [Covers("POST /sqlos/dashboard-auth/logout")]
    public async Task Operator_signs_in_reads_dashboard_apis_and_signs_out()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardPassword);
        var operatorBrowser = t.NewBrowser("operator-browser");

        t.Observe(await operatorBrowser.GetAsync("/sqlos/admin/fga/api/stats"), "the FGA dashboard API needs a session");
        t.Observe(
            await operatorBrowser.PostJsonAsync("/sqlos/dashboard-auth/login", new { password = BehaviorLockConstants.DashboardPassword }),
            "sign in with the dashboard password");
        t.Observe(await operatorBrowser.GetAsync("/sqlos/dashboard-auth/session"), "read the session");
        t.Observe(await operatorBrowser.GetAsync("/sqlos/admin/auth/api/stats"), "read auth dashboard stats");
        t.Observe(await operatorBrowser.GetAsync("/sqlos/admin/fga/api/stats"), "read FGA dashboard stats");
        t.Observe(
            await operatorBrowser.PostJsonAsync("/sqlos/dashboard-auth/logout", new { }, options => options.Header("X-SqlOS-Request", "1")),
            "sign out with the same-origin proof");
        t.Observe(await operatorBrowser.GetAsync("/sqlos/admin/auth/api/stats"), "the session no longer reads admin APIs");

        await t.ObserveAuditAsync("dashboard session events");
        await t.ApproveAsync();
    }
}
