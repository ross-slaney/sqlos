using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure;
using SqlOS.BehaviorLock.Infrastructure.Coverage;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;

namespace SqlOS.BehaviorLock.Gates;

/// <summary>
/// The route coverage gate. Every method and route any profile exposes (every
/// <c>EndpointDataSource</c> route plus the string-routed dashboard manifest) needs at least one
/// <c>[Covers]</c> scenario that ran in a profile exposing it, the dashboard scripts may only call
/// known routes, and every approved transcript must belong to a scenario.
/// </summary>
[TestClass]
[TestCategory("gate")]
public sealed class CoverageGateTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Every_route_in_every_profile_has_a_scenario()
    {
        var missing = UncoveredRoutes();
        if (missing.Count > 0)
        {
            Assert.Fail($"{missing.Count} routes have no [Covers] scenario:\n" + string.Join('\n', missing.Select(route => route.Route)));
        }
    }

    /// <summary>
    /// Always passes. Writes the per-profile route inventory and the uncovered routes to
    /// <c>TestResults/BehaviorLock/route-coverage/</c> so the catalog can be split and tracked.
    /// </summary>
    [TestMethod]
    public void Route_coverage_report()
    {
        var declared = RouteInventory.DeclaredCoverage();
        var covers = declared.Select(item => item.Cover).ToList();
        var report = new StringBuilder();
        report.Append("# Behavior-lock route coverage\n\n");
        report.Append("Covered: a scenario in any profile covers the route. Exercised here: a scenario that ran in this profile covers it.\n\n");
        report.Append("| Profile | Endpoint routes | SqlOS | Host | With dashboard manifest | Covered | Scenarios | Exercised here |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var profile in HostProfiles.All)
        {
            var endpoints = RouteInventory.ByProfile[profile.Name];
            var all = RouteInventory.ForProfile(profile.Name);
            var here = declared
                .Where(item => ApprovedScenarioProfiles.Of(item.Scenario) == profile.Name)
                .ToList();
            var hereCovers = here.Select(item => item.Cover).ToList();
            var scenarios = ApprovedScenarioProfiles.ByScenario.Values.Count(name => name == profile.Name);
            report.Append($"| {profile.Name} | {endpoints.Count} | {endpoints.Count(route => route.Owner == "sqlos")} | " +
                          $"{endpoints.Count(route => route.Owner == "host")} | {all.Count} | {all.Count(route => RouteInventory.IsCovered(route, covers))} | " +
                          $"{scenarios} | {all.Count(route => RouteInventory.IsCovered(route, hereCovers))} |\n");
        }

        var union = RouteInventory.Union();
        var missing = UncoveredRoutes();
        report.Append($"\nUnion of all profiles: {union.Count} routes, {union.Count - missing.Count} covered, {missing.Count} uncovered.\n\n");
        report.Append("## Uncovered routes by surface\n");
        foreach (var surface in missing.GroupBy(RouteSurfaces.Of).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            report.Append($"\n### {surface.Key} ({surface.Count()})\n\n");
            foreach (var route in surface)
            {
                report.Append($"- `{route.Route}` (profiles: {string.Join(", ", ProfilesExposing(route))})\n");
            }
        }

        var directory = RepositoryPaths.Combine("TestResults", "BehaviorLock", "route-coverage");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "route-coverage.md"), report.ToString());
        File.WriteAllLines(Path.Combine(directory, "uncovered-routes.txt"), missing.Select(route => $"{RouteSurfaces.Of(route)}\t{route.Route}"));
        TestContext.WriteLine(report.ToString());
    }

    [TestMethod]
    public void Dashboard_scripts_only_call_known_routes()
    {
        var templates = RouteInventory.Union().Select(route => route.Template).Distinct(StringComparer.Ordinal).ToList();
        var scanned = DashboardScriptScanner.ScanAll();
        Assert.IsTrue(scanned.Count > 50, $"The dashboard script scan found only {scanned.Count} paths; the scanner no longer understands app.js.");
        var unknown = scanned
            .Where(path => !templates.Any(template => DashboardScriptScanner.Matches(path.Path, template)))
            .ToList();
        if (unknown.Count > 0)
        {
            Assert.Fail(
                "The dashboard scripts call paths that are neither mapped endpoints nor listed in " +
                "tests/SqlOS.BehaviorLock/Coverage/dashboard-routes.manifest:\n" +
                string.Join('\n', unknown.Select(path => $"{path.Path}  ({path.Source})")));
        }
    }

    [TestMethod]
    public void Every_covers_declaration_names_a_known_route()
    {
        var known = RouteInventory.Union();
        var unknown = RouteInventory.DeclaredCoverage()
            .Where(item => !known.Any(route => route.Template == item.Cover.Template
                                               && (route.Method == "*" || route.Method == item.Cover.Method)))
            .Select(item => $"{item.Cover.Route}  ({item.Scenario.DeclaringType!.Name}.{item.Scenario.Name})")
            .ToList();
        if (unknown.Count > 0)
        {
            Assert.Fail("[Covers] names routes no profile exposes (check the method and the exact RoutePattern.RawText):\n" + string.Join('\n', unknown));
        }
    }

    /// <summary>
    /// A scenario's <c>[Covers]</c> routes must exist in the profile it runs in, which its approved
    /// transcript names. Together with <see cref="Every_route_in_every_profile_has_a_scenario"/> this
    /// proves, without running anything, that every route was exercised in a profile that exposes it.
    /// </summary>
    [TestMethod]
    public void Every_scenario_covers_routes_its_own_profile_exposes()
    {
        var mismatched = RouteInventory.DeclaredCoverage()
            .Select(item => (item.Cover, item.Scenario, Profile: ApprovedScenarioProfiles.Of(item.Scenario)))
            .Where(item => item.Profile != null
                           && !RouteInventory.ForProfile(item.Profile).Any(route => route.Template == item.Cover.Template
                                                                                     && (route.Method == "*" || route.Method == item.Cover.Method)))
            .Select(item => $"{item.Cover.Route}  ({item.Scenario.DeclaringType!.Name}.{item.Scenario.Name} runs in {item.Profile})")
            .ToList();
        if (mismatched.Count > 0)
        {
            Assert.Fail("[Covers] names routes the scenario's own profile does not expose:\n" + string.Join('\n', mismatched));
        }
    }

    [TestMethod]
    public void Approved_transcripts_belong_to_a_scenario()
    {
        var scenarioNames = typeof(CoverageGateTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method => method.GetCustomAttribute<ScenarioAttribute>() != null)
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToHashSet(StringComparer.Ordinal);
        var scenarioRoot = RepositoryPaths.Combine("tests", "SqlOS.BehaviorLock", "Scenarios");
        var orphans = Directory.EnumerateFiles(scenarioRoot, "*.verified.txt", SearchOption.AllDirectories)
            .Where(path => !scenarioNames.Contains(Path.GetFileName(path)[..^".verified.txt".Length]))
            .Select(RepositoryPaths.Relative)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (orphans.Count > 0)
        {
            Assert.Fail("Approved transcripts without a [Scenario] (renamed or deleted scenarios leave these behind):\n" + string.Join('\n', orphans));
        }
    }

    private static IReadOnlyList<InventoryRoute> UncoveredRoutes()
    {
        var covers = RouteInventory.DeclaredCoverage().Select(item => item.Cover).ToList();
        return RouteInventory.Union().Where(route => !RouteInventory.IsCovered(route, covers)).ToList();
    }

    private static IEnumerable<string> ProfilesExposing(InventoryRoute route)
        => route.Owner == "dashboard"
            ? ["all"]
            : HostProfiles.All
                .Where(profile => RouteInventory.ByProfile[profile.Name].Any(candidate => candidate.Route == route.Route))
                .Select(profile => profile.Name);
}
