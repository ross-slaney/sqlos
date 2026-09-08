using Microsoft.AspNetCore.Routing;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Extensions;
using SqlOS.Calendar.Extensions;
using SqlOS.Configuration;
using SqlOS.Email.Extensions;

namespace SqlOS.Extensions;

/// <summary>
/// Maps SqlOS-owned endpoints for the startup filter.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Maps the auth-server, admin, email, and calendar endpoints under the SqlOS-owned prefixes.
    /// </summary>
    internal static void MapSqlOSCoreEndpoints(
        IEndpointRouteBuilder endpoints,
        SqlOSOptions sqlosOptions,
        SqlOSAuthServerOptions authOptions)
    {
        endpoints.MapAuthServer(authOptions.BasePath);
        endpoints.MapSqlOSAuditLogsAdmin(sqlosOptions.DashboardBasePath);
        endpoints.MapSqlOSEmailAdmin(sqlosOptions.DashboardBasePath);
        if (sqlosOptions.Calendar.Enabled)
        {
            endpoints.MapSqlOSCalendarConnect(authOptions.BasePath);
            endpoints.MapSqlOSCalendarAdmin(sqlosOptions.DashboardBasePath);
        }
    }
}
