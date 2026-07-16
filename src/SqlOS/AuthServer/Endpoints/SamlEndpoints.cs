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
    private static void MapSamlEndpoints(RouteGroupBuilder auth)
    {
        auth.MapPost("/sso/authorization-url", async (SqlOSAuthorizationUrlRequest request, SqlOSSamlService samlService, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(new
                {
                    authorizationUrl = await samlService.CreateAuthorizationUrlAsync(request, cancellationToken)
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_request",
                    error_description = ex.Message
                });
            }
        });

        static async Task<IResult> HandleSamlAcsAsync(
            string connectionId,
            HttpContext httpContext,
            SqlOSSamlService samlService,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken)
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var samlResponse = form["SAMLResponse"].ToString();
            var relayState = form["RelayState"].ToString();
            if (string.IsNullOrWhiteSpace(samlResponse) || string.IsNullOrWhiteSpace(relayState))
            {
                return Results.BadRequest(new { error = "SAMLResponse and RelayState are required." });
            }

            try
            {
                var redirectUrl = await samlService.HandleAcsAsync(connectionId, samlResponse, relayState, httpContext, cancellationToken);
                return Results.Redirect(redirectUrl);
            }
            catch (InvalidOperationException ex)
            {
                var error = await MapPublicAuthErrorAsync(
                    httpContext,
                    ex,
                    SqlOSPublicAuthErrorSurface.SamlAcs,
                    cancellationToken);
                var headlessErrorRedirect = await headlessAuthService.TryBuildUiUrlForAuthorizationRequestAsync(
                    httpContext,
                    relayState,
                    "login",
                    error.PublicMessage,
                    pendingToken: null,
                    email: null,
                    displayName: null,
                    cancellationToken);
                if (headlessErrorRedirect != null)
                {
                    return Results.Redirect(headlessErrorRedirect);
                }

                return Results.Json(new
                {
                    error = error.Error,
                    message = error.PublicMessage
                }, statusCode: error.StatusCode);
            }
        }

        auth.MapPost("/saml/acs/{connectionId}", HandleSamlAcsAsync);
    }
}
