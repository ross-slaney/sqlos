# Configuration

`SqlOS` is configured from one host registration call, one EF model call, and `app.MapSqlOS()` after `Build()`.

## Service registration

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.UseFGA(fga =>
    {
        fga.DashboardPathPrefix = "/sqlos/admin/fga";
        fga.Seed(seed =>
        {
            seed.ResourceType("workspace", "Workspace");
            seed.Permission("perm_workspace_view", "workspace.view", "View workspace", "workspace");
            seed.Role("role_workspace_admin", "workspace_admin", "Workspace Admin");
            seed.RolePermission("workspace_admin", "workspace.view");
        });
    });

    options.UseAuthServer(auth =>
    {
        auth.Issuer = "https://localhost/sqlos/auth";
        auth.SeedAuthPage(page =>
        {
            page.PageTitle = "Sign in";
            page.PageSubtitle = "Secure your app-owned AI and MCP experience.";
        });
        auth.SeedBrowserClient("web", "Main Web App", "https://app.example.com/auth/callback");
    });
});
```

### Optional: Dashboard Password Login

Use this when you want a standalone dashboard login without integrating host-app auth.
Set the password from configuration (appsettings, user secrets, env vars), then pass it
through `AddSqlOS(...)`:

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
    options.Dashboard.Password = builder.Configuration["SqlOS:Dashboard:Password"]
        ?? throw new InvalidOperationException("SqlOS dashboard password is not configured.");
});
```

Optional session lifetime override:

```csharp
var sessionMinutes = builder.Configuration.GetValue<int?>("SqlOS:Dashboard:SessionLifetimeMinutes");
if (sessionMinutes is > 0)
{
    options.Dashboard.SessionLifetime = TimeSpan.FromMinutes(sessionMinutes.Value);
}
```

## EF model registration

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.UseSqlOS(GetType());
}
```

## Map routes

```csharp
var app = builder.Build();
app.MapSqlOS();
```

Bootstrap and dashboard middleware run automatically from host startup. If you need SqlOS tables to exist before your own EF migrations run, call `SqlOSBootstrapper.InitializeAsync()` once before migrating (see the example API).

## Schema Ownership

`SqlOS` manages its own schema through embedded SQL scripts.

That means:
- consumer EF migrations do not own `SqlOS` tables
- host startup applies pending library schema changes (via SqlOS hosted bootstrap)
- both `Fga` and `AuthServer` use the same library-managed bootstrap model
- startup seeds are reapplied on boot for the records they manage

## Dashboard Paths

- shared dashboard shell: `/sqlos`
- auth admin APIs and UI: `/sqlos/admin/auth`
- FGA admin UI: `/sqlos/admin/fga`
- auth runtime endpoints: `/sqlos/auth`
