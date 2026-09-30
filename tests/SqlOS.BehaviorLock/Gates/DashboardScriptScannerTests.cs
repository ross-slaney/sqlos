using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Infrastructure.Coverage;

namespace SqlOS.BehaviorLock.Gates;

/// <summary>Pins how the dashboard script scan reads app.js, so the coverage gate cannot silently find nothing.</summary>
[TestClass]
[TestCategory("unit")]
public sealed class DashboardScriptScannerTests
{
    [TestMethod]
    public void Root_dashboard_paths_resolve_base_constants_interpolations_and_ternaries()
    {
        const string script = """
            const dashboardBasePath = normalizeBasePath(window.__SQL_OS_BASE_PATH__ || "/sqlos");
            const authDashboardPath = `${dashboardBasePath}/admin/auth`;
            const authApiBasePath = `${authDashboardPath}/api`;
            fetchJson(`${authApiBasePath}/users/${encodeURIComponent(id)}/sessions?${query}`);
            fetchJson(`${authApiBasePath}/sso-connections/${id}/${enabled ? 'enable' : 'disable'}`, { method: 'POST' });
            window.location.href = `${authDashboardPath}/clients`; // navigation, not an API call
            """;

        var paths = DashboardScriptScanner.ScanRootDashboard(script, "app.js").Select(path => path.Path).ToList();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "/sqlos/admin/auth/api/users/{}/sessions",
                "/sqlos/admin/auth/api/sso-connections/{}/enable",
                "/sqlos/admin/auth/api/sso-connections/{}/disable"
            },
            paths);
    }

    [TestMethod]
    public void Fga_dashboard_paths_come_from_api_helpers_fetch_and_picker_endpoints()
    {
        const string script = """
            const basePath = String(options.basePath || '/sqlos/admin/fga').replace(/\/$/, '');
            const stats = await api('stats');
            await apiDelete(`grants/${grantId}`);
            const response = await fetch(`${basePath}/api/trace`, { method: 'POST' });
            const picker = { endpoint: 'roles' };
            const page = await api(`${config.endpoint}?${params.toString()}`);
            """;

        var paths = DashboardScriptScanner.ScanFgaDashboard(script, "fga.js").Select(path => path.Path).ToList();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "/sqlos/admin/fga/api/stats",
                "/sqlos/admin/fga/api/grants/{}",
                "/sqlos/admin/fga/api/roles",
                "/sqlos/admin/fga/api/trace"
            },
            paths);
    }

    [TestMethod]
    public void Paths_match_templates_segment_by_segment()
    {
        Assert.IsTrue(DashboardScriptScanner.Matches("/sqlos/admin/auth/api/users/{}", "/sqlos/admin/auth/api/users/{userId}"));
        Assert.IsTrue(DashboardScriptScanner.Matches("/sqlos/admin/calendar/api/connections/{}/{}", "/sqlos/admin/calendar/api/connections/{connectionId}/sync"));
        Assert.IsTrue(DashboardScriptScanner.Matches("/sqlos/admin/fga/api/anything/else", "/sqlos/admin/fga/{*page}"));
        Assert.IsFalse(DashboardScriptScanner.Matches("/sqlos/admin/fga/api/unknown", "/sqlos/admin/fga/api/stats"));
        Assert.IsFalse(DashboardScriptScanner.Matches("/sqlos/admin/auth/api/users/{}/extra", "/sqlos/admin/auth/api/users/{userId}"));
    }

    [TestMethod]
    public void The_real_dashboards_expose_their_known_calls()
    {
        var paths = DashboardScriptScanner.ScanAll().Select(path => path.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var expected in new[]
                 {
                     "/sqlos/dashboard-auth/login",
                     "/sqlos/admin/auth/api/users",
                     "/sqlos/admin/audit/api/events",
                     "/sqlos/admin/fga/api/trace",
                     "/sqlos/admin/fga/api/grants/{}"
                 })
        {
            Assert.IsTrue(paths.Contains(expected), $"Expected the dashboard scan to find {expected}.");
        }
    }
}
