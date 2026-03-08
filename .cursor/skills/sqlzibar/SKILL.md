---
name: sqlzibar
description: Guides implementation of authorization patterns using the Sqlzibar library (hierarchical RBAC for .NET + EF Core + SQL Server). Use when writing endpoints, background jobs, or queries that need authorization, when creating entities with resource hierarchies, when working with PagedSpec builders or SortablePagedSpecification, or when the user mentions Sqlzibar, authorization filters, resource access, or permission checks.
---

# Sqlzibar Authorization Patterns

Sqlzibar provides hierarchical RBAC via SQL Server TVFs composed into EF Core LINQ queries. All authorized entities implement `IHasResourceId`.

## Pattern Selection

| Scenario | Pattern |
|---|---|
| Paginated list endpoint | `PagedSpec.For<T>()` builder or `SortablePagedSpecification<T>` class |
| Non-paginated query (job, service) | `GetAuthorizationFilterAsync<T>()` + LINQ |
| Detail endpoint (get by ID) | `AuthorizedDetailAsync()` |
| Mutation (create/update/delete) | `CheckAccessAsync()` + `CreateResource()` |

## Key Imports

```csharp
using Sqlzibar.Interfaces;       // ISqlzibarAuthService, ISpecificationExecutor, ISqlzibarDbContext
using Sqlzibar.Extensions;        // CreateResource, AuthorizedDetailAsync
using Sqlzibar.Specifications;    // PagedSpec, SortablePagedSpecification, PagedSpecification
using Sqlzibar.Models;            // SqlzibarResource, IHasResourceId
```

## 1. Paginated List (Builder — preferred for most endpoints)

```csharp
var spec = PagedSpec.For<Order>(o => o.Id)
    .RequirePermission("ORDER_VIEW")
    .SortByString("name", o => o.Name, isDefault: true)
    .SortByString("status", o => o.Status)
    .Search(search, o => o.Name, o => o.Description)    // case-insensitive OR across fields
    .Where(o => o.IsActive)                              // additional business filter
    .Configure(q => q.Include(o => o.Customer))          // EF Core includes
    .Build(pageSize, cursor, sortBy, sortDir);           // sortDir accepts "asc"/"desc" string

var result = await executor.ExecuteAsync(context.Orders, spec, subjectId, o => new OrderDto { ... });
return Results.Ok(result);
```

Returns `PaginatedResult<T>` with `Data`, `NextCursor`, `HasMore`.

For numeric/custom sorts use `.SortBy()`:
```csharp
.SortBy("price", o => o.Price,
    serialize: v => v.ToString(CultureInfo.InvariantCulture),
    deserialize: s => decimal.Parse(s, CultureInfo.InvariantCulture))
```

## 2. Paginated List (Specification Class — for complex reusable queries)

```csharp
public class GetOrdersSpec : SortablePagedSpecification<Order>
{
    public GetOrdersSpec(int pageSize, string? search = null)
    {
        PageSize = pageSize;
        RegisterStringSort("name", o => o.Name, isDefault: true);
        Search(search, o => o.Name, o => o.CustomerName);  // replaces manual AddFilter for search
        AddFilter(o => o.IsActive);                         // AND-combined filters
    }

    public override string? RequiredPermission => "ORDER_VIEW";
    protected override Expression<Func<Order, string>> IdSelector => o => o.Id;

    public override IQueryable<Order> ConfigureQuery(IQueryable<Order> query)
        => query.Include(o => o.Customer);
}
```

## 3. Non-Paginated Query (Background Jobs, Services)

```csharp
var authFilter = await authService.GetAuthorizationFilterAsync<Order>(principalId, "ORDER_VIEW");

var recentOrders = await context.Orders
    .Where(authFilter)
    .Where(o => o.CreatedAt >= since)
    .Include(o => o.Customer)
    .ToListAsync(ct);
```

The auth filter is a plain `Expression<Func<T, bool>>` — compose it with any LINQ.

## 4. Detail Endpoint

```csharp
return await authService.AuthorizedDetailAsync(
    context.Orders.Include(o => o.Customer),
    o => o.Id == id,
    subjectId, "ORDER_VIEW",
    o => new OrderDto { Id = o.Id, Name = o.Name });
```

Returns 404 if not found, 403 if denied, 200 with mapped DTO.

## 5. Mutation with Resource Creation

```csharp
// Check permission at the parent scope
var access = await authService.CheckAccessAsync(subjectId, "ORDER_EDIT", parentResourceId);
if (!access.Allowed) return Results.Json(new { error = "Permission denied" }, statusCode: 403);

// Create the authorization resource (one line, not yet saved)
var resourceId = context.CreateResource(parentResourceId, request.Name, "order");

var order = new Order { ResourceId = resourceId, Name = request.Name };
context.Orders.Add(order);
await context.SaveChangesAsync();
```

## 6. Capability Check (Root-Level)

```csharp
bool isAdmin = await authService.HasCapabilityAsync(subjectId, "ADMIN_ACCESS");
```

## Key Rules

- Every authorized entity needs `IHasResourceId` with a `ResourceId` property
- `ResourceId` links to `SqlzibarResource` in the hierarchy tree
- Grants cascade down the resource tree — a grant at a parent covers all descendants
- `CreateResource()` adds the resource to the context but does not call `SaveChangesAsync()`
- The TVF is auto-created on startup via `UseSqlzibarAsync()` — no manual migration needed
- For unit tests, use `modelBuilder.ApplySqlzibarModel()` (no `GetType()`) to skip TVF registration
- For integration tests, use `modelBuilder.ApplySqlzibarModel(GetType())` with real SQL Server
