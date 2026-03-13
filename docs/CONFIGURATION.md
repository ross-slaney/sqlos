# Configuration

`SqlOS` is configured from one root registration call and two module hooks.

## Service Registration

```csharp
builder.Services.AddSqlOS<AppDbContext>(options =>
{
    options.UseFGA(fga =>
    {
        fga.DashboardPathPrefix = "/sqlos/admin/fga";
    });

    options.UseAuthServer(auth =>
    {
        auth.BasePath = "/sqlos/auth";
        auth.Issuer = "https://localhost/sqlos/auth";
    });
});
```

### Optional: Dashboard Password Login

Use this when you want a standalone dashboard login without integrating host-app auth.
Set the password from configuration (appsettings, user secrets, env vars), then pass it
through `AddSqlOS(...)`:

```csharp
builder.Services.AddSqlOS<AppDbContext>(options =>
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

## EF Model Registration

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    modelBuilder.UseAuthServer();
    modelBuilder.UseFGA(GetType());
}
```

## Startup Bootstrap

```csharp
await app.UseSqlOSAsync();
app.MapAuthServer("/sqlos/auth");
app.UseSqlOSDashboard("/sqlos");
```

## Schema Ownership

`SqlOS` manages its own schema through embedded SQL scripts.

That means:
- consumer EF migrations do not own `SqlOS` tables
- `UseSqlOSAsync()` applies pending library schema changes at startup
- both `Fga` and `AuthServer` use the same library-managed bootstrap model

## Dashboard Paths

- shared dashboard shell: `/sqlos`
- auth admin APIs and UI: `/sqlos/admin/auth`
- FGA admin UI: `/sqlos/admin/fga`
- auth runtime endpoints: `/sqlos/auth`
