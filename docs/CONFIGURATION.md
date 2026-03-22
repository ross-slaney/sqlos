# Configuration

Wire SqlOS in three places:

1. host registration
2. EF model registration
3. `app.MapSqlOS()` after `Build()`

## Service registration

Start with the smallest useful hosted setup for an owned app:

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.Issuer = "https://app.example.com/sqlos/auth";
    options.AuthServer.PublicOrigin = "https://app.example.com";

    options.AuthServer.SeedAuthPage(page =>
    {
        page.PageTitle = "Sign in";
        page.PageSubtitle = "Secure your owned app first. Add portable clients later.";
    });

    options.AuthServer.SeedOwnedWebApp(
        "web",
        "Main Web App",
        "https://app.example.com/auth/callback");
});
```

If you use FGA too, keep seeding the auth and FGA model in the same call:

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.Fga.Seed(seed =>
    {
        seed.ResourceType("workspace", "Workspace");
        seed.Permission("perm_workspace_view", "workspace.view", "View workspace", "workspace");
        seed.Role("role_workspace_admin", "workspace_admin", "Workspace Admin");
        seed.RolePermission("workspace_admin", "workspace.view");
    });
});
```

## Client onboarding modes

Most teams only need the first mode on day one:

- **Owned app**: seeded or dashboard-created client, hosted or headless UI
- **Portable MCP client**: `CIMD` for public-client interoperability
- **Compatibility client**: optional `DCR` when a real client still needs runtime registration

### Portable MCP clients

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.EnablePortableMcpClients(registration =>
    {
        registration.Cimd.TrustedHosts.Add("clients.example.com");
    });
});
```

### Compatibility clients

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.EnableChatGptCompatibility(dcr =>
    {
        dcr.MaxRegistrationsPerWindow = 25;
    });
});
```

### Resource indicators

Resource indicators are on by default for the portable and compatibility helper methods.

Use them when you need tokens bound to a protected resource instead of the client audience fallback:

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.ResourceIndicators.Enabled = true;
});
```

## Auth page: hosted vs headless

`/sqlos/auth/authorize` uses one rule:

- **Headless**: `BuildUiUrl` exists
- **Hosted**: `BuildUiUrl` does not exist

See:

- repo site source: `web/content/docs/authserver/headless-auth.mdx`
- published guide: `/docs/guides/authserver/headless-auth`

## Optional: dashboard password login

Use a dashboard-only password when you are not wiring your app's own auth into the SqlOS admin UI.

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

Need SqlOS tables **before** your EF migrations? Call `SqlOSBootstrapper.InitializeAsync()` once first. The example API and Todo sample both show that pattern.

## Schema ownership

SqlOS ships SQL scripts and updates its own tables.

- your EF migrations do **not** own SqlOS tables
- the host runs SqlOS bootstrap on startup
- auth and FGA share the same bootstrap
- seed data you configure is reapplied on each boot for the rows SqlOS owns

## Dashboard paths

- shared dashboard shell: `/sqlos`
- auth admin APIs and UI: `/sqlos/admin/auth`
- FGA admin UI: `/sqlos/admin/fga`
- auth runtime endpoints: `/sqlos/auth`
