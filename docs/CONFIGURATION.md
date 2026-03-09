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
