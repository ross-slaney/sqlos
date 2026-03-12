using Microsoft.EntityFrameworkCore;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.FgaRetail.Dtos;
using SqlOS.Example.Api.FgaRetail.Middleware;
using SqlOS.Example.Api.FgaRetail.Models;
using SqlOS.Example.Api.FgaRetail.Seeding;
using SqlOS.Example.Api.FgaRetail.Specifications;
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
            ISpecificationExecutor executor,
            HttpContext http,
            int pageSize = 10,
            string? search = null,
            string? cursor = null) =>
        {
            var subjectId = http.GetSubjectId();
            var spec = new GetLocationsSpecification(pageSize, search) { Cursor = cursor };
            var result = await executor.ExecuteAsync(
                context.Locations, spec, subjectId,
                l => new LocationDto
                {
                    Id = l.Id,
                    ResourceId = l.ResourceId,
                    ChainId = l.ChainId,
                    ChainName = l.Chain?.Name,
                    Name = l.Name,
                    StoreNumber = l.StoreNumber,
                    City = l.City,
                    State = l.State,
                    CreatedAt = l.CreatedAt
                });
            return Results.Ok(result);
        }).WithName("GetAllLocations");

        group.MapGet("/chains/{chainId}/locations", async (
            string chainId,
            ExampleAppDbContext context,
            ISpecificationExecutor executor,
            HttpContext http,
            int pageSize = 10,
            string? search = null,
            string? cursor = null) =>
        {
            var subjectId = http.GetSubjectId();
            var spec = new GetLocationsSpecification(pageSize, search, chainId) { Cursor = cursor };
            var result = await executor.ExecuteAsync(
                context.Locations, spec, subjectId,
                l => new LocationDto
                {
                    Id = l.Id,
                    ResourceId = l.ResourceId,
                    ChainId = l.ChainId,
                    ChainName = l.Chain?.Name,
                    Name = l.Name,
                    StoreNumber = l.StoreNumber,
                    City = l.City,
                    State = l.State,
                    CreatedAt = l.CreatedAt
                });
            return Results.Ok(result);
        }).WithName("GetLocations");

        group.MapGet("/locations/{id}", async (
            string id,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            HttpContext http) =>
        {
            var subjectId = http.GetSubjectId();

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
            HttpContext http) =>
        {
            var subjectId = http.GetSubjectId();

            var chain = await context.Chains.FirstOrDefaultAsync(c => c.Id == chainId);
            if (chain is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.LocationEdit, chain.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

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
            HttpContext http) =>
        {
            var subjectId = http.GetSubjectId();

            var location = await context.Locations.Include(l => l.Chain).FirstOrDefaultAsync(l => l.Id == id);
            if (location is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.LocationEdit, location.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            location.Name = request.Name;
            location.StoreNumber = request.StoreNumber;
            location.Address = request.Address;
            location.City = request.City;
            location.State = request.State;
            location.ZipCode = request.ZipCode;
            location.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

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
            HttpContext http) =>
        {
            var subjectId = http.GetSubjectId();

            var location = await context.Locations.FirstOrDefaultAsync(l => l.Id == id);
            if (location is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.LocationEdit, location.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            context.Locations.Remove(location);
            await context.SaveChangesAsync();

            return Results.NoContent();
        }).WithName("DeleteLocation");
    }
}
