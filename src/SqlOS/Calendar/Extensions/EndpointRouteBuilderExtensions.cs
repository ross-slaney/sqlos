using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.Calendar.Services;
using SqlOS.Configuration;
using SqlOS.Dashboard;

namespace SqlOS.Calendar.Extensions;

public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the browser-facing calendar connect callback under the auth server base path
    /// (<c>{authBasePath}/calendar/callback</c>). The connect flow itself is started
    /// server-side through <see cref="SqlOSCalendarService.StartConnectAsync"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapSqlOSCalendarConnect(
        this IEndpointRouteBuilder endpoints,
        string authBasePath)
    {
        var calendar = endpoints.MapGroup($"{authBasePath.TrimEnd('/')}/calendar");
        calendar.ExcludeFromDescription();

        calendar.MapGet("/callback", async (
            HttpContext context,
            SqlOSCalendarService calendarService,
            CancellationToken cancellationToken) =>
            await calendarService.HandleConnectCallbackAsync(context, cancellationToken));

        return endpoints;
    }

    /// <summary>
    /// Maps the calendar admin API under <c>{dashboardBasePath}/admin/calendar/api</c>,
    /// guarded by the same dashboard session rules as the other admin surfaces.
    /// </summary>
    public static IEndpointRouteBuilder MapSqlOSCalendarAdmin(
        this IEndpointRouteBuilder endpoints,
        string dashboardBasePath)
    {
        var prefix = $"{dashboardBasePath.TrimEnd('/')}/admin/calendar";
        var admin = endpoints.MapGroup(prefix);
        admin.ExcludeFromDescription();

        var api = admin.MapGroup("/api");

        api.MapGet("/connections", async (
            HttpContext context,
            string? search,
            bool? includeRevoked,
            int? page,
            int? pageSize,
            SqlOSCalendarService calendarService,
            IOptions<SqlOSOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value.Dashboard, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await calendarService.GetAdminConnectionsAsync(search, includeRevoked ?? true, page, pageSize, cancellationToken));
        });

        api.MapGet("/connections/{connectionId}", async (
            HttpContext context,
            string connectionId,
            SqlOSCalendarService calendarService,
            IOptions<SqlOSOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value.Dashboard, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await calendarService.GetAdminConnectionAsync(connectionId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/connections/{connectionId}/sync", async (
            HttpContext context,
            string connectionId,
            SqlOSCalendarSyncService syncService,
            IOptions<SqlOSOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value.Dashboard, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await syncService.SyncConnectionAsync(connectionId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/connections/{connectionId}/refresh", async (
            HttpContext context,
            string connectionId,
            SqlOSCalendarService calendarService,
            IOptions<SqlOSOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value.Dashboard, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await calendarService.ForceRefreshAsync(connectionId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/connections/{connectionId}/disconnect", async (
            HttpContext context,
            string connectionId,
            SqlOSCalendarService calendarService,
            IOptions<SqlOSOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value.Dashboard, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await calendarService.DisconnectAsync(connectionId, "admin_disconnected", cancellationToken: cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapGet("/summary", async (
            HttpContext context,
            SqlOSCalendarService calendarService,
            IOptions<SqlOSOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value.Dashboard, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await calendarService.GetAdminSummaryAsync(cancellationToken));
        });

        return endpoints;
    }

    private static async Task<bool> IsAdminAuthorizedAsync(
        HttpContext context,
        SqlOSDashboardOptions options,
        IHostEnvironment environment)
    {
        if (options.AuthMode == SqlOSDashboardAuthMode.Password)
        {
            var sessionService = context.RequestServices.GetService<SqlOSDashboardSessionService>();
            if (sessionService == null || !sessionService.HasActiveSession(context))
            {
                return false;
            }

            if (options.AuthorizationCallback != null)
            {
                return await options.AuthorizationCallback(context);
            }

            return true;
        }

        if (options.AuthorizationCallback != null)
        {
            return await options.AuthorizationCallback(context);
        }

        return environment.IsDevelopment();
    }
}
