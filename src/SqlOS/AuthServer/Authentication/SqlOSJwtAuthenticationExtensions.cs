using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace SqlOS.AuthServer.Authentication;

/// <summary>
/// Registers a SqlOS JWT bearer scheme. Same shape as <c>AddJwtBearer</c>: the scheme's
/// <see cref="SqlOSJwtOptions.ExpectedAudience"/> is the required <c>aud</c>. Use a second
/// scheme when another resource has a different audience. Also registers a same-named
/// authorization policy so <c>RequireAuthorization("Billing")</c> does not combine with
/// the default <c>SqlOS</c> policy.
/// </summary>
public static class SqlOSJwtAuthenticationExtensions
{
    public static AuthenticationBuilder AddSqlOSJwt(
        this AuthenticationBuilder builder,
        Action<SqlOSJwtOptions>? configure = null)
        => builder.AddSqlOSJwt(SqlOSJwtDefaults.AuthenticationScheme, configure);

    public static AuthenticationBuilder AddSqlOSJwt(
        this AuthenticationBuilder builder,
        string authenticationScheme,
        Action<SqlOSJwtOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);
        builder.Services.AddAuthorization(authorization =>
        {
            authorization.AddPolicy(
                authenticationScheme,
                policy => policy
                    .AddAuthenticationSchemes(authenticationScheme)
                    .RequireAuthenticatedUser());
        });
        return builder.AddScheme<SqlOSJwtOptions, SqlOSJwtHandler>(authenticationScheme, configure);
    }
}
