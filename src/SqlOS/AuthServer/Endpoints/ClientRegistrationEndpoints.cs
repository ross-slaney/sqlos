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
    private static void MapClientRegistrationEndpoints(RouteGroupBuilder auth, SqlOSAuthServerOptions authOptions)
    {
        if (authOptions.ClientRegistration.Dcr.Enabled)
        {
            auth.MapPost("/register", async (
                SqlOSDynamicClientRegistrationRequest request,
                SqlOSDynamicClientRegistrationService registrationService,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await registrationService.RegisterAsync(request, context, cancellationToken);
                    return Results.Json(result, statusCode: StatusCodes.Status201Created);
                }
                catch (SqlOSClientRegistrationException ex)
                {
                    var error = await MapPublicAuthErrorAsync(
                        context,
                        ex,
                        SqlOSPublicAuthErrorSurface.DynamicClientRegistration,
                        cancellationToken);
                    return Results.Json(new
                    {
                        error = error.Error,
                        error_description = error.PublicMessage
                    }, statusCode: error.StatusCode);
                }
            });
        }
    }
}
