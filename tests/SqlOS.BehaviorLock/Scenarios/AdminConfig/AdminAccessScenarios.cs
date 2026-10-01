using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// Operator access to the configuration route groups without an authorization callback or a
/// dashboard password. <c>Dashboard.AuthMode = DevelopmentOnly</c> opens them in the Development
/// environment and hides them (404) everywhere else. The auth admin, audit, email, and calendar
/// groups each apply the shared admin filter and, in their handlers, their own copy of the same
/// rule, so each group is locked separately.
/// </summary>
[TestClass]
public sealed class AdminAccessScenarios
{
    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/settings/security")]
    [Covers("GET /sqlos/admin/audit/api/events")]
    [Covers("GET /sqlos/admin/email/api/templates")]
    [Covers("GET /sqlos/admin/calendar/api/summary")]
    [Covers("GET /sqlos/admin/audit/{*page}")]
    public async Task Development_hosts_open_the_configuration_apis_without_operator_credentials()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardDevelopment);

        t.Observe(await t.Api.GetAsync("/sqlos/admin/auth/api/settings/security"), "the auth admin API answers an anonymous request");
        t.Observe(await t.Api.GetAsync("/sqlos/admin/audit/api/events?source=nothing"), "so does the audit API");
        t.Observe(await t.Api.GetAsync("/sqlos/admin/email/api/templates?search=nothing"), "and the email API");
        t.Observe(await t.Api.GetAsync("/sqlos/admin/calendar/api/summary"), "and the calendar API");
        t.Observe(await t.Api.GetAsync("/sqlos/admin/audit/events"), "and the dashboard pages");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/settings/security")]
    [Covers("PUT /sqlos/admin/auth/api/settings/security")]
    [Covers("GET /sqlos/admin/audit/api/events")]
    [Covers("GET /sqlos/admin/email/api/templates")]
    [Covers("POST /sqlos/admin/email/api/templates")]
    [Covers("GET /sqlos/admin/calendar/api/summary")]
    [Covers("GET /sqlos/admin/calendar/{*page}")]
    public async Task Production_hosts_without_operator_access_hide_the_configuration_apis()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardOff);

        t.Observe(await t.Api.GetAsync("/sqlos/admin/auth/api/settings/security"), "the auth admin API is not found");
        t.Observe(
            await t.Api.PutJsonAsync("/sqlos/admin/auth/api/settings/security", new
            {
                refreshTokenLifetimeMinutes = 1,
                sessionIdleTimeoutMinutes = 1,
                sessionAbsoluteLifetimeMinutes = 1,
                signingKeyRotationIntervalDays = 2,
                signingKeyGraceWindowDays = 1,
                signingKeyRetiredCleanupDays = 1,
                refreshTokenGraceWindowSeconds = 0
            }),
            "nor can it be written");
        t.Observe(await t.Api.GetAsync("/sqlos/admin/audit/api/events"), "the audit API is not found");
        t.Observe(await t.Api.GetAsync("/sqlos/admin/email/api/templates"), "the email API is not found");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/admin/email/api/templates", new
            {
                key = "hidden",
                displayName = "Hidden",
                subjectTemplate = "Hidden",
                htmlBodyTemplate = "<p>Hidden</p>",
                textBodyTemplate = "Hidden"
            }),
            "nor can a template be created");
        t.Observe(await t.Api.GetAsync("/sqlos/admin/calendar/api/summary"), "the calendar API is not found");
        t.Observe(await t.Api.GetAsync("/sqlos/admin/calendar/connections"), "and neither are the dashboard pages");

        await t.ApproveAsync();
    }
}
