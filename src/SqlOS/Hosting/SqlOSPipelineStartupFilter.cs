using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;
using SqlOS.Fga.Dashboard;
using RootDashboardMiddleware = SqlOS.Dashboard.SqlOSDashboardMiddleware;

namespace SqlOS.Hosting;

/// <summary>
/// Registers SqlOS dashboard middleware and auth server endpoints without requiring app code after <see cref="WebApplicationBuilder.Build"/>.
/// </summary>
internal sealed class SqlOSPipelineStartupFilter : IStartupFilter
{
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

        app.UseMiddleware<RootDashboardMiddleware>(prefix, environment, hostOptions.Dashboard);
        app.UseMiddleware<SqlOSFgaDashboardMiddleware>($"{prefix}/admin/fga", environment, hostOptions.Dashboard);

        next(app);
    };
}
