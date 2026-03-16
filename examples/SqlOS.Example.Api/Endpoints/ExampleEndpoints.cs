using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.Models;
using SqlOS.Example.Api.Services;
using SqlOS.Fga.Extensions;
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

            await fgaService.EnsureUserAccessAsync(subjectId, organizationId, cancellationToken);
            var organizationResourceId = ExampleFgaService.GetOrganizationResourceId(organizationId);
            var access = await authService.CheckAccessAsync(subjectId, ExampleFgaService.WorkspaceManagePermission, organizationResourceId);
            if (!access.Allowed)
            {
                return Results.Json(new { error = "Permission denied" }, statusCode: 403);
            }

            var workspace = new Workspace
            {
                Id = $"wrk_{Guid.NewGuid():N}"[..28],
                OrganizationId = organizationId,
                Name = request.Name,
                CreatedAt = DateTime.UtcNow
            };
            workspace.ResourceId = fgaService.CreateWorkspaceResource(workspace);

            context.Workspaces.Add(workspace);
            await context.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/workspaces/{workspace.Id}", workspace);
        });
    }
}
