using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.Models;
using SqlOS.Example.Api.Services;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Example.Api.Endpoints;

public static class ExampleEndpoints
{
    public static void MapExampleEndpoints(this WebApplication app)
    {
        var example = app.MapGroup("/api");
        example.ExcludeFromDescription();

        example.MapGet("/hello", (HttpContext context) =>
        {
            var subjectId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                message = "hello",
                userId = subjectId,
                email = context.User.FindFirst("email")?.Value,
                organizationId = context.User.FindFirst("org_id")?.Value,
                authenticationMethod = context.User.FindFirst("amr")?.Value
            });
        });

        example.MapGet("/me", (HttpContext context) =>
        {
            var claims = context.User.Claims.Select(x => new { x.Type, x.Value });
            return Results.Ok(new
            {
                subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
                organizationId = context.User.FindFirst("org_id")?.Value,
                clientId = context.User.FindFirst("client_id")?.Value,
                claims
            });
        });

        example.MapGet("/profile", async (ExampleAppDbContext context, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            var subjectId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                return Results.Unauthorized();
            }

            var profile = await context.ExampleUserProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.SqlOSUserId == subjectId, cancellationToken);

            return Results.Ok(new
            {
                userId = subjectId,
                email = httpContext.User.FindFirst("email")?.Value,
                displayName = httpContext.User.FindFirst("name")?.Value ?? httpContext.User.Identity?.Name,
                organizationId = httpContext.User.FindFirst("org_id")?.Value,
                profile = profile == null
                    ? null
                    : new
                    {
                        referralSource = profile.ReferralSource,
                        organizationName = profile.OrganizationName,
                        defaultEmail = profile.DefaultEmail,
                        displayName = profile.DisplayName,
                        createdAt = profile.CreatedAt,
                        updatedAt = profile.UpdatedAt
                    }
            });
        });

        example.MapPost("/sso-portal-links", async (
            ExampleSsoPortalLinkRequest request,
            SqlOSSsoPortalService portalService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var organizationId = httpContext.User.FindFirst("org_id")?.Value;
            var subjectId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(organizationId) || string.IsNullOrWhiteSpace(subjectId))
            {
                return Results.BadRequest(new { error = "Token must include sub and org_id to create an SSO setup link." });
            }

            try
            {
                return Results.Ok(await portalService.CreateSessionAsync(
                    new SqlOSCreateSsoPortalSessionRequest(
                        organizationId,
                        CreatedByUserId: subjectId,
                        Provider: request.Provider),
                    httpContext,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        example.MapGet("/mfa/status", async (SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            var subjectId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await authService.GetMfaStatusAsync(
                subjectId,
                httpContext.User.FindFirst("org_id")?.Value,
                cancellationToken));
        });

        example.MapPost("/mfa/totp/enroll/start", async (SqlOSTotpEnrollmentStartRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            var subjectId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await authService.StartTotpEnrollmentAsync(
                subjectId,
                request,
                httpContext.User.FindFirst("org_id")?.Value,
                cancellationToken));
        });

        example.MapPost("/mfa/totp/enroll/verify", async (SqlOSTotpEnrollmentVerifyRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            var subjectId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await authService.VerifyTotpEnrollmentAsync(request, httpContext, cancellationToken));
        });

        example.MapGet("/workspaces", async (ExampleAppDbContext context, ExampleFgaService fgaService, ISqlOSFgaAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            var organizationId = httpContext.User.FindFirst("org_id")?.Value;
            var subjectId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(organizationId) || string.IsNullOrWhiteSpace(subjectId))
            {
                return Results.BadRequest(new { error = "Token must include sub and org_id to access workspaces." });
            }

            await fgaService.EnsureUserAccessAsync(subjectId, organizationId, cancellationToken);

            var filter = await authService.GetAuthorizationFilterAsync<Workspace>(
                subjectId,
                ExampleFgaService.WorkspaceViewPermission);

            var results = await context.Workspaces
                .Where(x => x.OrganizationId == organizationId)
                .Where(filter)
                .OrderBy(x => x.Name)
                .Select(x => new { x.Id, x.ResourceId, x.Name, x.OrganizationId, x.CreatedAt })
                .ToListAsync(cancellationToken);

            return Results.Ok(results);
        });

        example.MapPost("/workspaces", async (SqlOSCreateWorkspaceRequest request, ExampleAppDbContext context, ExampleFgaService fgaService, ISqlOSFgaAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            var organizationId = httpContext.User.FindFirst("org_id")?.Value;
            var subjectId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(organizationId) || string.IsNullOrWhiteSpace(subjectId))
            {
                return Results.BadRequest(new { error = "Token must include sub and org_id to create workspaces." });
            }

            var workspaceName = request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(workspaceName))
            {
                return Results.BadRequest(new { error = "Workspace name is required." });
            }

            await fgaService.EnsureUserAccessAsync(subjectId, organizationId, cancellationToken);
            var organizationResourceId = ExampleFgaService.GetOrganizationResourceId(organizationId);
            var access = await authService.CheckAccessAsync(subjectId, ExampleFgaService.WorkspaceManagePermission, organizationResourceId);
            if (!access.Allowed)
            {
                return Results.Json(new { error = "Permission denied" }, statusCode: 403);
            }

            var workspaceId = $"wrk_{Guid.NewGuid():N}"[..28];
            var workspace = new Workspace
            {
                Id = workspaceId,
                OrganizationId = organizationId,
                ResourceId = ExampleFgaService.GetWorkspaceResourceId(workspaceId),
                Name = workspaceName,
                CreatedAt = DateTime.UtcNow
            };

            context.Workspaces.Add(workspace);
            await context.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/workspaces/{workspace.Id}", workspace);
        });
    }

    private sealed record ExampleSsoPortalLinkRequest(string? Provider);
}
