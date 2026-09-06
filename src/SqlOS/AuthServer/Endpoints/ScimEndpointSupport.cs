using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlOS.Database;
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
using SqlOS.Pagination;

namespace SqlOS.AuthServer.Extensions;

public static partial class EndpointRouteBuilderExtensions
{
    private static async Task<IResult> HandleScimAsync(
        HttpContext context,
        SqlOSScimService scimService,
        Func<SqlOSScimConnection, Task<IResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = await scimService.AuthenticateAsync(context, cancellationToken);
            return await action(connection);
        }
        catch (SqlOSScimException ex)
        {
            if (ex.StatusCode == StatusCodes.Status401Unauthorized)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer realm=\"SqlOS SCIM\"";
            }
            return ScimError(ex.StatusCode, ex.Message, ex.ScimType);
        }
        catch (Exception ex) when (IsSqlServerDeadlock(ex))
        {
            context.Response.Headers.RetryAfter = "1";
            return ScimError(StatusCodes.Status503ServiceUnavailable, "The SCIM request encountered a transient concurrency conflict. Retry the request.");
        }
        catch (DbUpdateException ex) when (IsSqlServerUniqueConstraintViolation(ex))
        {
            return ScimError(StatusCodes.Status409Conflict, "The SCIM resource conflicts with an existing resource.", "uniqueness");
        }
        catch (DbUpdateException)
        {
            context.Response.Headers.RetryAfter = "1";
            return ScimError(StatusCodes.Status503ServiceUnavailable, "The SCIM request could not be persisted. Retry the request.");
        }
        catch (JsonException ex)
        {
            return ScimError(StatusCodes.Status400BadRequest, ex.Message, "invalidSyntax");
        }
        catch (InvalidOperationException ex)
        {
            return ScimError(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static bool IsSqlServerDeadlock(Exception exception)
        => SqlOSDatabaseErrors.IsDeadlock(exception);

    private static bool IsSqlServerUniqueConstraintViolation(Exception exception)
        => SqlOSDatabaseErrors.IsUniqueConstraintViolation(exception);

    private static async Task<IResult> HandleAdminApiAsync(
        HttpContext context,
        IOptions<SqlOSAuthServerOptions> options,
        IHostEnvironment environment,
        Func<Task<IResult>> action)
    {
        if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
        {
            return Results.NotFound();
        }

        try
        {
            return await action();
        }
        catch (SqlOSCursorException ex)
        {
            return SqlOSCursorPagination.BadRequest(ex);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static IResult ScimJson(JsonObject payload, int statusCode = StatusCodes.Status200OK)
        => Results.Json(payload, statusCode: statusCode, contentType: "application/scim+json");

    private static IResult ScimResourceJson(
        HttpContext context,
        JsonObject payload,
        int statusCode = StatusCodes.Status200OK,
        string? location = null)
    {
        location ??= payload["meta"]?["location"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(location))
        {
            context.Response.Headers.ContentLocation = location;
        }
        return ScimJson(payload, statusCode);
    }

    private static IResult ScimCreated(HttpContext context, JsonObject payload, string? location = null)
    {
        location ??= payload["meta"]?["location"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(location))
        {
            context.Response.Headers.Location = location;
        }
        return ScimResourceJson(context, payload, StatusCodes.Status201Created, location);
    }

    private static IResult ScimError(int statusCode, string message, string? scimType = null)
        => Results.Json(
            SqlOSScimService.CreateError(statusCode, message, scimType),
            statusCode: statusCode,
            contentType: "application/scim+json");

    private static IResult SensitiveJson(HttpContext context, object payload)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Ok(payload);
    }

    private static async Task<JsonObject> ReadScimPayloadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            throw new SqlOSScimException(StatusCodes.Status415UnsupportedMediaType, "SCIM requests require application/scim+json or application/json.");
        }

        if (context.Request.ContentLength is > MaxScimPayloadBytes)
        {
            throw new SqlOSScimException(StatusCodes.Status413PayloadTooLarge, "SCIM JSON body exceeds the allowed size.", "tooMany");
        }

        await using var buffer = new MemoryStream(Math.Min(MaxScimPayloadBytes, 81920));
        var chunk = new byte[81920];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxScimPayloadBytes)
            {
                throw new SqlOSScimException(StatusCodes.Status413PayloadTooLarge, "SCIM JSON body exceeds the allowed size.", "tooMany");
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM JSON body is required.", "invalidSyntax");
        }

        buffer.Position = 0;
        return await JsonNode.ParseAsync(buffer, cancellationToken: cancellationToken) as JsonObject
            ?? throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM JSON body must be a JSON object.", "invalidSyntax");
    }

    private static string NormalizeScimBasePath(string? basePath)
    {
        var path = string.IsNullOrWhiteSpace(basePath) ? "/sqlos/scim/v2" : basePath.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return path.TrimEnd('/');
    }

    private static object ToScimConnectionAdminResponse(SqlOSScimConnection connection) => new
    {
        connection.Id,
        connection.OrganizationId,
        connection.DisplayName,
        connection.IsEnabled,
        connection.Source,
        connection.SeedKey,
        Ownership = SqlOSConfigurationOwnershipPolicy.ToDto(connection.ConfigurationOwner, connection.ConfigurationSourceKey, connection.LastReconciledAt, connection.ConfigurationFingerprint, connection.ConfigurationOrphanedAt),
        connection.TokenPrefix,
        connection.TokenRotatedAt,
        connection.TokenLastUsedAt,
        connection.LastSyncAt,
        connection.CreatedAt,
        connection.UpdatedAt
    };

    private static object ToScimMappingAdminResponse(SqlOSScimGroupMapping mapping) => new
    {
        mapping.Id,
        mapping.ConnectionId,
        mapping.Source,
        mapping.SourceKey,
        mapping.MatchType,
        mapping.GroupDisplayName,
        mapping.GroupExternalId,
        mapping.GroupPattern,
        mapping.RoleKey,
        mapping.ResourceId,
        mapping.ResourceIdTemplate,
        mapping.Description,
        mapping.IsEnabled,
        mapping.CreatedAt,
        mapping.UpdatedAt
    };

    private static async Task<bool> IsAdminAuthorizedAsync(HttpContext context, SqlOSAuthServerOptions options, IHostEnvironment environment)
    {
        if (options.Dashboard.AuthMode == SqlOSDashboardAuthMode.Password)
        {
            var sessionService = context.RequestServices.GetService<SqlOSDashboardSessionService>();
            if (sessionService == null || !sessionService.HasActiveSession(context))
            {
                return false;
            }

            if (options.Dashboard.AuthorizationCallback != null)
            {
                return await options.Dashboard.AuthorizationCallback(context);
            }

            return true;
        }

        if (options.Dashboard.AuthorizationCallback != null)
        {
            return await options.Dashboard.AuthorizationCallback(context);
        }

        return environment.IsDevelopment();
    }
}
