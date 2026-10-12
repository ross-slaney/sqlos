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

public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Inventory");

        // Name, Sku and Price are declared page orders (ExampleAppDbContext). ToPageAsync applies the
        // authorization filter and the cursor, and appends the Id tiebreaker.
        group.MapGet("/locations/{locationId}/inventory", async (
            string locationId,
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
            var canView = await fga.BuildFilterAsync<InventoryItem>(subjectId, RetailPermissionKeys.InventoryView);
            var descending = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);

            IQueryable<InventoryItem> query = context.InventoryItems
                .AsNoTracking()
                .Include(i => i.Location)
                .Where(i => i.LocationId == locationId);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.ToLower();
                query = query.Where(i =>
                    i.Name.ToLower().Contains(term) ||
                    i.Sku.ToLower().Contains(term));
            }

            var ordered = (sortBy?.ToLowerInvariant(), descending) switch
            {
                ("sku", false) => query.OrderBy(i => i.Sku),
                ("sku", true) => query.OrderByDescending(i => i.Sku),
                ("price", false) => query.OrderBy(i => i.Price),
                ("price", true) => query.OrderByDescending(i => i.Price),
                (_, false) => query.OrderBy(i => i.Name),
                (_, true) => query.OrderByDescending(i => i.Name),
            };

            SqlOSCursorPage<InventoryItem> page;
            try
            {
                page = await ordered.ToPageAsync(canView, Math.Clamp(pageSize, 1, 100), cursor, http.RequestAborted);
            }
            catch (Exception exception) when (exception is SqlOSCursorException or ArgumentException { ParamName: "cursor" })
            {
                return Results.BadRequest(new { error = SqlOSCursorException.ErrorCode, message = exception.Message });
            }

            return Results.Ok(page.ToResponse(i => new InventoryItemDto
            {
                Id = i.Id,
                ResourceId = i.ResourceId,
                LocationId = i.LocationId,
                LocationName = i.Location!.Name,
                Sku = i.Sku,
                Name = i.Name,
                Price = i.Price,
                QuantityOnHand = i.QuantityOnHand,
                CreatedAt = i.CreatedAt
            }));
        }).WithName("GetInventoryItems");

        group.MapGet("/inventory/{id}", async (
            string id,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

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
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            RetailAuditService audit,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

            var location = await context.Locations.FirstOrDefaultAsync(l => l.Id == locationId);
            if (location is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.InventoryEdit, location.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            // Retail is the lower-level/manual FGA sample. Recommended app entities use
            // ISqlOSResourceEntity and let SqlOSDbContext sync resources on SaveChanges.
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
            await audit.RecordAsync(
                http,
                context,
                "retail.inventory_item.created",
                [
                    new SqlOSAuditTarget("location", location.Id, location.Name),
                    new SqlOSAuditTarget("inventory_item", item.Id, item.Name)
                ],
                new Dictionary<string, object?>
                {
                    ["result"] = "success",
                    ["sku"] = item.Sku,
                    ["quantityOnHand"] = item.QuantityOnHand,
                    ["price"] = item.Price,
                    ["locationId"] = location.Id,
                    ["locationResourceId"] = location.ResourceId,
                    ["inventoryResourceId"] = item.ResourceId
                },
                http.RequestAborted);

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

        group.MapPut("/inventory/{id}", async (
            string id,
            UpdateInventoryItemRequest request,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            RetailAuditService audit,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

            var item = await context.InventoryItems.Include(i => i.Location).FirstOrDefaultAsync(i => i.Id == id);
            if (item is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.InventoryEdit, item.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            var previousQuantity = item.QuantityOnHand;
            var previousPrice = item.Price;
            item.Name = request.Name;
            item.Description = request.Description;
            item.Price = request.Price;
            item.QuantityOnHand = request.QuantityOnHand;
            item.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            await audit.RecordAsync(
                http,
                context,
                "retail.inventory_item.updated",
                [
                    new SqlOSAuditTarget("location", item.LocationId, item.Location?.Name),
                    new SqlOSAuditTarget("inventory_item", item.Id, item.Name)
                ],
                new Dictionary<string, object?>
                {
                    ["result"] = "success",
                    ["sku"] = item.Sku,
                    ["previousQuantityOnHand"] = previousQuantity,
                    ["quantityOnHand"] = item.QuantityOnHand,
                    ["quantityDelta"] = item.QuantityOnHand - previousQuantity,
                    ["previousPrice"] = previousPrice,
                    ["price"] = item.Price,
                    ["locationId"] = item.LocationId,
                    ["inventoryResourceId"] = item.ResourceId
                },
                http.RequestAborted);

            return Results.Ok(new InventoryItemDetailDto
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
        }).WithName("UpdateInventoryItem");

        group.MapDelete("/inventory/{id}", async (
            string id,
            ExampleAppDbContext context,
            ISqlOSFgaAuthService authService,
            RetailAuditService audit,
            HttpContext http) =>
        {
            var subjectId = RetailSubjectResolver.ResolveSubjectId(http);

            var item = await context.InventoryItems.Include(i => i.Location).FirstOrDefaultAsync(i => i.Id == id);
            if (item is null) return Results.NotFound();

            var access = await authService.CheckAccessAsync(subjectId, RetailPermissionKeys.InventoryEdit, item.ResourceId);
            if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

            context.InventoryItems.Remove(item);
            await context.SaveChangesAsync();
            await audit.RecordAsync(
                http,
                context,
                "retail.inventory_item.deleted",
                [
                    new SqlOSAuditTarget("location", item.LocationId, item.Location?.Name),
                    new SqlOSAuditTarget("inventory_item", item.Id, item.Name)
                ],
                new Dictionary<string, object?>
                {
                    ["result"] = "success",
                    ["sku"] = item.Sku,
                    ["quantityOnHand"] = item.QuantityOnHand,
                    ["price"] = item.Price,
                    ["locationId"] = item.LocationId,
                    ["inventoryResourceId"] = item.ResourceId
                },
                http.RequestAborted);

            return Results.NoContent();
        }).WithName("DeleteInventoryItem");
    }
}
