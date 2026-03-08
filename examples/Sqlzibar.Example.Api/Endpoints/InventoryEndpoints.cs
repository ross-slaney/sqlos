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

public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Inventory");

        group.MapGet("/locations/{locationId}/inventory", async (
            string locationId,
            RetailDbContext context,
            ISpecificationExecutor executor,
            HttpContext http,
            int pageSize = 10,
            string? search = null,
            string? cursor = null,
            string? sortBy = null,
            string? sortDir = null) =>
        {
            var subjectId = http.GetSubjectId();
            var descending = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
            var spec = new GetInventoryItemsSpecification(pageSize, search, locationId, sortBy, descending) { Cursor = cursor };
            var result = await executor.ExecuteAsync(
                context.InventoryItems, spec, subjectId,
                i => new InventoryItemDto
                {
                    Id = i.Id,
                    ResourceId = i.ResourceId,
                    LocationId = i.LocationId,
                    LocationName = i.Location?.Name,
                    Sku = i.Sku,
                    Name = i.Name,
                    Price = i.Price,
                    QuantityOnHand = i.QuantityOnHand,
                    CreatedAt = i.CreatedAt
                });
            return Results.Ok(result);
        }).WithName("GetInventoryItems");

        group.MapGet("/inventory/{id}", async (
            string id,
            RetailDbContext context,
            ISqlzibarAuthService authService,
            HttpContext http) =>
        {
            var subjectId = http.GetSubjectId();

            return await authService.AuthorizedDetailAsync(
                context.InventoryItems.Include(i => i.Location),
                i => i.Id == id,
                subjectId, RetailPermissionKeys.InventoryView,
                item => new InventoryItemDetailDto
                {
                    Id = item.Id,
                    ResourceId = item.ResourceId,
                    LocationId = item.LocationId,
                    LocationName = item.Location?.Name,
                    Sku = item.Sku,
                    Name = item.Name,
                    Description = item.Description,
                    Price = item.Price,
                    QuantityOnHand = item.QuantityOnHand,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt
                });
        }).WithName("GetInventoryItem");

        group.MapPost("/locations/{locationId}/inventory", async (
            string locationId,
            CreateInventoryItemRequest request,
            RetailDbContext context,
            ISqlzibarAuthService authService,
            HttpContext http) =>
        {
            var subjectId = http.GetSubjectId();

            var location = await context.Locations.FirstOrDefaultAsync(l => l.Id == locationId);
            if (location is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.InventoryEdit, location.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            var resourceId = context.CreateResource(location.ResourceId, request.Name, RetailResourceTypeIds.InventoryItem);

            var item = new InventoryItem
            {
                ResourceId = resourceId,
                LocationId = locationId,
                Sku = request.Sku,
                Name = request.Name,
                Description = request.Description,
                Price = request.Price,
                QuantityOnHand = request.QuantityOnHand
            };
            context.InventoryItems.Add(item);

            await context.SaveChangesAsync();

            return Results.Created($"/api/inventory/{item.Id}", new InventoryItemDetailDto
            {
                Id = item.Id,
                ResourceId = item.ResourceId,
                LocationId = item.LocationId,
                LocationName = location.Name,
                Sku = item.Sku,
                Name = item.Name,
                Description = item.Description,
                Price = item.Price,
                QuantityOnHand = item.QuantityOnHand,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt
            });
        }).WithName("CreateInventoryItem");
    }
}
