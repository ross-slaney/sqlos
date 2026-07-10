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

/// <summary>
/// Provides endpoint-mapping extensions for applications that host SqlOS.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Maps the SqlOS auth-server, audit-log administration, and transactional-email
    /// administration endpoints, plus calendar endpoints when calendar integration is enabled.
    /// </summary>
    /// <param name="app">The built ASP.NET Core application.</param>
    /// <returns>The same <paramref name="app"/> instance.</returns>
    /// <remarks>
    /// Call this method once after <see cref="WebApplicationBuilder.Build"/> when using
    /// <see cref="WebApplicationBuilderExtensions.AddSqlOS{TContext}(WebApplicationBuilder,Action{SqlOSOptions})"/>.
    /// </remarks>
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
