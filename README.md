# Sqlzibar

**Hierarchical Role-Based Access Control (RBAC) for .NET + EF Core + SQL Server**

Sqlzibar provides a complete authorization system that plugs into any EF Core application with minimal configuration. It uses a resource hierarchy with SQL Server table-valued functions (TVFs) to deliver fast, composable authorization queries that work directly inside your LINQ-to-SQL pipelines.

**[Paper: SHRBAC](paper/shrbac-compsac-2026.pdf)** — Hierarchical RBAC with SQL Server TVFs.

## Features

- **Hierarchical Resource Model** — Resources form a tree. Grants on parent resources cascade to all descendants.
- **TVF-Based Authorization** — A SQL Server inline TVF enables single-query authorization via `CROSS APPLY`. No N+1 checks.
- **Minimal Consumer API** — One interface method + one line in `OnModelCreating`. No DbSet properties needed.
- **Group Membership** — Users and service accounts can belong to groups. Group grants apply to all members.
- **Time-Bounded Grants** — Optional `EffectiveFrom` / `EffectiveTo` for temporary access.
- **Specification Executor** — Paginated, authorized queries with cursor pagination, sorting, and search out of the box.
- **Built-in Dashboard** — Embedded web UI at `/sqlzibar` to browse resources, subjects, grants, roles, and permissions.
- **Access Tracing** — Full diagnostic trace of why access was granted or denied.

## Quick Start

### 1. Install

During development as a project reference:

```xml
<PackageReference Include="Sqlzibar" Version="1.0.2" />
```

### 2. Add to Your DbContext

Implement `ISqlzibarDbContext` on your existing DbContext. You only need **one method** — no DbSet properties required:

```csharp
using Sqlzibar.Interfaces;
using Sqlzibar.Models;
using Sqlzibar.Extensions;

public class AppDbContext : DbContext, ISqlzibarDbContext
{
    public DbSet<Project> Projects => Set<Project>();

    public IQueryable<SqlzibarAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlzibarModel(GetType());
    }
}
```

### 3. Register Services

```csharp
builder.Services.AddSqlzibar<AppDbContext>(options =>
{
    options.Schema = "dbo";
    options.RootResourceId = "portal_root";
    options.RootResourceName = "Portal Root";
});

var app = builder.Build();

await app.UseSqlzibarAsync();            // Initialize TVF + seed core data
app.UseSqlzibarDashboard("/sqlzibar");   // Optional: mount the dashboard
```

### 4. Seed Your Authorization Data

```csharp
using var scope = app.Services.CreateScope();
var seeder = scope.ServiceProvider.GetRequiredService<SqlzibarSeedService>();

await seeder.SeedAuthorizationDataAsync(new SqlzibarSeedData
{
    ResourceTypes = [
        new() { Id = "agency", Name = "Agency" },
        new() { Id = "project", Name = "Project" },
    ],
    Roles = [
        new() { Id = "role_admin", Key = "Admin", Name = "Administrator" },
        new() { Id = "role_viewer", Key = "Viewer", Name = "Viewer" },
    ],
    Permissions = [
        new() { Id = "perm_view", Key = "PROJECT_VIEW", Name = "View Projects" },
        new() { Id = "perm_edit", Key = "PROJECT_EDIT", Name = "Edit Projects" },
    ],
    RolePermissions = [
        ("Admin", ["PROJECT_VIEW", "PROJECT_EDIT"]),
        ("Viewer", ["PROJECT_VIEW"]),
    ]
});
```

## How It Works

Resources form a tree rooted at a configurable root node. A grant at a parent cascades to all descendants:

```
portal_root
  ├── agency:acme
  │   ├── project:website
  │   └── project:mobile_app
  └── agency:globex
      └── project:dashboard
```

A grant at `agency:acme` with role `Admin` gives the subject admin access to `project:website` and `project:mobile_app`. The TVF walks up the ancestor chain for any target resource, matching grants at each level.

| Concept        | Description                                                          |
| -------------- | -------------------------------------------------------------------- |
| **Subject**    | A user, group, or service account that can receive grants            |
| **Resource**   | A node in the hierarchy (agency, project, team, etc.)                |
| **Grant**      | Links a subject to a resource with a role, optionally time-bounded |
| **Role**       | A named set of permissions (e.g., Admin, Viewer)                     |
| **Permission** | A specific capability (e.g., `PROJECT_EDIT`)                         |

