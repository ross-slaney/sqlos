# Configuration

Wire SqlOS in three places: host registration, EF model, then `app.MapSqlOS()` after `Build()`.

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

### Auth page: hosted vs headless

The DB stores **hosted** or **headless**. `/authorize` uses it.

- **Headless:** `UseHeadlessAuthPage` + `BuildUiUrl`. In `SeedAuthPage` use `page.PresentationMode = "headless"`. Or omit it; seed infers headless when `BuildUiUrl` exists.
- **Hosted:** use hosted pages. You can set `page.PresentationMode = "hosted"` to be explicit.

See `web/content/docs/authserver/headless-auth.mdx` in this repo (published as `/docs/guides/authserver/headless-auth`).

### Optional: Dashboard Password Login

Use a dashboard-only password when you are not wiring your app’s own auth into the SqlOS admin UI.

Read the secret from config (appsettings, user secrets, env). Pass it into `AddSqlOS`:

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

Bootstrap and dashboard middleware start with the host.

Need SqlOS tables **before** your EF migrations? Call `SqlOSBootstrapper.InitializeAsync()` once first. Copy the pattern from the example API.

## Schema Ownership

SqlOS ships SQL scripts. It updates its own tables.

- Your EF migrations do **not** own SqlOS tables.
- The host runs SqlOS bootstrap on startup.
- Auth and FGA share the same bootstrap.
- Seed data you configure is reapplied on each boot for the rows SqlOS owns.

## Dashboard Paths

- shared dashboard shell: `/sqlos`
- auth admin APIs and UI: `/sqlos/admin/auth`
- FGA admin UI: `/sqlos/admin/fga`
- auth runtime endpoints: `/sqlos/auth`
