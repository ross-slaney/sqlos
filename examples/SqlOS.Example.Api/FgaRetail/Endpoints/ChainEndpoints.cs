using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.FgaRetail.Dtos;
using SqlOS.Example.Api.FgaRetail.Models;
using SqlOS.Example.Api.FgaRetail.Seeding;
using SqlOS.Example.Api.FgaRetail.Services;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Pagination;

namespace SqlOS.Example.Api.FgaRetail.Endpoints;

public static class ChainEndpoints
{
    public static void MapChainEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/chains").WithTags("Chains");

        group.MapGet("/", async (
            ExampleAppDbContext context,
            ISqlOSFgaAuthService fga,
            HttpContext http,
            int pageSize = 10,
            string? search = null,
            string? cursor = null,
            string? sortDir = null) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);
            var canView = await fga.BuildFilterAsync<Chain>(subjectId, RetailPermissionKeys.ChainView);

            IQueryable<Chain> query = context.Chains.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.ToLower();
                query = query.Where(c =>
                    c.Name.ToLower().Contains(term) ||
                    (c.Description != null && c.Description.ToLower().Contains(term)));
            }

            // Name is a declared page order (ExampleAppDbContext); ToPageAsync applies the
            // authorization filter and the cursor, and appends the Id tiebreaker.
            var ordered = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase)
                ? query.OrderByDescending(c => c.Name)
                : query.OrderBy(c => c.Name);

            SqlOSCursorPage<Chain> page;
            try
            {
                page = await ordered.ToPageAsync(canView, Math.Clamp(pageSize, 1, 100), cursor, http.RequestAborted);
            }
            catch (Exception exception) when (exception is SqlOSCursorException or ArgumentException { ParamName: "cursor" })
            {
                return Results.BadRequest(new { error = SqlOSCursorException.ErrorCode, message = exception.Message });
            }

            // One query for the page's location counts.
            var chainIds = page.Data.Select(c => c.Id).ToList();
            var locationCounts = await context.Locations
                .Where(l => chainIds.Contains(l.ChainId))
                .GroupBy(l => l.ChainId)
                .Select(g => new { ChainId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ChainId, x => x.Count, http.RequestAborted);

            return Results.Ok(page.ToResponse(c => new ChainDto
            {
                Id = c.Id,
                ResourceId = c.ResourceId,
                Name = c.Name,
                Description = c.Description,
                LocationCount = locationCounts.GetValueOrDefault(c.Id),
                CreatedAt = c.CreatedAt
            }));
        }).WithName("GetChains");

        group.MapGet("/{id}", async (
            string id,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

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
            RetailAuditService audit,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.ChainEdit, "retail_root");
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            // Retail is the lower-level/manual FGA sample. Recommended app entities use
            // ISqlOSResourceEntity and let SqlOSDbContext sync resources on SaveChanges.
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
            await audit.RecordAsync(
                http,
                context,
                "retail.chain.created",
                [new SqlOSAuditTarget("chain", chain.Id, chain.Name)],
                new Dictionary<string, object?>
                {
                    ["result"] = "success",
                    ["chainResourceId"] = chain.ResourceId,
                    ["headquartersAddress"] = chain.HeadquartersAddress
                },
                http.RequestAborted);

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
            RetailAuditService audit,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

            var chain = await context.Chains.FirstOrDefaultAsync(c => c.Id == id);
            if (chain is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.ChainEdit, chain.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            var previousName = chain.Name;
            chain.Name = request.Name;
            chain.Description = request.Description;
            chain.HeadquartersAddress = request.HeadquartersAddress;
            chain.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            await audit.RecordAsync(
                http,
                context,
                "retail.chain.updated",
                [new SqlOSAuditTarget("chain", chain.Id, chain.Name)],
                new Dictionary<string, object?>
                {
                    ["result"] = "success",
                    ["previousName"] = previousName,
                    ["chainResourceId"] = chain.ResourceId,
                    ["headquartersAddress"] = chain.HeadquartersAddress
                },
                http.RequestAborted);

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
            RetailAuditService audit,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

            var chain = await context.Chains.FirstOrDefaultAsync(c => c.Id == id);
            if (chain is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.ChainEdit, chain.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            context.Chains.Remove(chain);
            await context.SaveChangesAsync();
            await audit.RecordAsync(
                http,
                context,
                "retail.chain.deleted",
                [new SqlOSAuditTarget("chain", chain.Id, chain.Name)],
                new Dictionary<string, object?>
                {
                    ["result"] = "success",
                    ["chainResourceId"] = chain.ResourceId,
                    ["headquartersAddress"] = chain.HeadquartersAddress
                },
                http.RequestAborted);

            return Results.NoContent();
        }).WithName("DeleteChain");
    }
}
