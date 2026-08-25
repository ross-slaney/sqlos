using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.FgaRetail.Dtos;
using SqlOS.Example.Api.FgaRetail.Models;
using SqlOS.Example.Api.FgaRetail.Seeding;
using SqlOS.Example.Api.FgaRetail.Services;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;

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
            string? sortBy = null,
            string? sortDir = null) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);
            var canView = await fga.BuildFilterAsync<Chain>(subjectId, RetailPermissionKeys.ChainView);
            var size = Math.Clamp(pageSize, 1, 100);
            var descending = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
            var sortKey = string.Equals(sortBy, "description", StringComparison.OrdinalIgnoreCase)
                ? "description"
                : "name";

            var query = context.Chains.Where(canView);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.ToLower();
                query = query.Where(c =>
                    c.Name.ToLower().Contains(term) ||
                    (c.Description != null && c.Description.ToLower().Contains(term)));
            }

            // Keyset cursor: skip everything at or before the last row of the previous page.
            if (cursor != null)
            {
                var (after, afterId) = RetailCursor.Decode(cursor);
                query = (sortKey, descending) switch
                {
                    ("description", false) => query.Where(c =>
                        (c.Description ?? "").CompareTo(after) > 0 ||
                        ((c.Description ?? "") == after && c.Id.CompareTo(afterId) > 0)),
                    ("description", true) => query.Where(c =>
                        (c.Description ?? "").CompareTo(after) < 0 ||
                        ((c.Description ?? "") == after && c.Id.CompareTo(afterId) < 0)),
                    (_, false) => query.Where(c =>
                        c.Name.CompareTo(after) > 0 ||
                        (c.Name == after && c.Id.CompareTo(afterId) > 0)),
                    (_, true) => query.Where(c =>
                        c.Name.CompareTo(after) < 0 ||
                        (c.Name == after && c.Id.CompareTo(afterId) < 0)),
                };
            }

            // Always end the ordering with the unique Id tiebreaker so pages are deterministic.
            query = (sortKey, descending) switch
            {
                ("description", false) => query.OrderBy(c => c.Description ?? "").ThenBy(c => c.Id),
                ("description", true) => query.OrderByDescending(c => c.Description ?? "").ThenByDescending(c => c.Id),
                (_, false) => query.OrderBy(c => c.Name).ThenBy(c => c.Id),
                (_, true) => query.OrderByDescending(c => c.Name).ThenByDescending(c => c.Id),
            };

            var rows = await query
                .Select(c => new ChainDto
                {
                    Id = c.Id,
                    ResourceId = c.ResourceId,
                    Name = c.Name,
                    Description = c.Description,
                    LocationCount = c.Locations.Count,
                    CreatedAt = c.CreatedAt
                })
                .Take(size + 1)
                .ToListAsync();

            var hasNextPage = rows.Count > size;
            if (hasNextPage) rows.RemoveAt(size);

            string? nextCursor = null;
            if (hasNextPage)
            {
                var last = rows[^1];
                nextCursor = RetailCursor.Encode(
                    sortKey == "description" ? last.Description ?? "" : last.Name,
                    last.Id);
            }

            return Results.Ok(new { data = rows, pageSize = size, nextCursor, hasNextPage });
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
