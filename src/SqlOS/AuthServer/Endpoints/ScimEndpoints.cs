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
    private static void MapScimEndpoints(RouteGroupBuilder scim)
    {
        scim.MapGet("/ServiceProviderConfig", async (
            HttpContext context,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, connection =>
                Task.FromResult<IResult>(ScimJson(scimService.GetServiceProviderConfig())), cancellationToken));

        scim.MapGet("/ResourceTypes", async (
            HttpContext context,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, connection =>
                Task.FromResult<IResult>(ScimJson(scimService.GetResourceTypes())), cancellationToken));

        scim.MapGet("/ResourceTypes/{id}", async (
            HttpContext context,
            string id,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, connection =>
                Task.FromResult<IResult>(ScimResourceJson(context, scimService.GetResourceType(id))), cancellationToken));

        scim.MapGet("/Schemas", async (
            HttpContext context,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, connection =>
                Task.FromResult<IResult>(ScimJson(scimService.GetSchemas())), cancellationToken));

        scim.MapGet("/Schemas/{id}", async (
            HttpContext context,
            string id,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, connection =>
                Task.FromResult<IResult>(ScimResourceJson(context, scimService.GetSchema(id))), cancellationToken));

        scim.MapGet("/Users", async (
            HttpContext context,
            int? startIndex,
            int? count,
            string? filter,
            string? attributes,
            string? excludedAttributes,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
                ScimJson(await scimService.ListUsersAsync(connection, startIndex, count, filter, attributes, excludedAttributes, cancellationToken)), cancellationToken));

        scim.MapPost("/Users", async (
            HttpContext context,
            string? attributes,
            string? excludedAttributes,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
            {
                var payload = await ReadScimPayloadAsync(context, cancellationToken);
                var resource = await scimService.CreateUserAsync(connection, payload, attributes, excludedAttributes, cancellationToken);
                return ScimCreated(context, resource, scimService.GetResourceLocation("Users", resource));
            }, cancellationToken));

        scim.MapGet("/Users/{id}", async (
            HttpContext context,
            string id,
            string? attributes,
            string? excludedAttributes,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
                ScimResourceJson(context, await scimService.GetUserAsync(connection, id, attributes, excludedAttributes, cancellationToken)), cancellationToken));

        scim.MapPut("/Users/{id}", async (
            HttpContext context,
            string id,
            string? attributes,
            string? excludedAttributes,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
            {
                var payload = await ReadScimPayloadAsync(context, cancellationToken);
                var resource = await scimService.ReplaceUserAsync(connection, id, payload, attributes, excludedAttributes, cancellationToken);
                return ScimResourceJson(context, resource, location: scimService.GetResourceLocation("Users", resource));
            }, cancellationToken));

        scim.MapPatch("/Users/{id}", async (
            HttpContext context,
            string id,
            string? attributes,
            string? excludedAttributes,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
            {
                var payload = await ReadScimPayloadAsync(context, cancellationToken);
                var resource = await scimService.PatchUserAsync(connection, id, payload, attributes, excludedAttributes, cancellationToken);
                return ScimResourceJson(context, resource, location: scimService.GetResourceLocation("Users", resource));
            }, cancellationToken));

        scim.MapDelete("/Users/{id}", async (
            HttpContext context,
            string id,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
            {
                await scimService.DeleteUserAsync(connection, id, cancellationToken);
                return Results.NoContent();
            }, cancellationToken));

        scim.MapGet("/Groups", async (
            HttpContext context,
            int? startIndex,
            int? count,
            string? filter,
            string? attributes,
            string? excludedAttributes,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
                ScimJson(await scimService.ListGroupsAsync(connection, startIndex, count, filter, attributes, excludedAttributes, cancellationToken)), cancellationToken));

        scim.MapPost("/Groups", async (
            HttpContext context,
            string? attributes,
            string? excludedAttributes,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
            {
                var payload = await ReadScimPayloadAsync(context, cancellationToken);
                var resource = await scimService.CreateGroupAsync(connection, payload, attributes, excludedAttributes, cancellationToken);
                return ScimCreated(context, resource, scimService.GetResourceLocation("Groups", resource));
            }, cancellationToken));

        scim.MapGet("/Groups/{id}", async (
            HttpContext context,
            string id,
            string? attributes,
            string? excludedAttributes,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
                ScimResourceJson(context, await scimService.GetGroupAsync(connection, id, attributes, excludedAttributes, cancellationToken)), cancellationToken));

        scim.MapPut("/Groups/{id}", async (
            HttpContext context,
            string id,
            string? attributes,
            string? excludedAttributes,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
            {
                var payload = await ReadScimPayloadAsync(context, cancellationToken);
                var resource = await scimService.ReplaceGroupAsync(connection, id, payload, attributes, excludedAttributes, cancellationToken);
                return ScimResourceJson(context, resource, location: scimService.GetResourceLocation("Groups", resource));
            }, cancellationToken));

        scim.MapPatch("/Groups/{id}", async (
            HttpContext context,
            string id,
            string? attributes,
            string? excludedAttributes,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
            {
                var payload = await ReadScimPayloadAsync(context, cancellationToken);
                var resource = await scimService.PatchGroupAsync(connection, id, payload, attributes, excludedAttributes, cancellationToken);
                return string.IsNullOrWhiteSpace(attributes) && string.IsNullOrWhiteSpace(excludedAttributes)
                    ? Results.NoContent()
                    : ScimResourceJson(context, resource, location: scimService.GetResourceLocation("Groups", resource));
            }, cancellationToken));

        scim.MapDelete("/Groups/{id}", async (
            HttpContext context,
            string id,
            SqlOSScimService scimService,
            CancellationToken cancellationToken) =>
            await HandleScimAsync(context, scimService, async connection =>
            {
                await scimService.DeleteGroupAsync(connection, id, cancellationToken);
                return Results.NoContent();
            }, cancellationToken));
    }

    private static void MapScimAdminEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/organizations/{organizationId}/scim-connections", async (
            HttpContext context,
            string organizationId,
            int? page,
            int? pageSize,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
                Results.Ok(await adminService.ListOrganizationScimConnectionsAsync(organizationId, page, pageSize, cancellationToken))));

        api.MapPost("/organizations/{organizationId}/scim-connections", async (
            HttpContext context,
            string organizationId,
            CreateScimConnectionDashboardRequest request,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
            {
                var connection = await adminService.CreateScimConnectionAsync(
                    new SqlOSCreateScimConnectionRequest(organizationId, request.DisplayName, request.Enabled),
                    cancellationToken);
                return SensitiveJson(context, connection);
            }));

        api.MapGet("/scim-connections/{connectionId}", async (
            HttpContext context,
            string connectionId,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
                Results.Ok(await adminService.GetScimConnectionAsync(connectionId, cancellationToken))));

        api.MapPut("/scim-connections/{connectionId}", async (
            HttpContext context,
            string connectionId,
            UpdateScimConnectionDashboardRequest request,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
            {
                var connection = await adminService.UpdateScimConnectionAsync(
                    connectionId,
                    new SqlOSUpdateScimConnectionRequest(request.DisplayName, request.Enabled),
                    cancellationToken);
                return Results.Ok(ToScimConnectionAdminResponse(connection));
            }));

        api.MapPost("/scim-connections/{connectionId}/enable", async (
            HttpContext context,
            string connectionId,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
                Results.Ok(ToScimConnectionAdminResponse(await adminService.SetScimConnectionEnabledAsync(connectionId, true, cancellationToken)))));

        api.MapPost("/scim-connections/{connectionId}/disable", async (
            HttpContext context,
            string connectionId,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
                Results.Ok(ToScimConnectionAdminResponse(await adminService.SetScimConnectionEnabledAsync(connectionId, false, cancellationToken)))));

        api.MapPost("/scim-connections/{connectionId}/token/rotate", async (
            HttpContext context,
            string connectionId,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
                SensitiveJson(context, await adminService.RotateScimTokenAsync(connectionId, cancellationToken))));

        api.MapGet("/scim-connections/{connectionId}/mappings", async (
            HttpContext context,
            string connectionId,
            int? page,
            int? pageSize,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
                Results.Ok(await adminService.ListScimGroupMappingsAsync(connectionId, page, pageSize, cancellationToken))));

        api.MapPost("/scim-connections/{connectionId}/mappings", async (
            HttpContext context,
            string connectionId,
            SqlOSCreateScimGroupMappingRequest request,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
                Results.Ok(ToScimMappingAdminResponse(await adminService.CreateScimGroupMappingAsync(connectionId, request, cancellationToken)))));

        api.MapPut("/scim-mappings/{mappingId}", async (
            HttpContext context,
            string mappingId,
            SqlOSUpdateScimGroupMappingRequest request,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
                Results.Ok(ToScimMappingAdminResponse(await adminService.UpdateScimGroupMappingAsync(mappingId, request, cancellationToken)))));

        api.MapPost("/scim-mappings/{mappingId}/enable", async (
            HttpContext context,
            string mappingId,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
                Results.Ok(ToScimMappingAdminResponse(await adminService.SetScimGroupMappingEnabledAsync(mappingId, true, cancellationToken)))));

        api.MapPost("/scim-mappings/{mappingId}/disable", async (
            HttpContext context,
            string mappingId,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
                Results.Ok(ToScimMappingAdminResponse(await adminService.SetScimGroupMappingEnabledAsync(mappingId, false, cancellationToken)))));

        api.MapGet("/scim-connections/{connectionId}/sync-events", async (
            HttpContext context,
            string connectionId,
            int? page,
            int? pageSize,
            SqlOSAdminService adminService,
            IOptions<SqlOSAuthServerOptions> options,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
            await HandleAdminApiAsync(context, options, environment, async () =>
                Results.Ok(await adminService.ListScimSyncEventsAsync(connectionId, page, pageSize, cancellationToken))));
    }
}
