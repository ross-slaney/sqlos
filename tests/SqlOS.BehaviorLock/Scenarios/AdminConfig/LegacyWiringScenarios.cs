using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// The explicit-wiring deployment (<c>AddSqlOS&lt;T&gt;(options)</c> plus a leftover
/// <c>app.MapAuthServer()</c>). The hosting reference calls that leftover call safe and idempotent,
/// but in 7.2.1 it makes SqlOS withdraw its whole core route set, and <c>MapAuthServer()</c> maps
/// only the auth server and its admin API back: the audit-log, transactional-email, and calendar
/// admin APIs and the calendar connect callback are gone. Not filed yet; recorded as it behaves.
/// </summary>
[TestClass]
public sealed class LegacyWiringScenarios
{
    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/stats")]
    public async Task A_manual_MapAuthServer_call_withdraws_the_module_admin_apis_CurrentBehavior_UnfiledDefect()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.LegacyHost);

        t.Observe(await t.Operator.GetAsync("/sqlos/admin/auth/api/stats"), "the auth admin API that MapAuthServer maps answers");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/audit/api/events"), "the audit-log admin API is gone");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/audit/api/events/export.csv"), "and so is the audit export");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/email/api/templates"), "the email template admin API is gone");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/calendar/api/summary"), "the calendar admin API is gone");
        t.Observe(await t.GetAsync("/sqlos/auth/calendar/callback?state=unknown"), "the calendar connect callback is gone");

        await t.ApproveAsync();
    }
}