EF Core composes the TVF into your LINQ queries via `CROSS APPLY`, meaning authorization filtering happens in the same SQL query as your data fetch — no round trips.

## Building APIs

Pick the right pattern for each shape of query:

| Scenario                                                | Pattern                                | Key API                                                |
| ------------------------------------------------------- | -------------------------------------- | ------------------------------------------------------ |
| Paginated list endpoints                                | Builder or specification class         | `PagedSpec.For<T>()` / `SortablePagedSpecification<T>` |
| Non-paginated queries (jobs, services, one-off fetches) | Raw auth filter + LINQ                 | `GetAuthorizationFilterAsync<T>()`                     |
| Detail endpoints (get by ID)                            | One-liner with 404/403 handling        | `AuthorizedDetailAsync()`                              |
| Mutations (create, update, delete)                      | Point access check + resource creation | `CheckAccessAsync()` + `CreateResource()`              |

Every entity that needs authorization implements `IHasResourceId`:

```csharp
public class Project : IHasResourceId
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string ResourceId { get; set; } = string.Empty;
}
```

### Authorized List Endpoints

Use the fluent builder to create paginated, authorized, searchable, sortable list queries inline — no specification class needed:

```csharp
app.MapGet("/api/projects", async (
    AppDbContext context,
    ISpecificationExecutor executor,
    HttpContext http,
    int pageSize = 20,
    string? search = null,
    string? cursor = null,
    string? sortBy = null,
    string? sortDir = null) =>
{
    var subjectId = http.GetSubjectId();

    var spec = PagedSpec.For<Project>(p => p.Id)
        .RequirePermission("PROJECT_VIEW")
        .SortByString("name", p => p.Name, isDefault: true)
        .SortByString("status", p => p.Status)
        .Search(search, p => p.Name, p => p.Description)
        .Where(p => p.IsActive)
        .Configure(q => q.Include(p => p.Agency))
        .Build(pageSize, cursor, sortBy, sortDir);

    var result = await executor.ExecuteAsync(
        context.Projects, spec, subjectId,
        p => new { p.Id, p.Name, p.Status, Agency = p.Agency.Name });

    return Results.Ok(result);
});
```

This single call:

- Filters to only rows the subject is authorized to see (via TVF)
- Applies search across the specified fields (case-insensitive OR)
- Sorts by the requested field with cursor-based pagination
- Returns a `PaginatedResult<T>` with `Data`, `NextCursor`, and `HasMore`

### Authorized Detail Endpoints

One-liner for GET-by-ID with automatic 404/403 handling:

```csharp
app.MapGet("/api/projects/{id}", async (
    string id,
    AppDbContext context,
    ISqlzibarAuthService authService,
    HttpContext http) =>
{
    var subjectId = http.GetSubjectId();

    return await authService.AuthorizedDetailAsync(
        context.Projects.Include(p => p.Agency),
        p => p.Id == id,
        subjectId, "PROJECT_VIEW",
        p => new { p.Id, p.Name, p.Description, Agency = p.Agency.Name });
});
```

Returns `404` if not found, `403` if denied, `200` with the mapped DTO if authorized.

### Create Endpoints with Resource Creation

Use `CreateResource` to add the authorization resource in one line:

```csharp
app.MapPost("/api/projects", async (
    CreateProjectRequest request,
    AppDbContext context,
    ISqlzibarAuthService authService,
    HttpContext http) =>
{
    var subjectId = http.GetSubjectId();

    var access = await authService.CheckAccessAsync(
        subjectId, "PROJECT_EDIT", request.AgencyResourceId);
    if (!access.Allowed)
        return Results.Json(new { error = "Permission denied" }, statusCode: 403);

    var resourceId = context.CreateResource(
        request.AgencyResourceId, request.Name, "project");

    var project = new Project
    {
        ResourceId = resourceId,
        Name = request.Name,
        Description = request.Description
    };
    context.Projects.Add(project);
    await context.SaveChangesAsync();

    return Results.Created($"/api/projects/{project.Id}", project);
});
```

### Reusable Specifications

For complex queries that are reused across multiple endpoints, extend `SortablePagedSpecification<T>` instead of the inline builder:

