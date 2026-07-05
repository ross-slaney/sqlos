using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Extensions;
using SqlOS.Calendar.Extensions;
using SqlOS.Configuration;
using SqlOS.Email.Extensions;

namespace SqlOS.Extensions;

public static class WebApplicationExtensions
{
    /// <summary>
    /// Maps SqlOS auth server endpoints. Call once after <see cref="WebApplicationBuilder.Build"/> when using <see cref="WebApplicationBuilderExtensions.AddSqlOS{TContext}"/>.
    /// </summary>
    public static WebApplication MapSqlOS(this WebApplication app)
    {
        var authOptions = app.Services.GetRequiredService<IOptions<SqlOSAuthServerOptions>>().Value;
        var sqlosOptions = app.Services.GetRequiredService<IOptions<SqlOSOptions>>().Value;
        app.MapAuthServer(authOptions.BasePath);
        app.MapSqlOSAuditLogsAdmin(sqlosOptions.DashboardBasePath);
        app.MapSqlOSEmailAdmin(sqlosOptions.DashboardBasePath);
        if (sqlosOptions.Calendar.Enabled)
        {
            app.MapSqlOSCalendarConnect(authOptions.BasePath);
            app.MapSqlOSCalendarAdmin(sqlosOptions.DashboardBasePath);
        }

        return app;
    }
}
