using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Email.Contracts;
using SqlOS.Email.Services;
using SqlOS.Pagination;

namespace SqlOS.Email.Extensions;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapSqlOSEmailAdmin(
        this IEndpointRouteBuilder endpoints,
        string dashboardBasePath)
    {
        var prefix = $"{dashboardBasePath.TrimEnd('/')}/admin/email";
        var admin = endpoints.MapGroup(prefix);
        admin.ExcludeFromDescription();

        var api = admin.MapGroup("/api")
            .RequireSqlOSAdminAuthorization();

        api.MapGet("/templates", async (
            HttpContext context,
            string? search,
            bool? includeInactive,
            string? cursor,
            int? pageSize,
            int? page,
            SqlOSEmailAdminService emailAdmin,
            IOptions<SqlOSOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value.Dashboard, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => emailAdmin.ListTemplatesAsync(search, includeInactive ?? true, cursor, pageSize, page, cancellationToken));
        });

        api.MapGet("/templates/{templateId}", async (
            HttpContext context,
            string templateId,
            SqlOSEmailAdminService emailAdmin,
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
                return Results.Ok(await emailAdmin.GetTemplateAsync(templateId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/templates", async (
            HttpContext context,
            SqlOSCreateEmailTemplateRequest request,
            SqlOSEmailAdminService emailAdmin,
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
                return Results.Ok(await emailAdmin.CreateTemplateAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPut("/templates/{templateId}", async (
            HttpContext context,
            string templateId,
            SqlOSUpdateEmailTemplateRequest request,
            SqlOSEmailAdminService emailAdmin,
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
                return Results.Ok(await emailAdmin.UpdateTemplateAsync(templateId, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapDelete("/templates/{templateId}", async (
            HttpContext context,
            string templateId,
            SqlOSEmailAdminService emailAdmin,
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
                await emailAdmin.DeleteTemplateAsync(templateId, cancellationToken);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/templates/{templateId}/preview", async (
            HttpContext context,
            string templateId,
            SqlOSPreviewEmailTemplateRequest request,
            SqlOSEmailAdminService emailAdmin,
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
                return Results.Ok(await emailAdmin.PreviewTemplateAsync(templateId, request.Variables, cancellationToken));
            }
            catch (SqlOSEmailTemplateValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message, missingVariables = ex.MissingVariables });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapGet("/messages", async (
            HttpContext context,
            string? status,
            string? templateKey,
            string? recipient,
            DateTime? from,
            DateTime? to,
            string? cursor,
            int? pageSize,
            int? page,
            SqlOSEmailAdminService emailAdmin,
            IOptions<SqlOSOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value.Dashboard, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => emailAdmin.ListMessagesAsync(
                status,
                templateKey,
                recipient,
                from,
                to,
                cursor,
                pageSize,
                page,
                cancellationToken));
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
