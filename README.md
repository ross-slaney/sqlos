# SqlOS

`SqlOS` is a single embedded runtime for application auth and fine-grained authorization in .NET.

It combines two modules in one library:
- `Fga`: hierarchical resource authorization for EF Core and SQL Server
- `AuthServer`: organizations, users, credentials, sessions, refresh tokens, and SAML SSO

The integration model stays Hangfire-style:
- library-owned SQL schema
- embedded versioned SQL scripts
- startup bootstrap
- EF model registration inside the consumer `DbContext`
- optional embedded dashboard

## Package Surface

```csharp
builder.Services.AddSqlOS<AppDbContext>(options =>
{
    options.UseFGA();
    options.UseAuthServer();
});

public sealed class AppDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string subjectId,
        string permissionKey)
        => FromExpression(() => IsResourceAccessible(subjectId, permissionKey));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseAuthServer();
        modelBuilder.UseFGA(GetType());
    }
}

var app = builder.Build();

await app.UseSqlOSAsync();
app.MapAuthServer("/sqlos/auth");
app.UseSqlOSDashboard("/sqlos");
```

## Repo Layout

```text
src/SqlOS
tests/SqlOS.Tests
tests/SqlOS.IntegrationTests
tests/SqlOS.IntegrationTests.AppHost
tests/SqlOS.Benchmarks
examples/SqlOS.Example.Api
examples/SqlOS.Example.Web
examples/SqlOS.Example.AppHost
examples/SqlOS.Example.Tests
examples/SqlOS.Example.IntegrationTests
```

## Shared Example

The example stack is now one Aspire-driven system:
- SQL Server
- ASP.NET API embedding `SqlOS`
- Next.js web app

Run it with:

```bash
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

Then use:
- shared dashboard shell: `http://localhost:5062/sqlos/`
- auth admin dashboard: `http://localhost:5062/sqlos/admin/auth/`
- FGA dashboard: `http://localhost:5062/sqlos/admin/fga/`
- example web app: `http://localhost:3001/`

## Run Tests

From the repo root:

Library tests:

```bash
dotnet test tests/SqlOS.Tests/SqlOS.Tests.csproj
```

Library integration tests:

```bash
dotnet test tests/SqlOS.IntegrationTests/SqlOS.IntegrationTests.csproj
```

Example app tests:

```bash
dotnet test examples/SqlOS.Example.Tests/SqlOS.Example.Tests.csproj
```

Example app integration tests:

```bash
dotnet test examples/SqlOS.Example.IntegrationTests/SqlOS.Example.IntegrationTests.csproj
```

Run the full suite:

```bash
dotnet test SqlOS.sln
```

## Docs

- [Configuration](docs/CONFIGURATION.md)
- [Entra SSO Testing](docs/ENTRA_SSO.md)
- [Example App](docs/EXAMPLE_APP.md)
- [Testing](docs/TESTING.md)
- [Release](docs/RELEASE_VERSION.md)
