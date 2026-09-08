using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Endpoints;
using SqlOS.AuthServer.Extensions;
using SqlOS.Configuration;
using SqlOS.Extensions;
using SqlOS.Fga.Dashboard;
using RootDashboardMiddleware = SqlOS.Dashboard.SqlOSDashboardMiddleware;

namespace SqlOS.Hosting;

/// <summary>
/// Registers SqlOS dashboard middleware and adds the auth-server, admin, protected-resource-metadata,
/// and companion-package endpoints to the application's route table without requiring app code
/// after <see cref="WebApplicationBuilder.Build"/>. Declared API/MCP surfaces attach bearer
/// validation to the mapped endpoints under those paths; SqlOS does not add application
/// pipeline middleware for them.
/// </summary>
internal sealed class SqlOSPipelineStartupFilter : IStartupFilter
{
    private readonly ILogger<SqlOSPipelineStartupFilter> _logger;

    public SqlOSPipelineStartupFilter(ILogger<SqlOSPipelineStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var services = app.ApplicationServices;
        var hostOptions = services.GetService<IOptions<SqlOSOptions>>()?.Value;
        if (hostOptions == null)
        {
            next(app);
            return;
        }

        var environment = services.GetRequiredService<IHostEnvironment>();
        var prefix = hostOptions.DashboardBasePath.TrimEnd('/');

        if (hostOptions.Dashboard.AuthMode == SqlOSDashboardAuthMode.DevelopmentOnly
            && hostOptions.Dashboard.AuthorizationCallback == null)
        {
            if (environment.IsDevelopment())
            {
                _logger.LogWarning(
                    "SqlOS dashboard authentication is DevelopmentOnly and the host environment is Development. " +
                    "The dashboard and admin APIs are available without a login. Do not use Development in a production deployment.");
            }
            else
            {
                _logger.LogWarning(
                    "SqlOS dashboard authentication is DevelopmentOnly. The dashboard and admin APIs return 404 outside Development. " +
                    "Configure Dashboard.AuthMode = Password or Dashboard.AuthorizationCallback before exposing operator access.");
            }
        }

        var forwardedHeaders = services.GetService<IOptions<ForwardedHeadersOptions>>()?.Value;
        var publicThrottleSurface =
            hostOptions.Dashboard.AuthMode == SqlOSDashboardAuthMode.Password
            || hostOptions.AuthServer.ClientRegistration.Dcr.Enabled;
        if (publicThrottleSurface
            && forwardedHeaders?.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor) == true
            && !HasNonLoopbackTrustedProxy(forwardedHeaders))
        {
            _logger.LogWarning(
                "SqlOS public throttling is enabled while X-Forwarded-For has no non-loopback KnownProxies or KnownNetworks. " +
                "Configure trusted proxy boundaries or disable X-Forwarded-For processing; untrusted forwarded client addresses can bypass or collapse rate-limit buckets.");
        }

        // Apply only the host-configured, trusted ForwardedHeaders options. The
        // startup filter must do this before dashboard middleware so dashboard
        // throttling and audit events see the external client IP and scheme.
        app.UseForwardedHeaders();
        app.UseMiddleware<RootDashboardMiddleware>(
            prefix,
            environment,
            hostOptions.Dashboard,
            hostOptions.AuthServer.EnableScim);
        app.UseMiddleware<SqlOSFgaDashboardMiddleware>($"{prefix}/admin/fga", environment, hostOptions.Dashboard);

        var mappingState = services.GetService<SqlOSEndpointMappingState>();
        if (mappingState == null)
        {
            // The host did not register SqlOS through AddSqlOS (for example a middleware-only
            // pipeline in tests); there is no endpoint surface to own.
            next(app);
            return;
        }

