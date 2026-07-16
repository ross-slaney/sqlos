using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.AuthServer.Security;
using SqlOS.Configuration;
using SqlOS.Dashboard;

namespace SqlOS.AuthServer.Extensions;

public static partial class EndpointRouteBuilderExtensions
{
    private const int MaxScimPayloadBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapAuthServer(this IEndpointRouteBuilder endpoints, string? pathPrefix = null)
    {
        var authPrefix = (pathPrefix ?? "/sqlos/auth").TrimEnd('/');
        var authOptions = endpoints.ServiceProvider.GetService<IOptions<SqlOSAuthServerOptions>>()?.Value
            ?? new SqlOSAuthServerOptions();
        var adminPrefix = authPrefix.EndsWith("/auth", StringComparison.OrdinalIgnoreCase)
            ? $"{authPrefix[..^5]}/admin/auth"
            : $"{authPrefix}/admin";
        var resolvedHeadlessPath = authOptions.Headless.ResolveApiBasePath(authPrefix);
        var resolvedSsoSetupApiPath = authOptions.SsoPortal.ResolveHeadlessApiBasePath(adminPrefix);

        var auth = endpoints.MapGroup(authPrefix);
        auth.ExcludeFromDescription();
        var hostedForms = auth.MapGroup(string.Empty);
        hostedForms.WithMetadata(SqlOSHostedFormAntiforgeryMetadata.Instance);
        hostedForms.AddEndpointFilter<SqlOSHostedFormAntiforgeryFilter>();

        var headless = endpoints.MapGroup(resolvedHeadlessPath);
        headless.ExcludeFromDescription();

        var adminRoot = endpoints.MapGroup(adminPrefix);
        adminRoot.ExcludeFromDescription();
        var adminApi = adminRoot.MapGroup("/api")
            .RequireSqlOSAdminAuthorization();
        var ssoSetupApi = endpoints.MapGroup(resolvedSsoSetupApiPath);
        ssoSetupApi.ExcludeFromDescription();
        ssoSetupApi.AllowSqlOSAdminPublicException("SSO setup APIs require a scoped portal session.");

        if (authOptions.EnableScim)
        {
            var scim = endpoints.MapGroup(NormalizeScimBasePath(authOptions.ScimBasePath));
            scim.ExcludeFromDescription();
            MapScimEndpoints(scim);
        }

        MapProtocolEndpoints(auth, authPrefix);
        MapHostedPrimaryEndpoints(auth, hostedForms, authPrefix);
        MapEmailOtpEndpoints(auth, hostedForms, authPrefix);
        MapHostedFactorEndpoints(auth, hostedForms, authPrefix);
        MapHostedSignupEndpoints(auth, hostedForms, authPrefix);
        MapHeadlessAuthEndpoints(headless);
        MapTokenAndPublicApiEndpoints(auth, hostedForms, authPrefix, authOptions);

        var ssoPortal = adminRoot.MapGroup("/sso-portal");
        ssoPortal.ExcludeFromDescription();
        ssoPortal.AllowSqlOSAdminPublicException("SSO portal routes require a scoped portal session token.");
        MapSsoPortalEndpoints(adminApi, ssoPortal, ssoSetupApi);

        adminApi.MapGet("/stats", async (
            HttpContext context,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            var authorized = await IsAdminAuthorizedAsync(context, options.Value, environment);
            if (!authorized)
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.GetDashboardSummaryAsync(cancellationToken));
        });

        if (authOptions.EnableScim)
        {
            MapScimAdminEndpoints(adminApi);
        }

        MapAdminIdentityEndpoints(adminApi);
        MapAdminConfigurationEndpoints(adminApi);
        return endpoints;
    }
}
