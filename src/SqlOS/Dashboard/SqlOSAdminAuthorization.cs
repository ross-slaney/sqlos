using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.Configuration;

namespace SqlOS.Dashboard;

internal sealed class SqlOSAdminRequiredMetadata
{
    public static SqlOSAdminRequiredMetadata Instance { get; } = new();

    private SqlOSAdminRequiredMetadata()
    {
    }
}

internal sealed record SqlOSAdminPublicExceptionMetadata(string Reason);

internal static class SqlOSAdminAuthorizationRouteGroupExtensions
{
    public static RouteGroupBuilder RequireSqlOSAdminAuthorization(this RouteGroupBuilder group)
    {
        group.WithMetadata(SqlOSAdminRequiredMetadata.Instance);
        group.AddEndpointFilter<SqlOSAdminAuthorizationFilter>();
        return group;
    }

    public static RouteGroupBuilder AllowSqlOSAdminPublicException(
        this RouteGroupBuilder group,
        string reason)
    {
        group.WithMetadata(new SqlOSAdminPublicExceptionMetadata(reason));
        return group;
    }
}

internal sealed class SqlOSAdminAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var dashboardOptions = services.GetService<IOptions<SqlOSOptions>>()?.Value.Dashboard
            ?? services.GetService<IOptions<SqlOSAuthServerOptions>>()?.Value.Dashboard
            ?? new SqlOSDashboardOptions();
        var environment = services.GetRequiredService<IHostEnvironment>();

        if (!await IsAuthorizedAsync(context.HttpContext, dashboardOptions, environment))
        {
            return Results.NotFound();
        }

        return await next(context);
    }

    private static async Task<bool> IsAuthorizedAsync(
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

            return options.AuthorizationCallback == null
                || await options.AuthorizationCallback(context);
        }

        if (options.AuthorizationCallback != null)
        {
            return await options.AuthorizationCallback(context);
        }

        return environment.IsDevelopment();
    }
}
