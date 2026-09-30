using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Admin;

[TestClass]
public sealed class AdminUserScenarios
{
    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}")]
    [Covers("GET /sqlos/admin/audit/api/events")]
    public async Task Operator_creates_a_user_and_the_audit_log_records_it()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var email = t.Unique.Email("dana");

        t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/users", new { displayName = "Dana", email, password = t.Unique.Password("dana") },
                options => options.WithoutCredentials()),
            "without operator credentials the admin API is not found");
        var created = t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/users", new { displayName = "Dana", email, password = t.Unique.Password("dana") }),
            "create a user");

        var userId = created.JsonString("id");
        await t.ObserveAuditAsync("user creation events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{userId}", "the user as the admin API reads it");
        t.Observe(
            await t.Operator.GetAsync($"/sqlos/admin/audit/api/events?targetId={userId}"),
            "the audit API filtered to events targeting the new user");
        await t.ApproveAsync();
    }
}