        // Map the auth-server/admin routes into a SqlOS-owned data source. It withholds them at
        // dispatch time when application code also called MapAuthServer(),
        // whether that call runs before or after this filter, so no route is ever registered twice.
        var application = hostOptions.AuthServer.Application;
        var hostExtensions = application?.HostExtensions ?? [];
        var coreEndpoints = new SqlOSEndpointRouteBuilder(services);
        mappingState.OwnedMappingInProgress = true;
        try
        {
            WebApplicationExtensions.MapSqlOSCoreEndpoints(coreEndpoints, hostOptions, hostOptions.AuthServer);
        }
        finally
        {
            mappingState.OwnedMappingInProgress = false;
        }

        var sharedEndpoints = new SqlOSEndpointRouteBuilder(services);
        sharedEndpoints.MapSqlOSProtectedResourceMetadata(hostOptions.AuthServer);
        foreach (var extension in hostExtensions)
        {
            extension.MapEndpoints(sharedEndpoints, hostOptions);
        }

        var sqlosEndpoints = ProtectSurfaceEndpoints(
            new SqlOSEndpointDataSource(
                mappingState,
                coreEndpoints.DataSources.ToArray(),
                sharedEndpoints.DataSources.ToArray()),
            hostOptions);

        // Let the application configure its pipeline first. WebApplication wraps it in
        // UseRouting()/UseEndpoints() when the application mapped endpoints of its own and leaves
        // the route builder it used (the WebApplication itself) in the builder properties.
        next(app);

        if (app.Properties.TryGetValue(EndpointRouteBuilderKey, out var value)
            && value is IEndpointRouteBuilder applicationRoutes)
        {
            // Join the application's route table so SqlOS endpoints are dispatched by the same
            // routing pass as the application's, behind its middleware (CORS, exception handling,
            // rate limiting) and with normal route precedence over catch-all fallbacks. The
            // routing middleware snapshots the data sources when the pipeline is built, which
            // happens after every startup filter has run. UseEndpoints() only registers the new
            // sources with the global EndpointDataSource; it ignores ones already present.
            ProtectMappedEndpoints(applicationRoutes.DataSources, hostOptions);
            applicationRoutes.DataSources.Add(sqlosEndpoints);
            app.UseEndpoints(static _ => { });
            return;
        }

        // The application mapped nothing itself, so WebApplication added no routing pass. SqlOS
        // appends one after the application's middleware; unmatched requests still end in 404.
        app.UseRouting();
        app.UseEndpoints(endpoints => endpoints.DataSources.Add(sqlosEndpoints));
    };

    private static EndpointDataSource ProtectSurfaceEndpoints(EndpointDataSource source, SqlOSOptions hostOptions)
    {
        var surfaces = SqlOSSingleApplicationSurfaces.Describe(hostOptions.AuthServer.Application);
        return surfaces.Count == 0 ? source : new SqlOSSurfaceEndpointDataSource(source, surfaces);
    }

    private static void ProtectMappedEndpoints(ICollection<EndpointDataSource> sources, SqlOSOptions hostOptions)
    {
        var surfaces = SqlOSSingleApplicationSurfaces.Describe(hostOptions.AuthServer.Application);
        if (surfaces.Count == 0)
        {
            return;
        }

        var existing = sources.ToArray();
        sources.Clear();
        foreach (var source in existing)
        {
            sources.Add(source is SqlOSSurfaceEndpointDataSource
                ? source
                : new SqlOSSurfaceEndpointDataSource(source, surfaces));
        }
    }

    /// <summary>
    /// The <see cref="IApplicationBuilder.Properties"/> key under which <c>UseRouting()</c> records
    /// the <see cref="IEndpointRouteBuilder"/> it routes for and <c>UseEndpoints()</c> reads it back.
    /// </summary>
    private const string EndpointRouteBuilderKey = "__EndpointRouteBuilder";

    private static bool HasNonLoopbackTrustedProxy(ForwardedHeadersOptions options)
        => options.KnownProxies.Any(address => !IPAddress.IsLoopback(address))
           || options.KnownNetworks.Any(network => !IPAddress.IsLoopback(network.Prefix));
}
