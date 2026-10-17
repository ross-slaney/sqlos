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
    /// Maps the auth server on <paramref name="authEndpoints"/> and the audit, email, and calendar
    /// endpoints on <paramref name="moduleEndpoints"/>. The two builders stay separate so a leftover
    /// <c>MapAuthServer()</c> call can withdraw only the auth-server copy.
    /// </summary>
    internal static void MapSqlOSCoreEndpoints(
        IEndpointRouteBuilder authEndpoints,
        IEndpointRouteBuilder moduleEndpoints,
        SqlOSOptions sqlosOptions,
        SqlOSAuthServerOptions authOptions)
    {
        authEndpoints.MapAuthServer(authOptions.BasePath);
        moduleEndpoints.MapSqlOSAuditLogsAdmin(sqlosOptions.DashboardBasePath);
        moduleEndpoints.MapSqlOSEmailAdmin(sqlosOptions.DashboardBasePath);
        if (sqlosOptions.Calendar.Enabled)
        {
            moduleEndpoints.MapSqlOSCalendarConnect(authOptions.BasePath);
            moduleEndpoints.MapSqlOSCalendarAdmin(sqlosOptions.DashboardBasePath);
        }
    }
}
