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

public static class LocationEndpoints
{
    public static void MapLocationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Locations");

        group.MapGet("/locations", async (
            ExampleAppDbContext context,
            ISqlOSFgaAuthService fga,
            HttpContext http,
            int pageSize = 10,
            string? search = null,
            string? cursor = null) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);
            return await ListLocationsAsync(context, fga, subjectId, chainId: null, pageSize, search, cursor);
        }).WithName("GetAllLocations");

        group.MapGet("/chains/{chainId}/locations", async (
            string chainId,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService fga,
            HttpContext http,
            int pageSize = 10,
            string? search = null,
            string? cursor = null) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);
            return await ListLocationsAsync(context, fga, subjectId, chainId, pageSize, search, cursor);
        }).WithName("GetLocations");

        group.MapGet("/locations/{id}", async (
            string id,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

            return await authService.AuthorizedDetailAsync(
                context.Locations.Include(l => l.Chain).Include(l => l.InventoryItems),
                l => l.Id == id,
                subjectId, RetailPermissionKeys.LocationView,
                location => new LocationDetailDto
                {
                    Id = location.Id,
                    ResourceId = location.ResourceId,
                    ChainId = location.ChainId,
                    ChainName = location.Chain?.Name,
                    Name = location.Name,
                    StoreNumber = location.StoreNumber,
                    Address = location.Address,
                    City = location.City,
                    State = location.State,
                    ZipCode = location.ZipCode,
                    InventoryItemCount = location.InventoryItems.Count,
                    CreatedAt = location.CreatedAt,
                    UpdatedAt = location.UpdatedAt
                });
        }).WithName("GetLocation");

        group.MapPost("/chains/{chainId}/locations", async (
            string chainId,
            CreateLocationRequest request,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            RetailAuditService audit,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

            var chain = await context.Chains.FirstOrDefaultAsync(c => c.Id == chainId);
            if (chain is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.LocationEdit, chain.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            // Retail is the lower-level/manual FGA sample. Recommended app entities use
            // ISqlOSResourceEntity and let SqlOSDbContext sync resources on SaveChanges.
            var resourceId = context.CreateResource(chain.ResourceId, request.Name, RetailResourceTypeIds.Location);

            var location = new Location
            {
                ResourceId = resourceId,
                ChainId = chainId,
                Name = request.Name,
                StoreNumber = request.StoreNumber,
                Address = request.Address,
                City = request.City,
                State = request.State,
                ZipCode = request.ZipCode
            };
            context.Locations.Add(location);

            await context.SaveChangesAsync();
            await audit.RecordAsync(
                http,
                context,
                "retail.location.created",
                [
                    new SqlOSAuditTarget("chain", chain.Id, chain.Name),
                    new SqlOSAuditTarget("location", location.Id, location.Name)
                ],
                new Dictionary<string, object?>
                {
                    ["result"] = "success",
                    ["chainId"] = chain.Id,
                    ["chainResourceId"] = chain.ResourceId,
                    ["locationResourceId"] = location.ResourceId,
                    ["storeNumber"] = location.StoreNumber,
                    ["city"] = location.City,
                    ["state"] = location.State
                },
                http.RequestAborted);

            return Results.Created($"/api/locations/{location.Id}", new LocationDetailDto
            {
                Id = location.Id,
                ResourceId = location.ResourceId,
                ChainId = location.ChainId,
                ChainName = chain.Name,
                Name = location.Name,
                StoreNumber = location.StoreNumber,
                Address = location.Address,
                City = location.City,
                State = location.State,
                ZipCode = location.ZipCode,
                InventoryItemCount = 0,
                CreatedAt = location.CreatedAt,
                UpdatedAt = location.UpdatedAt
            });
        }).WithName("CreateLocation");

        group.MapPut("/locations/{id}", async (
            string id,
            UpdateLocationRequest request,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            RetailAuditService audit,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

            var location = await context.Locations.Include(l => l.Chain).FirstOrDefaultAsync(l => l.Id == id);
            if (location is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.LocationEdit, location.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            var previousName = location.Name;
            location.Name = request.Name;
            location.StoreNumber = request.StoreNumber;
            location.Address = request.Address;
            location.City = request.City;
            location.State = request.State;
            location.ZipCode = request.ZipCode;
            location.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            await audit.RecordAsync(
                http,
                context,
                "retail.location.updated",
                [
                    new SqlOSAuditTarget("chain", location.ChainId, location.Chain?.Name),
                    new SqlOSAuditTarget("location", location.Id, location.Name)
                ],
                new Dictionary<string, object?>
                {
                    ["result"] = "success",
                    ["previousName"] = previousName,
                    ["storeNumber"] = location.StoreNumber,
                    ["city"] = location.City,
                    ["state"] = location.State,
                    ["locationResourceId"] = location.ResourceId
                },
                http.RequestAborted);

            return Results.Ok(new LocationDetailDto
            {
                Id = location.Id,
                ResourceId = location.ResourceId,
                ChainId = location.ChainId,
                ChainName = location.Chain?.Name,
                Name = location.Name,
                StoreNumber = location.StoreNumber,
                Address = location.Address,
                City = location.City,
                State = location.State,
                ZipCode = location.ZipCode,
                InventoryItemCount = await context.InventoryItems.CountAsync(i => i.LocationId == location.Id),
                CreatedAt = location.CreatedAt,
                UpdatedAt = location.UpdatedAt
            });
        }).WithName("UpdateLocation");

        group.MapDelete("/locations/{id}", async (
            string id,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            RetailAuditService audit,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

            var location = await context.Locations.Include(l => l.Chain).FirstOrDefaultAsync(l => l.Id == id);
            if (location is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.LocationEdit, location.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            context.Locations.Remove(location);
            await context.SaveChangesAsync();
            await audit.RecordAsync(
                http,
                context,
                "retail.location.deleted",
                [
                    new SqlOSAuditTarget("chain", location.ChainId, location.Chain?.Name),
                    new SqlOSAuditTarget("location", location.Id, location.Name)
                ],
                new Dictionary<string, object?>
                {
                    ["result"] = "success",
                    ["storeNumber"] = location.StoreNumber,
                    ["city"] = location.City,
                    ["state"] = location.State,
                    ["locationResourceId"] = location.ResourceId
                },
                http.RequestAborted);

            return Results.NoContent();
        }).WithName("DeleteLocation");
    }

    private static async Task<IResult> ListLocationsAsync(
        ExampleAppDbContext context,
        ISqlOSFgaAuthService fga,
        string subjectId,
        string? chainId,
        int pageSize,
        string? search,
        string? cursor)
    {
        var canView = await fga.BuildFilterAsync<Location>(subjectId, RetailPermissionKeys.LocationView);
        var size = Math.Clamp(pageSize, 1, 100);

        var query = context.Locations.Where(canView);

        if (chainId != null)
        {
            query = query.Where(l => l.ChainId == chainId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.ToLower();
            query = query.Where(l =>
                l.Name.ToLower().Contains(term) ||
                (l.StoreNumber != null && l.StoreNumber.ToLower().Contains(term)));
        }

        // Keyset cursor: skip everything at or before the last row of the previous page.
        if (cursor != null)
        {
            var (after, afterId) = RetailCursor.Decode(cursor);
            query = query.Where(l =>
                (l.StoreNumber ?? "").CompareTo(after) > 0 ||
                ((l.StoreNumber ?? "") == after && l.Id.CompareTo(afterId) > 0));
        }

        var rows = await query
            .OrderBy(l => l.StoreNumber ?? "")
            .ThenBy(l => l.Id) // unique tiebreaker keeps pages deterministic
            .Select(l => new LocationDto
            {
                Id = l.Id,
                ResourceId = l.ResourceId,
                ChainId = l.ChainId,
                ChainName = l.Chain!.Name,
                Name = l.Name,
                StoreNumber = l.StoreNumber,
                City = l.City,
                State = l.State,
                CreatedAt = l.CreatedAt
            })
            .Take(size + 1)
            .ToListAsync();

        var hasNextPage = rows.Count > size;
        if (hasNextPage) rows.RemoveAt(size);

        string? nextCursor = null;
        if (hasNextPage)
        {
            var last = rows[^1];
            nextCursor = RetailCursor.Encode(last.StoreNumber ?? "", last.Id);
        }

        return Results.Ok(new { data = rows, pageSize = size, nextCursor, hasNextPage });
    }
}
