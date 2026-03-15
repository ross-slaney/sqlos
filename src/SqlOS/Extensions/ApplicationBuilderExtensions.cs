using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;
using SqlOS.Services;
using AuthServerDashboardMiddleware = SqlOS.AuthServer.Dashboard.SqlOSDashboardMiddleware;
using RootDashboardMiddleware = SqlOS.Dashboard.SqlOSDashboardMiddleware;
using SqlOSFgaDashboardMiddleware = SqlOS.Fga.Dashboard.SqlOSFgaDashboardMiddleware;

namespace SqlOS.Extensions;

public static class ApplicationBuilderExtensions
{
    public static async Task UseSqlOSAuthServerAsync(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<SqlOSAuthServerBootstrapper>();
        await bootstrapper.InitializeAsync();
    }

    public static async Task UseSqlOSAsync(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<SqlOSBootstrapper>();
        await bootstrapper.InitializeAsync();
    }

    public static IApplicationBuilder UseSqlOSAuthServerDashboard(this IApplicationBuilder app, string? pathPrefix = null)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<SqlOSOptions>>().Value;
        var authOptions = app.ApplicationServices.GetRequiredService<IOptions<SqlOS.AuthServer.Configuration.SqlOSAuthServerOptions>>().Value;
        var environment = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
        var prefix = (pathPrefix ?? "/sqlos/admin/auth").TrimEnd('/');

        // Root dashboard at /sqlos serves the shell and login page; sub-dashboards redirect there when unauthorized
        var rootPrefix = prefix.Contains("/admin/auth", StringComparison.OrdinalIgnoreCase)
            ? prefix[..prefix.IndexOf("/admin/auth", StringComparison.OrdinalIgnoreCase)].TrimEnd('/')
            : "/sqlos";
        if (string.IsNullOrEmpty(rootPrefix)) rootPrefix = "/sqlos";

        app.UseMiddleware<RootDashboardMiddleware>(rootPrefix, environment, options.Dashboard);
        app.UseMiddleware<AuthServerDashboardMiddleware>(prefix, environment, authOptions.Dashboard);
        return app;
    }

    public static IApplicationBuilder UseSqlOSDashboard(this IApplicationBuilder app, string? pathPrefix = null)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<SqlOSOptions>>().Value;
        var authOptions = app.ApplicationServices.GetRequiredService<IOptions<SqlOS.AuthServer.Configuration.SqlOSAuthServerOptions>>().Value;
        var fgaOptions = app.ApplicationServices.GetRequiredService<IOptions<SqlOS.Fga.Configuration.SqlOSFgaOptions>>().Value;
        var environment = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
        var prefix = (pathPrefix ?? options.DashboardBasePath).TrimEnd('/');

        app.UseMiddleware<RootDashboardMiddleware>(prefix, environment, options.Dashboard);

        if (options.EnableAuthServer)
        {
            app.UseMiddleware<AuthServerDashboardMiddleware>($"{prefix}/admin/auth", environment, authOptions.Dashboard);
        }

        if (options.EnableFga)
        {
            app.UseMiddleware<SqlOSFgaDashboardMiddleware>($"{prefix}/admin/fga", environment, fgaOptions.Dashboard);
        }

        return app;
    }
}
