using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuthServer.Configuration;
using SqlOS.Configuration;

namespace SqlOS.AuthServer.Authentication;

internal static class SqlOSJwtAuthentication
{
    public static void Add(IServiceCollection services, SqlOSOptions hostOptions)
    {
        var surfaces = SqlOSSingleApplicationSurfaces.Describe(hostOptions.AuthServer.Application);
        var api = surfaces.FirstOrDefault(surface => surface.Kind == SqlOSSingleApplicationSurfaceKind.Api);
        var mcp = surfaces.FirstOrDefault(surface => surface.Kind == SqlOSSingleApplicationSurfaceKind.Mcp);

        var authentication = services.AddAuthentication();
        authentication.AddSqlOSJwt(SqlOSJwtDefaults.AuthenticationScheme, options =>
        {
            if (api == null)
            {
                return;
            }

            options.ExpectedAudience = api.Audience;
            options.Realm = api.Realm;
            options.ResourceMetadataUrl = api.MetadataUrl;
        });

        if (mcp != null)
        {
            authentication.AddSqlOSJwt(SqlOSJwtDefaults.McpAuthenticationScheme, options =>
            {
                options.ExpectedAudience = mcp.Audience;
                options.Realm = mcp.Realm;
                options.ResourceMetadataUrl = mcp.MetadataUrl;
            });
        }

        services.PostConfigure<AuthenticationOptions>(options =>
        {
            if (string.IsNullOrEmpty(options.DefaultAuthenticateScheme))
            {
                options.DefaultAuthenticateScheme = SqlOSJwtDefaults.AuthenticationScheme;
            }

            if (string.IsNullOrEmpty(options.DefaultChallengeScheme))
            {
                options.DefaultChallengeScheme = SqlOSJwtDefaults.AuthenticationScheme;
            }

            if (string.IsNullOrEmpty(options.DefaultForbidScheme))
            {
                options.DefaultForbidScheme = SqlOSJwtDefaults.AuthenticationScheme;
            }
        });

        services.AddAuthorization(authorization =>
        {
            authorization.DefaultPolicy = new AuthorizationPolicyBuilder(SqlOSJwtDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build();

            if (mcp != null)
            {
                authorization.AddPolicy(
                    SqlOSJwtDefaults.McpPolicy,
                    policy => policy
                        .AddAuthenticationSchemes(SqlOSJwtDefaults.McpAuthenticationScheme)
                        .RequireAuthenticatedUser());
            }
        });
    }
}
