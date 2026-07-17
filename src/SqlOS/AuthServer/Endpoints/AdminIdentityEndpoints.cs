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
    private static void MapAdminIdentityEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/users", async (HttpContext context, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListUsersAsync(page, pageSize, cancellationToken));
        });

        api.MapGet("/users/{userId}", async (HttpContext context, string userId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.GetUserAsync(userId, cancellationToken));
        });

        api.MapGet("/users/{userId}/memberships", async (HttpContext context, string userId, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListUserMembershipsAsync(userId, page, pageSize, cancellationToken));
        });

        api.MapGet("/users/{userId}/sessions", async (HttpContext context, string userId, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListUserSessionsAsync(userId, page, pageSize, cancellationToken));
        });

        api.MapGet("/users/{userId}/applications", async (HttpContext context, string userId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await adminService.ListApplicationsForUserAsync(userId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/users/{userId}/password-reset-email", async (HttpContext context, string userId, SqlOSSendUserPasswordResetEmailRequest request, SqlOSAuthService authService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await authService.SendPasswordResetEmailForUserAsync(userId, request, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/users", async (HttpContext context, SqlOSCreateUserRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var user = await adminService.CreateUserAsync(request, cancellationToken);
            return Results.Ok(new
            {
                user.Id,
                user.DisplayName,
                user.DefaultEmail,
                user.IsActive,
                user.CreatedAt,
                user.UpdatedAt
            });
        });

        api.MapGet("/organizations", async (HttpContext context, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListOrganizationsAsync(page, pageSize, cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}", async (HttpContext context, string organizationId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.GetOrganizationAsync(organizationId, cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}/applications", async (HttpContext context, string organizationId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await adminService.ListApplicationsForOrganizationAsync(organizationId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/organizations", async (HttpContext context, SqlOSCreateOrganizationRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var organization = await adminService.CreateOrganizationAsync(request, cancellationToken);
            return Results.Ok(new
            {
                organization.Id,
                organization.Name,
                organization.Slug,
                organization.PrimaryDomain,
                organization.IsActive,
                organization.CreatedAt
            });
        });

        api.MapPut("/organizations/{organizationId}", async (HttpContext context, string organizationId, SqlOSUpdateOrganizationRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var organization = await adminService.UpdateOrganizationAsync(organizationId, request, cancellationToken);
            return Results.Ok(new
            {
                organization.Id,
                organization.Name,
                organization.Slug,
                organization.PrimaryDomain,
                organization.IsActive,
                organization.CreatedAt
            });
        });

        api.MapGet("/memberships", async (HttpContext context, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListMembershipsAsync(page, pageSize, cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}/memberships", async (HttpContext context, string organizationId, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListOrganizationMembershipsAsync(organizationId, page, pageSize, cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}/invitations", async (HttpContext context, string organizationId, int? page, int? pageSize, SqlOSInvitationService invitationService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await invitationService.ListOrganizationInvitationsAsync(organizationId, page, pageSize, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/organizations/{organizationId}/invitations", async (HttpContext context, string organizationId, CreateOrganizationInvitationRequest request, SqlOSInvitationService invitationService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await invitationService.CreateEmailInvitationAsync(
                    new SqlOSCreateEmailInvitationRequest(
                        organizationId,
                        request.Email,
                        string.IsNullOrWhiteSpace(request.Role) ? "member" : request.Role,
                        request.ClientId,
                        request.RedirectUri,
                        request.Scope,
                        request.Resource,
                        request.ExpiresAt,
                        request.CustomFields,
                        request.InvitedByUserId,
                        request.SendEmail ?? true),
                    context,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/invitations/{invitationId}/resend", async (HttpContext context, string invitationId, SqlOSInvitationService invitationService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await invitationService.ResendEmailInvitationAsync(
                    new SqlOSResendEmailInvitationRequest(invitationId),
                    context,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/invitations/{invitationId}/revoke", async (HttpContext context, string invitationId, RevokeInvitationRequest request, SqlOSInvitationService invitationService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await invitationService.RevokeEmailInvitationAsync(
                    new SqlOSRevokeEmailInvitationRequest(invitationId, request.Reason),
                    context,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/memberships", async (HttpContext context, CreateMembershipRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var membership = await adminService.CreateMembershipAsync(request.OrganizationId, new SqlOSCreateMembershipRequest(request.UserId, request.Role), cancellationToken);
            return Results.Ok(new
            {
                membership.OrganizationId,
                membership.UserId,
                membership.Role,
                membership.IsActive,
                membership.CreatedAt
            });
        });

        api.MapPost("/organizations/{organizationId}/memberships", async (HttpContext context, string organizationId, SqlOSCreateMembershipRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var membership = await adminService.CreateMembershipAsync(organizationId, request, cancellationToken);
            return Results.Ok(new
            {
                membership.OrganizationId,
                membership.UserId,
                membership.Role,
                membership.IsActive,
                membership.CreatedAt
            });
        });

        api.MapGet("/clients", async (HttpContext context, string? source, string? status, string? search, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListClientsAsync(source, status, search, page, pageSize, cancellationToken));
        });

        api.MapGet("/clients/{clientId}", async (HttpContext context, string clientId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await adminService.GetClientDetailAsync(clientId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapGet("/applications/{applicationId}/assignments", async (HttpContext context, string applicationId, bool? includeRevoked, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await adminService.ListApplicationAssignmentsAsync(applicationId, includeRevoked == true, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/applications/{applicationId}/access-mode", async (HttpContext context, string applicationId, SqlOSSetApplicationAccessModeRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.SetApplicationAccessModeAsync(applicationId, request, actorType: "dashboard", cancellationToken: cancellationToken);
                return Results.Ok(new { client.Id, client.ClientId, client.Name, client.AccessMode, client.IsActive, client.DisabledAt, client.DisabledReason });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/applications/{applicationId}/assignments", async (HttpContext context, string applicationId, SqlOSCreateApplicationAssignmentRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var assignment = await adminService.AssignApplicationAsync(applicationId, request, cancellationToken: cancellationToken);
                return Results.Ok(new
                {
                    assignment.Id,
                    assignment.ClientApplicationId,
                    assignment.OrganizationId,
                    assignment.PrincipalType,
                    assignment.PrincipalId,
                    assignment.RoleKey,
                    assignment.Access,
                    assignment.Reason,
                    Ownership = SqlOSConfigurationOwnershipPolicy.ToDto(
                        assignment.ConfigurationOwner,
                        assignment.ConfigurationSourceKey,
                        assignment.LastReconciledAt,
                        assignment.ConfigurationFingerprint,
                        assignment.ConfigurationOrphanedAt,
                        false),
                    assignment.CreatedAt,
                    assignment.RevokedAt
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapDelete("/applications/{applicationId}/assignments/{assignmentId}", async (HttpContext context, string applicationId, string assignmentId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var assignment = await adminService.RevokeApplicationAssignmentAsync(applicationId, assignmentId, null, cancellationToken);
                return Results.Ok(new
                {
                    assignment.Id,
                    assignment.RevokedAt,
                    assignment.RevokedByActorType,
                    assignment.RevokedByActorId,
                    Ownership = SqlOSConfigurationOwnershipPolicy.ToDto(
                        assignment.ConfigurationOwner,
                        assignment.ConfigurationSourceKey,
                        assignment.LastReconciledAt,
                        assignment.ConfigurationFingerprint,
                        assignment.ConfigurationOrphanedAt,
                        false)
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapGet("/applications/{applicationId}/access/check", async (HttpContext context, string applicationId, string? organizationId, string? userId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await adminService.CheckApplicationAccessAsync(applicationId, userId, organizationId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients", async (HttpContext context, SqlOSCreateClientRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.CreateClientAsync(request, cancellationToken);
                return Results.Ok(new
                {
                    client.Id,
                    client.ClientId,
                    client.Name,
                    client.Audience,
                    client.AccessMode,
                    client.AllowNativeHeadlessAuth,
                    client.AllowDeviceAuthorization,
                    RedirectUris = SqlOSAdminService.DeserializeJsonList(client.RedirectUrisJson),
                    client.IsActive,
                    client.CreatedAt
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients/{clientId}/disable", async (HttpContext context, string clientId, ClientLifecycleRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.DisableClientAsync(clientId, request.Reason, cancellationToken);
                return Results.Ok(new
                {
                    client.Id,
                    client.ClientId,
                    client.IsActive,
                    client.DisabledAt,
                    client.DisabledReason
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients/{clientId}/enable", async (HttpContext context, string clientId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.EnableClientAsync(clientId, cancellationToken);
                return Results.Ok(new
                {
                    client.Id,
                    client.ClientId,
                    client.IsActive,
                    client.DisabledAt,
                    client.DisabledReason
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients/{clientId}/revoke", async (HttpContext context, string clientId, ClientLifecycleRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var revokedSessions = await adminService.RevokeClientSessionsAsync(clientId, string.IsNullOrWhiteSpace(request.Reason) ? "client_revoked" : request.Reason.Trim(), cancellationToken);
                return Results.Ok(new { revokedSessions });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });
    }
}
