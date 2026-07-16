using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using SqlOS.Configuration;
using SqlOS.Fga.Dashboard;
using RootDashboardMiddleware = SqlOS.Dashboard.SqlOSDashboardMiddleware;

namespace SqlOS.Hosting;

/// <summary>
/// Registers SqlOS dashboard middleware and auth server endpoints without requiring app code after <see cref="WebApplicationBuilder.Build"/>.
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

        next(app);
    };

    private static bool HasNonLoopbackTrustedProxy(ForwardedHeadersOptions options)
        => options.KnownProxies.Any(address => !IPAddress.IsLoopback(address))
           || options.KnownNetworks.Any(network => !IPAddress.IsLoopback(network.Prefix));
}
