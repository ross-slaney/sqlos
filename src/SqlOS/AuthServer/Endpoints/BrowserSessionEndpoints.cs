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
    private static void MapBrowserSessionEndpoints(RouteGroupBuilder auth, string authPrefix)
    {
        auth.MapGet("/logged-out", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            await authPageSessionService.SignOutAsync(context, cancellationToken);
            var page = await BuildAuthPageViewModelAsync(
                "logged-out",
                null,
                null,
                null,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken);
            return Html(page);
        });

        auth.MapGet("/logout", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            await authPageSessionService.SignOutAsync(context, cancellationToken);

            var requestedReturnUrl = context.Request.Query["returnTo"].ToString();
            if (string.IsNullOrWhiteSpace(requestedReturnUrl))
            {
                requestedReturnUrl = context.Request.Query["post_logout_redirect_uri"].ToString();
            }

            var redirectTarget = await authorizationServerService.ResolvePostLogoutRedirectAsync(
                context,
                requestedReturnUrl,
                cancellationToken);

            return redirectTarget == null
                ? Results.Redirect($"{authPrefix}/logged-out")
                : Results.Redirect(redirectTarget);
        });
    }
}
