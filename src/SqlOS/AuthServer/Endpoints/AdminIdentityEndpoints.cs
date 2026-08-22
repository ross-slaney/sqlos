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
using SqlOS.Pagination;

namespace SqlOS.AuthServer.Extensions;

public static partial class EndpointRouteBuilderExtensions
{
    private static void MapAdminIdentityEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/users", async (HttpContext context, string? search, string? cursor, int? pageSize, int? page, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => adminService.ListUsersAsync(search, cursor, pageSize, page, cancellationToken));
        });

        api.MapGet("/users/{userId}", async (HttpContext context, string userId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.GetUserAsync(userId, cancellationToken));
        });

        api.MapGet("/users/{userId}/memberships", async (HttpContext context, string userId, string? cursor, int? pageSize, int? page, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => adminService.ListUserMembershipsAsync(userId, cursor, pageSize, page, cancellationToken));
        });

        api.MapGet("/users/{userId}/sessions", async (HttpContext context, string userId, string? cursor, int? pageSize, int? page, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => adminService.ListUserSessionsAsync(userId, cursor, pageSize, page, cancellationToken));
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

        api.MapGet("/users/{userId}/grants", async (HttpContext context, string userId, SqlOSConsentService consentService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var grants = await consentService.ListActiveGrantsForUserAsync(userId, cancellationToken);
            return Results.Ok(new { data = grants });
        });

        api.MapPost("/users/{userId}/grants/{grantId}/revoke", async (HttpContext context, string userId, string grantId, SqlOSConsentService consentService, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var grant = await consentService.RevokeGrantAsync(userId, grantId, "admin_revoked", cancellationToken);
                await adminService.RecordAuditAsync(
                    "oauth.consent.revoked",
                    "admin",
                    "dashboard",
                    userId: userId,
                    data: new
                    {
                        grant_id = grant.Id,
                        client_id = grant.ClientApplication?.ClientId ?? grant.ClientApplicationId,
                        reason = grant.RevocationReason
                    },
                    cancellationToken: cancellationToken);
                return Results.Ok(new { grant.Id, grant.RevokedAt });
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

        api.MapGet("/organizations", async (HttpContext context, string? search, string? cursor, int? pageSize, int? page, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => adminService.ListOrganizationsAsync(search, cursor, pageSize, page, cancellationToken));
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

        api.MapGet("/memberships", async (HttpContext context, string? search, string? cursor, int? pageSize, int? page, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => adminService.ListMembershipsAsync(search, cursor, pageSize, page, cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}/memberships", async (HttpContext context, string organizationId, string? search, string? cursor, int? pageSize, int? page, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => adminService.ListOrganizationMembershipsAsync(organizationId, search, cursor, pageSize, page, cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}/invitations", async (HttpContext context, string organizationId, string? cursor, int? pageSize, int? page, SqlOSInvitationService invitationService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => invitationService.ListOrganizationInvitationsAsync(organizationId, cursor, pageSize, page, cancellationToken));
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

        api.MapGet("/clients", async (HttpContext context, string? source, string? status, string? search, string? cursor, int? pageSize, int? page, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => adminService.ListClientsAsync(source, status, search, cursor, pageSize, page, cancellationToken));
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

        api.MapGet("/clients/{clientId}/credentials", async (HttpContext context, string clientId, string? cursor, int? pageSize, int? page, SqlOSClientAuthenticationService clientAuthentication, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => clientAuthentication.ListCredentialsAsync(clientId, cursor, pageSize, page, cancellationToken));
        });

        api.MapPost("/clients/{clientId}/credentials", async (HttpContext context, string clientId, SqlOSCreateClientCredentialRequest request, SqlOSClientAuthenticationService clientAuthentication, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await clientAuthentication.CreateCredentialAsync(
                    clientId,
                    request.DisplayName,
                    request.ExpiresAt,
                    cancellationToken: cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapDelete("/clients/{clientId}/credentials/{credentialId}", async (HttpContext context, string clientId, string credentialId, SqlOSClientAuthenticationService clientAuthentication, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                await clientAuthentication.RevokeCredentialAsync(clientId, credentialId, cancellationToken: cancellationToken);
                return Results.Ok(new { revoked = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapGet("/applications/{applicationId}/assignments", async (HttpContext context, string applicationId, bool? includeRevoked, string? cursor, int? pageSize, int? page, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return await SqlOSCursorPagination.Ok(() => adminService.ListApplicationAssignmentsAsync(applicationId, includeRevoked == true, cursor, pageSize, page, cancellationToken));
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
                    client.ClientType,
                    client.TokenEndpointAuthMethod,
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

        api.MapPost("/clients/{clientId}/emergency-disable", async (HttpContext context, string clientId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.EmergencyDisableClientAsync(clientId, cancellationToken);
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

        api.MapPost("/clients/{clientId}/emergency-enable", async (HttpContext context, string clientId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.EmergencyEnableClientAsync(clientId, cancellationToken);
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

        api.MapGet("/machine-clients", async (HttpContext context, string? cursor, int? pageSize, int? page, SqlOSMachineClientAdminService machines, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment)) return Results.NotFound();
            return await SqlOSCursorPagination.Ok(() => machines.ListAsync(cursor, pageSize, page, cancellationToken));
        });

        api.MapPost("/machine-clients", async (HttpContext context, SqlOSCreateMachineClientRequest request, SqlOSMachineClientAdminService machines, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment)) return Results.NotFound();
            try { return Results.Ok(await machines.CreateAsync(request, cancellationToken)); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
        });

        api.MapPost("/machine-clients/{clientId}/rotate", async (HttpContext context, string clientId, SqlOSMachineClientAdminService machines, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment)) return Results.NotFound();
            try { return Results.Ok(await machines.RotateAsync(clientId, cancellationToken)); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
        });

        api.MapPost("/machine-clients/{clientId}/validate", async (HttpContext context, string clientId, MachineClientValidationRequest request, SqlOSMachineClientAdminService machines, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment)) return Results.NotFound();
            try { return Results.Ok(await machines.ValidateCredentialAsync(clientId, request.ClientSecret, request.Resource, request.Scopes, cancellationToken)); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
        });

        api.MapPost("/machine-clients/{clientId}/revoke", async (HttpContext context, string clientId, SqlOSMachineClientAdminService machines, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment)) return Results.NotFound();
            try { await machines.RevokeAsync(clientId, cancellationToken); return Results.Ok(new { revoked = true }); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
        });

        api.MapPost("/machine-clients/{clientId}/emergency-disable", async (HttpContext context, string clientId, SqlOSMachineClientAdminService machines, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment)) return Results.NotFound();
            try { return Results.Ok(await machines.EmergencyDisableAsync(clientId, cancellationToken)); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
        });

        api.MapPost("/machine-clients/{clientId}/emergency-enable", async (HttpContext context, string clientId, SqlOSMachineClientAdminService machines, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment)) return Results.NotFound();
            try { return Results.Ok(await machines.EmergencyEnableAsync(clientId, cancellationToken)); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
        });

        api.MapPost("/machine-clients/{clientId}/grants", async (HttpContext context, string clientId, SqlOSMachineClientGrantRequest request, SqlOSMachineClientAdminService machines, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment)) return Results.NotFound();
            try { await machines.AddGrantAsync(clientId, request, cancellationToken); return Results.Ok(new { added = true }); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
        });

        api.MapDelete("/machine-clients/{clientId}/grants/{grantId}", async (HttpContext context, string clientId, string grantId, SqlOSMachineClientAdminService machines, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment)) return Results.NotFound();
            try { await machines.RemoveGrantAsync(clientId, grantId, cancellationToken); return Results.Ok(new { removed = true }); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
        });
    }

    private sealed record MachineClientValidationRequest(string ClientSecret, string Resource, IReadOnlyList<string> Scopes);
}