```csharp
public class GetProjectsSpec : SortablePagedSpecification<Project>
{
    public GetProjectsSpec(int pageSize, string? search = null, string? agencyId = null)
    {
        PageSize = pageSize;
        RegisterStringSort("name", p => p.Name, isDefault: true);
        RegisterStringSort("status", p => p.Status);

        if (agencyId != null)
            AddFilter(p => p.AgencyId == agencyId);

        Search(search, p => p.Name, p => p.Description);
    }

    public override string? RequiredPermission => "PROJECT_VIEW";
    protected override Expression<Func<Project, string>> IdSelector => p => p.Id;

    public override IQueryable<Project> ConfigureQuery(IQueryable<Project> query)
        => query.Include(p => p.Agency);
}
```

### Write Authorization

For mutation operations, check access explicitly:

```csharp
var result = await authService.CheckAccessAsync(
    subjectId, "PROJECT_EDIT", project.ResourceId);

if (!result.Allowed)
    return Results.Json(new { error = "Permission denied" }, statusCode: 403);
```

### Capability Check (Root-Level)

Check if a principal has a permission at the root level (e.g., system admin):

```csharp
bool isAdmin = await authService.HasCapabilityAsync(principalId, "ADMIN_ACCESS");
```

### Authorized Queries Outside Endpoints

`GetAuthorizationFilterAsync<T>()` returns a plain `Expression<Func<T, bool>>` you can drop into any LINQ pipeline — background jobs, scheduled tasks, SignalR hubs, whatever:

```csharp
// Get the authorization filter — this is just an Expression<Func<Order, bool>>
var authFilter = await _authService.GetAuthorizationFilterAsync<Order>(
    subjectId, "ORDER_VIEW");

// Use it like any other LINQ predicate
var recentOrders = await _context.Orders
    .Where(authFilter)
    .Where(o => o.CreatedAt >= since)
    .Include(o => o.Customer)
    .ToListAsync(ct);

foreach (var order in recentOrders)
{
    await SendEmailAsync(order);
}
```

EF Core compiles the authorization filter and your business filters into a single SQL query with the TVF `CROSS APPLY` baked in. No pagination needed, no specification — just a composable LINQ expression that makes any query authorization-aware.

### Access Tracing

Get a detailed diagnostic trace of any access decision:

```csharp
var trace = await authService.TraceResourceAccessAsync(
    principalId, resourceId, "PROJECT_EDIT");

// trace.AccessGranted
// trace.PathNodes — each ancestor with grants found
// trace.GrantsUsed — which grants contributed
// trace.DecisionSummary — human-readable explanation
```

## Architecture

```
src/Sqlzibar/
├── Configuration/       # SqlzibarOptions, SqlzibarModelConfiguration
├── Dashboard/           # Middleware, endpoints, embedded wwwroot/
├── Extensions/          # AddSqlzibar, UseSqlzibarAsync, ApplySqlzibarModel
├── Interfaces/          # ISqlzibarDbContext, ISqlzibarAuthService, etc.
├── Models/              # All entity models (14 files)
├── Schema/              # Versioned SQL scripts (001_Initial.sql, etc.)
├── Services/            # AuthService, SubjectService, SeedService, SchemaInitializer
└── Specifications/      # PagedSpecification, SortablePagedSpecification, PagedSpecBuilder
```

## Requirements

- .NET 9.0
- SQL Server (for TVF support)
- EF Core 9.0
- Docker (for integration tests via Aspire)

## Further Reading

| Document                                             | Description                                            |
| ---------------------------------------------------- | ------------------------------------------------------ |
| [Configuration & Dashboard](docs/CONFIGURATION.md)   | Full options, dashboard setup, schema versioning       |
| [API Reference](docs/API_REFERENCE.md)               | Complete interface signatures for all services         |
| [Principal Management](docs/PRINCIPAL_MANAGEMENT.md) | Users, groups, data access, linking entities           |
| [Performance](docs/PERFORMANCE.md)                   | Benchmark results at 1.2M entities                     |
| [Testing](docs/TESTING.md)                           | Unit and integration test setup                        |
| [Example App](docs/EXAMPLE_APP.md)                   | Retail API example with seeded data and demo scenarios |
| [Modeling Guide](docs/MODELING_GUIDE.md)             | LLM prompt and personal finance app example            |
| [Schema Changes](docs/SCHEMA_CHANGES.md)             | For library maintainers adding schema versions         |

## License

MIT
