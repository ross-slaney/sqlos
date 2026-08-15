using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Pagination;

namespace SqlOS.AuditLogs;

public static class SqlOSAuditLogEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapSqlOSAuditLogsAdmin(
        this IEndpointRouteBuilder endpoints,
        string dashboardBasePath)
    {
        var adminPrefix = $"{dashboardBasePath.TrimEnd('/')}/admin/audit";
        var api = endpoints.MapGroup($"{adminPrefix}/api")
            .RequireSqlOSAdminAuthorization();
        api.ExcludeFromDescription();

        api.MapGet("/events", async (
            HttpContext context,
            ISqlOSAuditLogService auditLogs,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            string? cursor,
            int? page,
            int? pageSize,
            string? organizationId,
            string? applicationId,
            string? applicationKey,
            string? application,
            string? source,
            string? action,
            string? actorType,
            string? actorId,
            string? targetType,
            string? targetId,
            string? result,
            string? search,
            DateTime? occurredAtFrom,
            DateTime? occurredAtTo,
            DateTime? from,
            DateTime? to,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(async () => (object)await auditLogs.ListAsync(
                BuildListRequest(
                    cursor,
                    page,
                    pageSize,
                    organizationId,
                    applicationId,
                    applicationKey,
                    application,
                    source,
                    action,
                    actorType,
                    actorId,
                    targetType,
                    targetId,
                    result,
                    search,
                    occurredAtFrom ?? from,
                    occurredAtTo ?? to),
                cancellationToken));
        });

        api.MapGet("/events/export.csv", async (
            HttpContext context,
            ISqlOSAuditLogService auditLogs,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            string? organizationId,
            string? applicationId,
            string? applicationKey,
            string? application,
            string? source,
            string? action,
            string? actorType,
            string? actorId,
            string? targetType,
            string? targetId,
            string? result,
            string? search,
            DateTime? occurredAtFrom,
            DateTime? occurredAtTo,
            DateTime? from,
            DateTime? to,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var export = await auditLogs.ExportCsvAsync(
                    BuildListRequest(
                        cursor: null,
                        page: 1,
                        pageSize: SqlOSAuditLogService.MaxExportRows,
                        organizationId,
                        applicationId,
                        applicationKey,
                        application,
                        source,
                        action,
                        actorType,
                        actorId,
                        targetType,
                        targetId,
                        result,
                        search,
                        occurredAtFrom ?? from,
                        occurredAtTo ?? to),
                    cancellationToken);
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"{export.FileName}\"";
                return Results.Text(export.Content, "text/csv; charset=utf-8");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapGet("/events/{id}", async (
            string id,
            HttpContext context,
            ISqlOSAuditLogService auditLogs,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var item = await auditLogs.GetAsync(id, cancellationToken);
            return item == null ? Results.NotFound() : Results.Ok(item);
        });

        return endpoints;
    }

    private static SqlOSAuditLogListRequest BuildListRequest(
        string? cursor,
        int? page,
        int? pageSize,
        string? organizationId,
        string? applicationId,
        string? applicationKey,
        string? application,
        string? source,
        string? action,
        string? actorType,
        string? actorId,
        string? targetType,
        string? targetId,
        string? result,
        string? search,
        DateTime? occurredAtFrom,
        DateTime? occurredAtTo)
        => new(
            Cursor: cursor,
            Page: page,
            PageSize: pageSize,
            OrganizationId: organizationId,
            ApplicationId: applicationId,
            ApplicationKey: applicationKey,
            Application: application,
            Source: source,
            Action: action,
            ActorType: actorType,
            ActorId: actorId,
            TargetType: targetType,
            TargetId: targetId,
            Result: result,
            Search: search,
            OccurredAtFrom: occurredAtFrom,
            OccurredAtTo: occurredAtTo);

    private static async Task<bool> IsAdminAuthorizedAsync(
        HttpContext context,
        SqlOSAuthServerOptions options,
        IHostEnvironment environment)
    {
        if (options.Dashboard.AuthMode == SqlOSDashboardAuthMode.Password)
        {
            var sessionService = context.RequestServices.GetService<SqlOSDashboardSessionService>();
            if (sessionService == null || !sessionService.HasActiveSession(context))
            {
                return false;
            }

            if (options.Dashboard.AuthorizationCallback != null)
            {
                return await options.Dashboard.AuthorizationCallback(context);
            }

            return true;
        }

        if (options.Dashboard.AuthorizationCallback != null)
        {
            return await options.Dashboard.AuthorizationCallback(context);
        }

        return environment.IsDevelopment();
    }
}
