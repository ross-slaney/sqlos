using System.Net;
using System.Net.Http.Headers;
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
    private static void MapTokenEndpoints(RouteGroupBuilder auth)
    {
        auth.MapPost("/token", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSDeviceAuthorizationService deviceAuthorizationService,
            SqlOSClientCredentialsService clientCredentialsService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var grantType = form["grant_type"].ToString();

            try
            {
                if (string.Equals(grantType, SqlOSOAuthGrantTypes.ClientCredentials, StringComparison.Ordinal))
                {
                    SqlOSClientCredentialsTokenResult clientResult;
                    try
                    {
                        var (clientId, clientSecret) = ReadClientCredentials(context, form);
                        clientResult = await clientCredentialsService.ExchangeAsync(
                            clientId,
                            clientSecret,
                            form["resource"].ToString(),
                            form["scope"].ToString(),
                            context,
                            cancellationToken);
                    }
                    catch (SqlOSClientCredentialsException ex)
                    {
                        if (ex.StatusCode == StatusCodes.Status401Unauthorized)
                        {
                            context.Response.Headers.WWWAuthenticate = "Basic";
                        }
                        return Results.Json(
                            new { error = ex.Error, error_description = ex.Message },
                            statusCode: ex.StatusCode);
                    }
                    return Results.Ok(new
                    {
                        access_token = clientResult.AccessToken,
                        token_type = "Bearer",
                        expires_in = Math.Max(1, (int)(clientResult.ExpiresAt - DateTime.UtcNow).TotalSeconds),
                        scope = string.Join(' ', clientResult.Scopes)
                    });
                }

                if (string.Equals(grantType, SqlOSOAuthGrantTypes.DeviceCode, StringComparison.Ordinal))
                {
                    var deviceResult = await deviceAuthorizationService.PollAsync(
                        new SqlOSDeviceTokenPollRequest(
                            form["client_id"].ToString(),
                            form["device_code"].ToString(),
                            form["resource"].ToString()),
                        context,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        access_token = deviceResult.Tokens.AccessToken,
                        refresh_token = deviceResult.Tokens.RefreshToken,
                        token_type = "Bearer",
                        expires_in = Math.Max(1, (int)(deviceResult.Tokens.AccessTokenExpiresAt - DateTime.UtcNow).TotalSeconds),
                        scope = deviceResult.Scope ?? string.Empty
                    });
                }

                var result = await authorizationServerService.ExchangeAuthorizationCodeAsync(
                    new SqlOSTokenRequest(
                        grantType,
                        form["code"].ToString(),
                        form["redirect_uri"].ToString(),
                        form["client_id"].ToString(),
                        form["code_verifier"].ToString(),
                        form["refresh_token"].ToString(),
                        form["resource"].ToString()),
                    context,
                    cancellationToken);

                return Results.Ok(new
                {
                    access_token = result.Tokens.AccessToken,
                    refresh_token = result.Tokens.RefreshToken,
                    token_type = "Bearer",
                    expires_in = Math.Max(1, (int)(result.Tokens.AccessTokenExpiresAt - DateTime.UtcNow).TotalSeconds),
                    scope = result.Scope ?? string.Empty
                });
            }
            catch (SqlOSDeviceAuthorizationException ex)
            {
                return Results.BadRequest(BuildDeviceAuthorizationError(ex));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicOAuthTokenErrorAsync(context, ex, cancellationToken);
            }
        });
    }

    private static (string ClientId, string ClientSecret) ReadClientCredentials(
        HttpContext context,
        IFormCollection form)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (AuthenticationHeaderValue.TryParse(authorization, out var parsed)
            && string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(parsed.Parameter))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(parsed.Parameter));
                var separator = decoded.IndexOf(':');
                if (separator > 0)
                {
                    return (
                        WebUtility.UrlDecode(decoded[..separator]),
                        WebUtility.UrlDecode(decoded[(separator + 1)..]));
                }
            }
            catch (FormatException)
            {
            }
        }

        return (string.Empty, string.Empty);
    }
}
