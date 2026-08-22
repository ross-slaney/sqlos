using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Extensions;

public static partial class EndpointRouteBuilderExtensions
{
    private static void MapOpenIdProviderEndpoints(RouteGroupBuilder auth, SqlOSAuthServerOptions authOptions)
    {
        if (!authOptions.OpenIdProvider.Enabled)
        {
            return;
        }

        if (authOptions.OpenIdProvider.PublishDiscoveryDocument)
        {
            auth.MapGet("/.well-known/openid-configuration", async (
                HttpContext context,
                SqlOSAuthorizationServerService authorizationServerService,
                CancellationToken cancellationToken) =>
                Results.Ok(await authorizationServerService.GetMetadataAsync(context, cancellationToken)));
        }

        if (authOptions.OpenIdProvider.EnableUserInfoEndpoint)
        {
            auth.MapGet("/userinfo", HandleUserInfoAsync);
            auth.MapPost("/userinfo", HandleUserInfoAsync);
        }
    }

    private static async Task<IResult> HandleUserInfoAsync(
        HttpContext context,
        SqlOSUserInfoService userInfoService,
        CancellationToken cancellationToken)
    {
        // OIDC Core §5.3.2: UserInfo responses carry identity PII, so every
        // response — success or challenge — forbids caching.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        var (bearerToken, isAmbiguous) = await ReadUserInfoBearerTokenAsync(context, cancellationToken);
        var result = isAmbiguous
            ? SqlOSUserInfoResult.Challenge(
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "Use exactly one access-token transport: the Authorization header or the access_token form parameter, not both.")
            : await userInfoService.GetUserInfoAsync(bearerToken, cancellationToken);
        if (result.Claims != null)
        {
            return Results.Json(result.Claims);
        }

        context.Response.Headers.WWWAuthenticate = BuildUserInfoBearerChallenge(result);
        return Results.Json(
            result.Error == null
                ? new { }
                : (object)new { error = result.Error, error_description = result.ErrorDescription },
            statusCode: result.StatusCode);
    }

    /// <summary>
    /// RFC 6750: the token arrives in the Authorization header, or — for POST with a
    /// form body — as an <c>access_token</c> parameter. §2 forbids using more than one
    /// transport in the same request, so a POST carrying both a Bearer header and an
    /// <c>access_token</c> form field is reported as ambiguous rather than silently
    /// resolved in favor of either credential.
    /// </summary>
    private static async Task<(string? Token, bool Ambiguous)> ReadUserInfoBearerTokenAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var headerToken = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;

        if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            if (form.ContainsKey("access_token"))
            {
                if (headerToken != null)
                {
                    return (null, true);
                }

                var formToken = form["access_token"].ToString();
                return string.IsNullOrWhiteSpace(formToken) ? (null, false) : (formToken, false);
            }
        }

        return (headerToken, false);
    }

    private static string BuildUserInfoBearerChallenge(SqlOSUserInfoResult result)
    {
        if (result.Error == null)
        {
            return "Bearer";
        }

        var challenge = $"Bearer error=\"{result.Error}\"";
        if (!string.IsNullOrWhiteSpace(result.ErrorDescription))
        {
            challenge += $", error_description=\"{result.ErrorDescription}\"";
        }

        return challenge;
    }
}
