using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Calendar.Contracts;
using SqlOS.Calendar.Services;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Extensions;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.BehaviorLock.Host.Probes;

/// <summary>
/// The library-API probe surface, mapped only by the behavior-lock host under <c>/__probe</c>.
/// Each probe calls one documented public .NET API that hosts use directly (FGA checks, filters,
/// resource creation and entity sync, token validation, the documented <c>SqlOSAuthService</c> and
/// <c>SqlOSAdminService</c> members, and the audit, email, and calendar services) and returns its
/// result as JSON, so probe results are approved exactly like HTTP transcripts.
/// <para>
/// Probes compile against both the released package and source. That makes this file, together
/// with <see cref="SourceCompatibilityCanary"/>, the source-compatibility canary for later
/// refactor layers: an intentional signature change needs a behavior-ledger entry and, if the
/// released API must keep compiling, an <c>#if SQLOS_UNDER_TEST_PACKAGE</c> branch here.
/// </para>
/// </summary>
public static class ProbeEndpoints
{
    public const string Prefix = "/__probe";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var probes = app.MapGroup(Prefix);
        MapFga(probes.MapGroup("/fga"));
        MapAuth(probes.MapGroup("/auth"));
        MapAdmin(probes.MapGroup("/admin"));
        MapModules(probes);
    }

    private static void MapFga(RouteGroupBuilder fga)
    {
        fga.MapPost("/check", (HttpContext http, ISqlOSFgaAuthService authorization) =>
            Invoke(http, (FgaCheck request) => authorization.CheckAccessAsync(request.SubjectId, request.PermissionKey, request.ResourceId!)));

        fga.MapPost("/allows", (HttpContext http, ISqlOSFgaAuthService authorization) =>
            Invoke(http, async (FgaCheck request) => new { allowed = await authorization.Allows(request.SubjectId, request.PermissionKey, request.ResourceId!) }));

        fga.MapPost("/capability", (HttpContext http, ISqlOSFgaAuthService authorization) =>
            Invoke(http, async (FgaCheck request) => new { allowed = await authorization.HasCapabilityAsync(request.SubjectId, request.PermissionKey) }));

        fga.MapPost("/trace", (HttpContext http, ISqlOSFgaAuthService authorization) =>
            Invoke(http, (FgaCheck request) => authorization.TraceResourceAccessAsync(request.SubjectId, request.ResourceId!, request.PermissionKey)));

        // The documented list path: BuildFilterAsync<T> composed into an EF query over an
        // application entity. Returns the visible workspaces in name order.
        fga.MapPost("/filter", (HttpContext http, ISqlOSFgaAuthService authorization, ISqlOSFgaDbContext db) =>
            Invoke(http, async (FgaCheck request) =>
            {
                var filter = await authorization.BuildFilterAsync<Workspace>(request.SubjectId, request.PermissionKey);
                var visible = await Workspaces(db)
                    .Where(filter)
                    .OrderBy(workspace => workspace.Name)
                    .Select(workspace => new { workspace.Name, workspace.ResourceId })
                    .ToListAsync(http.RequestAborted);
                return new { count = visible.Count, workspaces = visible };
            }));

        // Manual resource lifecycle helpers from the FGA docs.
        fga.MapPost("/resources", (HttpContext http, ISqlOSFgaDbContext db) =>
            Invoke(http, async (FgaResourceRequest request) =>
            {
                SqlOSFgaResource resource;
                switch (request.Mode)
                {
                    case "create":
                        var id = db.CreateResource(request.ParentResourceId ?? "root", request.Name, request.ResourceTypeId, request.ResourceId);
                        await db.SaveChangesAsync(http.RequestAborted);
                        resource = await db.Set<SqlOSFgaResource>().AsNoTracking().SingleAsync(item => item.Id == id, http.RequestAborted);
                        break;
                    case "create-async":
                        resource = await db.CreateResourceAsync(request.ResourceTypeId, request.Name, request.ParentResourceId, request.Description, http.RequestAborted);
                        await db.SaveChangesAsync(http.RequestAborted);
                        break;
                    case "create-with-id":
                        resource = await db.CreateResourceWithIdAsync(request.ResourceId!, request.ResourceTypeId, request.Name, request.ParentResourceId, request.Description, http.RequestAborted);
                        await db.SaveChangesAsync(http.RequestAborted);
                        break;
                    case "provision-with-id":
                        resource = await db.ProvisionResourceWithIdAsync(request.ResourceId!, request.ResourceTypeId, request.Name, request.ParentResourceId, request.Description, cancellationToken: http.RequestAborted);
                        await db.SaveChangesAsync(http.RequestAborted);
                        break;
                    default:
                        throw new ArgumentException($"Unknown resource probe mode '{request.Mode}'.");
                }

                return DescribeResource(resource);
            }));

        fga.MapDelete("/resources/{resourceId}", (string resourceId, HttpContext http, ISqlOSFgaDbContext db) =>
            Execute(http, async () =>
            {
                await db.DeleteResourceAsync(resourceId, http.RequestAborted);
                await db.SaveChangesAsync(http.RequestAborted);
                return new { deleted = resourceId };
            }));

        // ISqlOSResourceEntity: saving the application entity synchronizes its FGA resource.
        fga.MapPost("/workspaces", (HttpContext http, ISqlOSFgaDbContext db) =>
            Invoke(http, async (WorkspaceRequest request) =>
            {
                var id = request.Id ?? Guid.NewGuid().ToString("N");
                var workspace = new Workspace
                {
                    Id = id,
                    ResourceId = request.ResourceId ?? $"{BehaviorLockAuthorization.WorkspaceType}::{id}",
                    Name = request.Name,
                    ParentResourceId = request.ParentResourceId,
                    IsActive = request.IsActive ?? true
                };
                ((DbContext)db).Add(workspace);
                await db.SaveChangesAsync(http.RequestAborted);
                return await DescribeWorkspaceAsync(db, workspace, http.RequestAborted);
            }));

        fga.MapPut("/workspaces/{id}", (string id, HttpContext http, ISqlOSFgaDbContext db) =>
            Invoke(http, async (WorkspaceRequest request) =>
            {
                var workspace = await Workspaces(db).SingleAsync(item => item.Id == id, http.RequestAborted);
                workspace.Name = request.Name;
                workspace.ParentResourceId = request.ParentResourceId ?? workspace.ParentResourceId;
                workspace.IsActive = request.IsActive ?? workspace.IsActive;
                await db.SaveChangesAsync(http.RequestAborted);
                return await DescribeWorkspaceAsync(db, workspace, http.RequestAborted);
            }));

        fga.MapPost("/subjects", (HttpContext http, ISqlOSFgaDbContext db) =>
            Invoke(http, async (SubjectRequest request) =>
            {
                object subject = request.Type switch
                {
                    "user" => await db.ProvisionUserSubjectAsync(request.SubjectId, request.DisplayName, request.Email, request.OrganizationId, cancellationToken: http.RequestAborted),
                    "agent" => await db.ProvisionAgentSubjectAsync(request.SubjectId, request.DisplayName, organizationId: request.OrganizationId, cancellationToken: http.RequestAborted),
                    "service_account" => await db.ProvisionServiceAccountSubjectAsync(request.SubjectId, request.DisplayName, request.ClientId ?? request.SubjectId, request.ClientSecretHash ?? string.Empty, organizationId: request.OrganizationId, cancellationToken: http.RequestAborted),
                    _ => throw new ArgumentException($"Unknown subject type '{request.Type}'.")
                };
                await db.SaveChangesAsync(http.RequestAborted);
                return new { type = request.Type, subjectId = request.SubjectId, record = subject.GetType().Name };
            }));

        fga.MapPost("/grants", (HttpContext http, ISqlOSFgaDbContext db) =>
            Invoke(http, async (GrantRequest request) =>
            {
                var grant = await db.GrantRoleAsync(request.SubjectId, request.ResourceId, request.Role, http.RequestAborted);
                await db.SaveChangesAsync(http.RequestAborted);
                return new { grant.Id, grant.SubjectId, grant.ResourceId, grant.RoleId };
            }));

        fga.MapPost("/grants/revoke", (HttpContext http, ISqlOSFgaDbContext db) =>
            Invoke(http, async (GrantRequest request) =>
            {
                await db.RevokeRoleAsync(request.SubjectId, request.ResourceId, request.Role, http.RequestAborted);
                await db.SaveChangesAsync(http.RequestAborted);
                return new { revoked = true };
            }));
    }

    private static void MapAuth(RouteGroupBuilder auth)
    {
        auth.MapPost("/validate", (HttpContext http, SqlOSAuthService service) =>
            Invoke(http, async (ValidateRequest request) =>
            {
                var token = await service.ValidateAccessTokenAsync(request.Token, request.Audience, http.RequestAborted);
                return token == null
                    ? (object)new { valid = false }
                    : new
                    {
                        valid = true,
                        token.UserId,
                        token.SessionId,
                        token.OrganizationId,
                        token.ClientId,
                        token.Audience,
                        token.Scope
                    };
            }));

        auth.MapPost("/password-login", (HttpContext http, SqlOSAuthService service) =>
            Invoke(http, (SqlOSPasswordLoginRequest request) => service.LoginWithPasswordAsync(request, http, http.RequestAborted)));

        auth.MapPost("/refresh", (HttpContext http, SqlOSAuthService service) =>
            Invoke(http, (SqlOSRefreshRequest request) => service.RefreshAsync(request, http.RequestAborted)));

        auth.MapPost("/logout", (HttpContext http, SqlOSAuthService service) =>
            Invoke(http, async (LogoutRequest request) =>
            {
                await service.LogoutAsync(request.RefreshToken, request.SessionId, http.RequestAborted);
                return new { loggedOut = true };
            }));

        auth.MapPost("/logout-all", (HttpContext http, SqlOSAuthService service) =>
            Invoke(http, async (UserRequest request) =>
            {
                await service.LogoutAllAsync(request.UserId, http.RequestAborted);
                return new { loggedOut = true };
            }));

        auth.MapPost("/email-otp", (HttpContext http, SqlOSAuthService service) =>
            Invoke(http, (SqlOSEmailOtpStartRequest request) => service.RequestEmailOtpAsync(request, http, http.RequestAborted)));

        auth.MapPost("/device-authorization", (HttpContext http, SqlOSAuthService service) =>
            Invoke(http, (SqlOSDeviceAuthorizationStartRequest request) => service.StartDeviceAuthorizationAsync(request, http, http.RequestAborted)));

        auth.MapPost("/invitations", (HttpContext http, SqlOSAuthService service) =>
            Invoke(http, (SqlOSCreateEmailInvitationRequest request) => service.CreateEmailInvitationAsync(request, http, http.RequestAborted)));

        auth.MapPost("/mfa-status", (HttpContext http, SqlOSAuthService service) =>
            Invoke(http, (UserRequest request) => service.GetMfaStatusAsync(request.UserId, request.OrganizationId, http.RequestAborted)));

        auth.MapGet("/providers", (HttpContext http, SqlOSOidcAuthService service) =>
            Execute(http, () => service.ListEnabledProvidersAsync(http.RequestAborted)));
    }

    private static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapPost("/users", (HttpContext http, SqlOSAdminService service) =>
            Invoke(http, async (SqlOSCreateUserRequest request) =>
            {
                var user = await service.CreateUserAsync(request, http.RequestAborted);
                return new { user.Id, user.DisplayName, user.DefaultEmail, user.IsActive, user.CreatedAt };
            }));

        admin.MapPost("/organizations", (HttpContext http, SqlOSAdminService service) =>
            Invoke(http, async (SqlOSCreateOrganizationRequest request) =>
            {
                var organization = await service.CreateOrganizationAsync(request, http.RequestAborted);
                return new { organization.Id, organization.Name, organization.Slug, organization.IsActive, organization.CreatedAt };
            }));

        admin.MapPost("/organizations/{organizationId}/memberships", (string organizationId, HttpContext http, SqlOSAdminService service) =>
            Invoke(http, async (SqlOSCreateMembershipRequest request) =>
            {
                var membership = await service.CreateMembershipAsync(organizationId, request, http.RequestAborted);
                return new { membership.OrganizationId, membership.UserId, membership.Role, membership.IsActive };
            }));

        admin.MapGet("/users/{userId}/organizations", (string userId, HttpContext http, SqlOSAdminService service) =>
            Execute(http, () => service.GetUserOrganizationsAsync(userId, http.RequestAborted)));

        admin.MapGet("/users/{userId}/memberships/{organizationId}", (string userId, string organizationId, HttpContext http, SqlOSAdminService service) =>
            Execute(http, async () => new { member = await service.UserHasMembershipAsync(userId, organizationId, http.RequestAborted) }));

        admin.MapPost("/applications/{clientId}/access-mode", (string clientId, HttpContext http, SqlOSAdminService service) =>
            Invoke(http, async (SqlOSSetApplicationAccessModeRequest request) =>
            {
                var client = await service.SetApplicationAccessModeAsync(clientId, request, "deployment", "behavior-lock", http.RequestAborted);
                return new { client.ClientId, client.AccessMode, client.IsActive };
            }));

        admin.MapPost("/applications/{clientId}/assignments", (string clientId, HttpContext http, SqlOSAdminService service) =>
            Invoke(http, async (SqlOSCreateApplicationAssignmentRequest request) =>
            {
                var assignment = await service.AssignApplicationAsync(clientId, request, "deployment", "behavior-lock", http.RequestAborted);
                return new
                {
                    assignment.Id,
                    assignment.PrincipalType,
                    assignment.PrincipalId,
                    assignment.OrganizationId,
                    assignment.RoleKey,
                    assignment.Access,
                    assignment.Reason
                };
            }));

        admin.MapGet("/applications/{clientId}/access-check", (string clientId, string? userId, string? organizationId, HttpContext http, SqlOSAdminService service) =>
            Execute(http, () => service.CheckApplicationAccessAsync(clientId, userId, organizationId, http.RequestAborted)));
    }

    private static void MapModules(RouteGroupBuilder probes)
    {
        probes.MapPost("/audit", (HttpContext http, ISqlOSAuditLogService audit) =>
            Invoke(http, async (SqlOSAuditLogRecordRequest request) =>
            {
                var result = await audit.RecordAsync(request, http.RequestAborted);
                return new { result.EventId, result.Created };
            }));

        probes.MapPost("/email/send", (HttpContext http, ISqlOSTransactionalEmailService email) =>
            Invoke(http, (SqlOSSendEmailRequest request) => email.SendAsync(request, http.RequestAborted)));

        probes.MapPost("/email/preview", (HttpContext http, ISqlOSTransactionalEmailService email) =>
            Invoke(http, (SqlOSSendEmailRequest request) => email.PreviewAsync(request.TemplateKey, request.Variables, http.RequestAborted)));

        probes.MapPost("/calendar/connect", (HttpContext http, SqlOSCalendarService calendar) =>
            Invoke(http, (SqlOSStartCalendarConnectRequest request) => calendar.StartConnectAsync(request, http, http.RequestAborted)));

        probes.MapGet("/calendar/connections", (string? userId, string? organizationId, HttpContext http, SqlOSCalendarService calendar) =>
            Execute(http, () => calendar.ListConnectionsAsync(userId, organizationId, includeRevoked: true, cancellationToken: http.RequestAborted)));
    }

    private static IQueryable<Workspace> Workspaces(ISqlOSFgaDbContext db)
        => ((DbContext)db).Set<Workspace>();

    private static object DescribeResource(SqlOSFgaResource resource)
        => new { resource.Id, resource.ParentId, resource.Name, resource.ResourceTypeId, resource.IsActive };

    private static async Task<object> DescribeWorkspaceAsync(ISqlOSFgaDbContext db, Workspace workspace, CancellationToken cancellationToken)
    {
        var resource = await db.Set<SqlOSFgaResource>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == workspace.ResourceId, cancellationToken);
        return new
        {
            workspace = new { workspace.Id, workspace.ResourceId, workspace.Name, workspace.ParentResourceId, workspace.IsActive },
            resource = resource == null ? null : DescribeResource(resource)
        };
    }

    /// <summary>Binds the JSON body to <typeparamref name="TRequest"/> and runs the probe.</summary>
    private static async Task<IResult> Invoke<TRequest, TResult>(HttpContext http, Func<TRequest, Task<TResult>> probe)
    {
        TRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<TRequest>(http.Request.Body, Json, http.RequestAborted);
        }
        catch (JsonException exception)
        {
            return Results.Json(new { error = "invalid_probe_request", message = exception.Message }, Json, statusCode: StatusCodes.Status400BadRequest);
        }

        if (request == null)
        {
            return Results.Json(new { error = "invalid_probe_request", message = "A JSON body is required." }, Json, statusCode: StatusCodes.Status400BadRequest);
        }

        return await Execute(http, () => probe(request));
    }

    private static async Task<IResult> Execute<TResult>(HttpContext http, Func<Task<TResult>> probe)
    {
        try
        {
            var result = await probe();
            return Results.Json(result, Json);
        }
        catch (Exception exception) when (IsRejection(exception))
        {
            // A documented API that rejects its input is a recorded outcome, not a probe failure.
            return Results.Json(
                new { error = exception.GetType().Name, message = exception.Message },
                Json,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static bool IsRejection(Exception exception)
        => exception is InvalidOperationException or ArgumentException or UnauthorizedAccessException or KeyNotFoundException
           || exception.GetType().Namespace?.StartsWith("SqlOS", StringComparison.Ordinal) == true;

    private sealed record FgaCheck(string SubjectId, string PermissionKey, string? ResourceId);

    private sealed record FgaResourceRequest(
        string Mode,
        string ResourceTypeId,
        string Name,
        string? ResourceId = null,
        string? ParentResourceId = null,
        string? Description = null);

    private sealed record WorkspaceRequest(
        string Name,
        string? Id = null,
        string? ResourceId = null,
        string? ParentResourceId = null,
        bool? IsActive = null);

    private sealed record SubjectRequest(
        string Type,
        string SubjectId,
        string DisplayName,
        string? Email = null,
        string? OrganizationId = null,
        string? ClientId = null,
        string? ClientSecretHash = null);

    private sealed record GrantRequest(string SubjectId, string ResourceId, string Role);

    private sealed record ValidateRequest(string Token, string Audience);

    private sealed record LogoutRequest(string? RefreshToken, string? SessionId);

    private sealed record UserRequest(string UserId, string? OrganizationId = null);
}
