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
            SqlOSClientAuthenticationService clientAuthenticationService,
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
                        var client = await clientAuthenticationService.AuthenticateTokenEndpointClientAsync(
                            form,
                            context,
                            cancellationToken);
                        clientResult = await clientCredentialsService.ExchangeAsync(
                            client,
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

                    var deviceResponse = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["access_token"] = deviceResult.Tokens.AccessToken,
                        ["refresh_token"] = deviceResult.Tokens.RefreshToken,
                        ["token_type"] = "Bearer",
                        ["expires_in"] = Math.Max(1, (int)(deviceResult.Tokens.AccessTokenExpiresAt - DateTime.UtcNow).TotalSeconds),
                        ["scope"] = deviceResult.Scope ?? string.Empty
                    };
                    if (!string.IsNullOrEmpty(deviceResult.Tokens.IdToken))
                    {
                        deviceResponse["id_token"] = deviceResult.Tokens.IdToken;
                    }

                    return Results.Ok(deviceResponse);
                }

                if (!string.Equals(grantType, SqlOSOAuthGrantTypes.AuthorizationCode, StringComparison.Ordinal)
                    && !string.Equals(grantType, SqlOSOAuthGrantTypes.RefreshToken, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Unsupported grant type.");
                }

                var clientId = string.Equals(grantType, SqlOSOAuthGrantTypes.RefreshToken, StringComparison.Ordinal)
                    ? await clientAuthenticationService.AuthenticateRefreshGrantClientAsync(
                        form,
                        context,
                        cancellationToken)
                    : (await clientAuthenticationService.AuthenticateTokenEndpointClientAsync(
                        form,
                        context,
                        cancellationToken)).ClientId;
                var result = await authorizationServerService.ExchangeAuthorizationCodeAsync(
                    new SqlOSTokenRequest(
                        grantType,
                        form["code"].ToString(),
                        form["redirect_uri"].ToString(),
                        clientId,
                        form["code_verifier"].ToString(),
                        form["refresh_token"].ToString(),
                        form["resource"].ToString()),
                    context,
                    cancellationToken);

                // RFC 6749 §5.1: a null scope means "unknown" (legacy sessions on the
                // refresh grant), so the field is omitted instead of claiming an empty
                // grant. An empty string is a real deny-all grant and is still echoed.
                // id_token appears only when OpenID Provider mode minted one for a
                // granted openid scope — strict RP libraries reject "id_token": null.
                var response = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["access_token"] = result.Tokens.AccessToken,
                    ["refresh_token"] = result.Tokens.RefreshToken,
                    ["token_type"] = "Bearer",
                    ["expires_in"] = Math.Max(1, (int)(result.Tokens.AccessTokenExpiresAt - DateTime.UtcNow).TotalSeconds)
                };
                if (result.Scope != null)
                {
                    response["scope"] = result.Scope;
                }

                if (!string.IsNullOrEmpty(result.Tokens.IdToken))
                {
                    response["id_token"] = result.Tokens.IdToken;
                }

                return Results.Ok(response);
            }
            catch (SqlOSDeviceAuthorizationException ex)
            {
                return Results.BadRequest(BuildDeviceAuthorizationError(ex));
            }
            catch (SqlOSClientAuthenticationException ex)
            {
                context.Response.Headers.WWWAuthenticate = "Basic";
                return Results.Json(
                    new { error = ex.Error, error_description = ex.Message },
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            catch (InvalidOperationException ex)
            {
                return await PublicOAuthTokenErrorAsync(context, ex, cancellationToken);
            }
        });
    }
}
