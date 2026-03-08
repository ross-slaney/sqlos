using Microsoft.EntityFrameworkCore;
using Sqlzibar.Example.Api.Data;
using Sqlzibar.Example.Api.Dtos;
using Sqlzibar.Example.Api.Middleware;
using Sqlzibar.Example.Api.Models;
using Sqlzibar.Example.Api.Seeding;
using Sqlzibar.Example.Api.Specifications;
using Sqlzibar.Extensions;
using Sqlzibar.Interfaces;

namespace Sqlzibar.Example.Api.Endpoints;

public static class LocationEndpoints
{
    public static void MapLocationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Locations");

        group.MapGet("/chains/{chainId}/locations", async (
            string chainId,
            RetailDbContext context,
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
            RetailDbContext context,
            ISqlzibarAuthService authService,
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
            RetailDbContext context,
            ISqlzibarAuthService authService,
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
    }
}
