using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;

namespace SqlOS.BehaviorLock.Infrastructure.Coverage;

/// <summary>A route the coverage gate requires a scenario for.</summary>
/// <param name="Method">The HTTP method, or <c>*</c> for an endpoint that accepts any method.</param>
/// <param name="Template">The endpoint's <c>RoutePattern.RawText</c>, or the manifest template.</param>
/// <param name="Owner"><c>sqlos</c>, <c>host</c> (the behavior-lock host's own routes and probes), or <c>dashboard</c> (manifest).</param>
public sealed record InventoryRoute(string Method, string Template, string Owner)
{
    public string Route => $"{Method} {Template}";
}

/// <summary>
/// Enumerates what each profile exposes: every <see cref="RouteEndpoint"/> in the host's
/// <see cref="EndpointDataSource"/> (method + <c>RoutePattern.RawText</c>), plus the checked-in
/// manifest of routes the dashboard middleware answers by string matching. Building the
/// inventory starts each profile's host without bootstrap, so it needs no database.
/// </summary>
public static class RouteInventory
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<InventoryRoute>>> Endpoints = new(BuildAll);

    public static IReadOnlyDictionary<string, IReadOnlyList<InventoryRoute>> ByProfile => Endpoints.Value;

    /// <summary>The routes each profile exposes, including the dashboard manifest.</summary>
    public static IReadOnlyList<InventoryRoute> ForProfile(string profile)
        => ByProfile[profile].Concat(DashboardManifest.Routes).ToList();

    /// <summary>Every route of every profile, deduplicated, in a stable order.</summary>
    public static IReadOnlyList<InventoryRoute> Union()
        => ByProfile.Values
            .SelectMany(routes => routes)
            .Concat(DashboardManifest.Routes)
            .DistinctBy(route => route.Route)
            .OrderBy(route => route.Template, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .ToList();

    /// <summary>All <c>[Covers]</c> declarations on scenarios in this assembly, with the scenario that declares them.</summary>
    public static IReadOnlyList<(CoversAttribute Cover, MethodInfo Scenario)> DeclaredCoverage()
        => typeof(RouteInventory).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method => method.GetCustomAttribute<ScenarioAttribute>() != null)
            .SelectMany(method => method.GetCustomAttributes<CoversAttribute>().Select(cover => (cover, method)))
            .ToList();

    /// <summary>Whether <paramref name="route"/> has a scenario: same method (or an any-method endpoint) and template.</summary>
    public static bool IsCovered(InventoryRoute route, IReadOnlyCollection<CoversAttribute> covers)
        => covers.Any(cover => string.Equals(cover.Template, route.Template, StringComparison.Ordinal)
                               && (route.Method == "*" || string.Equals(cover.Method, route.Method, StringComparison.Ordinal)));

    private static IReadOnlyDictionary<string, IReadOnlyList<InventoryRoute>> BuildAll()
        => HostProfiles.All.ToDictionary(profile => profile.Name, profile => Build(profile.Name), StringComparer.Ordinal);

    private static IReadOnlyList<InventoryRoute> Build(string profile)
    {
        var app = BehaviorLockHost.Build(new BehaviorLockHostOptions
        {
            Profile = profile,
            Provider = DatabaseProvider.SqlServer,
            ConnectionString = "Server=route-inventory;Database=route-inventory;Trusted_Connection=True",
            RunBootstrap = false
        });
        try
        {
            // Starting builds the pipeline, which is where SqlOS's startup filter maps its endpoints.
            app.StartAsync().GetAwaiter().GetResult();
            var sqlosAssembly = typeof(SqlOS.Configuration.SqlOSOptions).Assembly;
            return app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.RoutePattern.RawText != null)
                .SelectMany(endpoint =>
                {
                    var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                    var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
                    var owner = handler?.DeclaringType?.Assembly == sqlosAssembly ? "sqlos" : "host";
                    return (methods is { Count: > 0 } ? methods : ["*"])
                        .Select(method => new InventoryRoute(method.ToUpperInvariant(), endpoint.RoutePattern.RawText!, owner));
                })
                .DistinctBy(route => route.Route)
                .OrderBy(route => route.Template, StringComparer.Ordinal)
                .ThenBy(route => route.Method, StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

/// <summary>Reads <c>Coverage/dashboard-routes.manifest</c>.</summary>
public static class DashboardManifest
{
    public static string Path => RepositoryPaths.Combine("tests", "SqlOS.BehaviorLock", "Coverage", "dashboard-routes.manifest");

    public static IReadOnlyList<InventoryRoute> Routes { get; } = Load();

    private static IReadOnlyList<InventoryRoute> Load()
    {
        var routes = new List<InventoryRoute>();
        foreach (var raw in File.ReadAllLines(Path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[1].StartsWith('/'))
            {
                throw new InvalidOperationException($"Malformed dashboard manifest line: '{raw}'. Expected 'METHOD /template  description'.");
            }

            routes.Add(new InventoryRoute(parts[0].ToUpperInvariant(), parts[1], "dashboard"));
        }

        return routes;
    }
}

/// <summary>Groups routes by product surface, so coverage work can be split and tracked.</summary>
public static class RouteSurfaces
{
    private static readonly (string Prefix, string Surface)[] Prefixes =
    [
        ("/__probe/", "probes"),
        ("/api/", "host-resource-routes"),
        ("/mcp", "host-resource-routes"),
        ("/resource-api/", "host-resource-routes"),
        ("/billing/", "host-resource-routes"),
        ("/.well-known/oauth-protected-resource", "protected-resource-metadata"),
        ("/sqlos/auth/headless/", "headless-api"),
        ("/sqlos/auth/saml/", "saml"),
        ("/sqlos/auth/sso/", "saml"),
        ("/sqlos/auth/oidc/", "social-oidc"),
        ("/sqlos/auth/login/oidc/", "social-oidc"),
        ("/sqlos/auth/calendar/", "calendar"),
        ("/sqlos/auth/.well-known/", "protocol-discovery"),
        ("/sqlos/auth/token", "protocol-token"),
        ("/sqlos/auth/authorize", "protocol-authorize"),
        ("/sqlos/auth/device", "device-flow"),
        ("/sqlos/auth/register", "dcr"),
        ("/sqlos/auth/userinfo", "protocol-discovery"),
        ("/sqlos/auth/", "hosted-and-public-auth"),
        ("/sqlos/scim/", "scim-protocol"),
        ("/sqlos/admin/auth/sso-portal", "sso-portal"),
        ("/sqlos/admin/auth/api/sso-portal", "sso-portal"),
        ("/sqlos/admin/auth/api/", "admin-api"),
        ("/sqlos/admin/fga/", "fga-dashboard"),
        ("/sqlos/admin/audit/", "audit-admin"),
        ("/sqlos/admin/email/", "email-admin"),
        ("/sqlos/admin/calendar/", "calendar"),
        ("/sqlos", "dashboard")
    ];

    public static string Of(InventoryRoute route)
    {
        foreach (var (prefix, surface) in Prefixes)
        {
            if (route.Template.StartsWith(prefix, StringComparison.Ordinal)
                || string.Equals(route.Template + "/", prefix, StringComparison.Ordinal))
            {
                if (surface == "admin-api" && route.Template.Contains("/scim", StringComparison.Ordinal))
                {
                    return "scim-admin";
                }

                if (surface == "admin-api" && route.Template.Contains("/sso-portal", StringComparison.Ordinal))
                {
                    return "sso-portal";
                }

                return surface;
            }
        }

        return "other";
    }
}
