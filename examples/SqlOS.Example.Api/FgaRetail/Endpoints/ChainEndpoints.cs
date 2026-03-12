using Microsoft.EntityFrameworkCore;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.FgaRetail.Middleware;
using SqlOS.Example.Api.FgaRetail.Dtos;
using SqlOS.Example.Api.FgaRetail.Models;
using SqlOS.Example.Api.FgaRetail.Seeding;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Specifications;

namespace SqlOS.Example.Api.FgaRetail.Endpoints;

public static class ChainEndpoints
{
    public static void MapChainEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/chains").WithTags("Chains");

        group.MapGet("/", async (
            ExampleAppDbContext context,
            ISpecificationExecutor executor,
            HttpContext http,
            int pageSize = 10,
            string? search = null,
            string? cursor = null,
            string? sortBy = null,
            string? sortDir = null) =>
        {
            var subjectId = http.GetSubjectId();

            var spec = PagedSpec.For<Chain>(c => c.Id)
                .RequirePermission(RetailPermissionKeys.ChainView)
                .SortByString("name", c => c.Name, isDefault: true)
                .SortByString("description", c => c.Description ?? "")
                .Search(search, c => c.Name, c => c.Description)
                .Configure(q => q.Include(c => c.Locations))
                .Build(pageSize, cursor, sortBy, sortDir);

            var result = await executor.ExecuteAsync(
                context.Chains, spec, subjectId,
                c => new ChainDto
                {
                    Id = c.Id,
                    ResourceId = c.ResourceId,
                    Name = c.Name,
                    Description = c.Description,
                    LocationCount = c.Locations.Count,
                    CreatedAt = c.CreatedAt
                });
            return Results.Ok(result);
        }).WithName("GetChains");

        group.MapGet("/{id}", async (
            string id,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            HttpContext http) =>
        {
            var subjectId = http.GetSubjectId();

            return await authService.AuthorizedDetailAsync(
                context.Chains.Include(c => c.Locations),
                c => c.Id == id,
                subjectId, RetailPermissionKeys.ChainView,
                chain => new ChainDetailDto
                {
                    Id = chain.Id,
                    ResourceId = chain.ResourceId,
                    Name = chain.Name,
                    Description = chain.Description,
                    HeadquartersAddress = chain.HeadquartersAddress,
                    LocationCount = chain.Locations.Count,
                    CreatedAt = chain.CreatedAt,
                    UpdatedAt = chain.UpdatedAt
                });
        }).WithName("GetChain");

        group.MapPost("/", async (
            CreateChainRequest request,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            HttpContext http) =>
        {
            var subjectId = http.GetSubjectId();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.ChainEdit, "retail_root");
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            var resourceId = context.CreateResource("retail_root", request.Name, RetailResourceTypeIds.Chain);

            var chain = new Chain
            {
                ResourceId = resourceId,
                Name = request.Name,
                Description = request.Description,
                HeadquartersAddress = request.HeadquartersAddress
            };
            context.Chains.Add(chain);

            await context.SaveChangesAsync();

            return Results.Created($"/api/chains/{chain.Id}", new ChainDetailDto
            {
                Id = chain.Id,
                ResourceId = chain.ResourceId,
                Name = chain.Name,
                Description = chain.Description,
                HeadquartersAddress = chain.HeadquartersAddress,
                LocationCount = 0,
                CreatedAt = chain.CreatedAt,
                UpdatedAt = chain.UpdatedAt
            });
        }).WithName("CreateChain");

        group.MapPut("/{id}", async (
            string id,
            UpdateChainRequest request,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            HttpContext http) =>
        {
            var subjectId = http.GetSubjectId();

            var chain = await context.Chains.FirstOrDefaultAsync(c => c.Id == id);
            if (chain is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.ChainEdit, chain.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            chain.Name = request.Name;
            chain.Description = request.Description;
            chain.HeadquartersAddress = request.HeadquartersAddress;
            chain.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            return Results.Ok(new ChainDetailDto
            {
                Id = chain.Id,
                ResourceId = chain.ResourceId,
                Name = chain.Name,
                Description = chain.Description,
                HeadquartersAddress = chain.HeadquartersAddress,
                LocationCount = await context.Locations.CountAsync(l => l.ChainId == chain.Id),
                CreatedAt = chain.CreatedAt,
                UpdatedAt = chain.UpdatedAt
            });
        }).WithName("UpdateChain");

        group.MapDelete("/{id}", async (
            string id,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            HttpContext http) =>
        {
            var subjectId = http.GetSubjectId();

            var chain = await context.Chains.FirstOrDefaultAsync(c => c.Id == id);
            if (chain is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.ChainEdit, chain.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            context.Chains.Remove(chain);
            await context.SaveChangesAsync();

            return Results.NoContent();
        }).WithName("DeleteChain");
    }
}
