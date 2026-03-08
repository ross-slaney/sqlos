# Configuration

## Options

```csharp
builder.Services.AddSqlzibar<AppDbContext>(options =>
{
    // Database schema (default: "dbo")
    options.Schema = "auth";

    // Root resource configuration
    options.RootResourceId = "my_root";
    options.RootResourceName = "Application Root";

    // Auto-initialization (default: true for both)
    options.InitializeFunctions = true;  // Create/update TVF on startup
    options.SeedCoreData = true;         // Seed subject types + root resource

    // Dashboard path (default: "/sqlzibar")
    options.DashboardPathPrefix = "/admin/auth";

    // Custom table names
    options.TableNames.Resources = "AuthResources";
    options.TableNames.Grants = "AuthGrants";
    options.TableNames.Roles = "AuthRoles";
    options.TableNames.Permissions = "AuthPermissions";
    options.TableNames.RolePermissions = "AuthRolePermissions";
    options.TableNames.Principals = "AuthPrincipals";
    options.TableNames.PrincipalTypes = "AuthPrincipalTypes";
    options.TableNames.UserGroups = "AuthUserGroups";
    options.TableNames.UserGroupMemberships = "AuthUserGroupMemberships";
    options.TableNames.ResourceTypes = "AuthResourceTypes";
    options.TableNames.ServiceAccounts = "AuthServiceAccounts";
});
```

## Dashboard

Sqlzibar includes a built-in web dashboard served as embedded static files. Mount it with:

```csharp
app.UseSqlzibarDashboard("/sqlzibar");
```

**By default the dashboard is only accessible in Development environments.** In production, return 404 unless you configure an authorization callback:

```csharp
app.UseSqlzibarDashboard("/sqlzibar", dashboard =>
{
    dashboard.AuthorizationCallback = async httpContext =>
    {
        // Your custom auth logic here
        return httpContext.User.IsInRole("Admin");
    };
});
```

The dashboard provides:

- **Resources** — Lazy-loading hierarchical tree view with paginated children
- **Subjects** — Tabbed by type (users, groups, service accounts), click into a subject to see detail page with info, group memberships, and paginated role grants
- **Grants** — Subject + role + resource with effective dates
- **Roles** — With expandable permission lists (click a role to see its permissions)
- **Permissions** — All permissions with resource type associations
- **Access Tester** — Interactive tool to trace access decisions
- **Stats** — Summary counts

All table views support pagination and search.

### Dashboard API Endpoints

| Endpoint                                                            | Description                                                     |
| ------------------------------------------------------------------- | --------------------------------------------------------------- |
| `GET /sqlzibar/api/stats`                                           | Summary counts                                                  |
| `GET /sqlzibar/api/resources/tree?maxDepth=2`                       | Resource tree (breadth-first, configurable depth)               |
| `GET /sqlzibar/api/resources/{id}/children?page=1&pageSize=50`      | Paginated children of a resource                                |
| `GET /sqlzibar/api/principals?type=user&page=1&pageSize=25&search=` | Principals filtered by type, paginated                          |
| `GET /sqlzibar/api/principals/{id}`                                 | Principal detail with group memberships and members             |
| `GET /sqlzibar/api/principals/{id}/grants?page=1&pageSize=25`       | Paginated grants for a principal                                |
| `GET /sqlzibar/api/grants?page=1&pageSize=25&search=`               | All grants with joined names, paginated                         |
| `GET /sqlzibar/api/roles?page=1&pageSize=25&search=`                | Roles with permission counts, paginated                         |
| `GET /sqlzibar/api/roles/{id}/permissions`                          | Permissions for a role                                          |
| `GET /sqlzibar/api/permissions?page=1&pageSize=25&search=`          | All permissions, paginated                                      |
| `POST /sqlzibar/api/trace`                                          | Access trace (body: `{subjectId, resourceId, permissionKey}`) |
| `GET /sqlzibar/api/schema/export`                                   | Export schema as YAML                                           |
| `POST /sqlzibar/api/schema/import`                                  | Import schema from YAML (body: raw YAML content)                |

## Schema Versioning

Sqlzibar creates and upgrades its own database tables automatically via `UseSqlzibarAsync()`. Consumers do **not** need to write EF migrations for Sqlzibar tables.

On startup, the schema initializer:

1. Checks the `SqlzibarSchema` version table
2. If the database is fresh, runs the initial schema creation script
3. If the version is behind, runs upgrade scripts in order
4. All scripts are idempotent (`IF NOT EXISTS`), so they are safe to run on existing databases

Sqlzibar tables are automatically excluded from EF migrations via `ExcludeFromMigrations()`. This means EF can still query Sqlzibar entities, but `dotnet ef migrations add` will not generate migration code for them. Sqlzibar always manages its own schema via raw SQL.

> **Important:** Because consumer tables often have foreign keys to Sqlzibar tables (e.g., an `Agency` referencing `SqlzibarResources`), `UseSqlzibarAsync()` must be called **before** `Database.Migrate()` in your startup so the referenced tables exist when the migration runs.

```csharp
// 1. Sqlzibar creates its own tables first
await app.UseSqlzibarAsync();

// 2. Then EF migrations create consumer tables (which may FK to Sqlzibar tables)
using var scope = app.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
db.Database.Migrate();
```

For library maintainers: see [SCHEMA_CHANGES.md](SCHEMA_CHANGES.md) for how to add new schema versions.
