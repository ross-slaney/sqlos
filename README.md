# SqlOS

**Embedded auth server and fine-grained authorization for .NET — one NuGet package, zero external services.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/SqlOS)](https://www.nuget.org/packages/SqlOS)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple)](https://dotnet.microsoft.com)

SqlOS adds auth and fine-grained authorization to your .NET app. OAuth, login UI, orgs, SAML, OIDC, and FGA live in **your** SQL Server. An embedded admin UI ships with the package.

Think **WorkOS / AuthKit**, but **self-hosted** and **your database**.

## Why SqlOS?

| External auth services | SqlOS |
|---|---|
| Data lives on someone else's servers | Data lives in **your** SQL Server |
| Per-MAU pricing that scales against you | **MIT-licensed**, no usage fees |
| Another vendor dependency to manage | **Single NuGet package**, ships with your app |
| Limited customization of login flows | **Full control** — branded AuthPage, custom OIDC, SAML |

## Features

### AuthServer

- **OAuth 2.0 with PKCE** — `/authorize`, `/token`, `/.well-known/oauth-authorization-server`, `/.well-known/jwks.json`
- **Branded AuthPage** — hosted `/login`, `/signup`, and `/logged-out` with customizable branding
- **Organizations & Users** — multi-tenant user management with memberships and roles
- **Password Credentials** — secure local authentication with session management
- **Social Login** — Google, Microsoft, Apple, and any custom OIDC provider
- **SAML SSO** — enterprise single sign-on with home realm discovery by email domain
- **Sessions & Refresh Tokens** — full lifecycle management with revocation
- **Signing Key Rotation** — automatic RS256 key rotation with configurable intervals
- **Audit Logging** — track authentication events across your system

### FGA (Fine-Grained Authorization)

- **Hierarchical Resource Authorization** — define resource types, permissions, and roles
- **Access Grants** — assign permissions to users, user groups, and service accounts
- **EF Core Query Filters** — filter authorized resources directly in LINQ queries
- **Access Tester** — verify authorization decisions through the dashboard

### Embedded Admin Dashboard

- **Auth Admin** — manage organizations, users, clients, OIDC/SAML connections, security settings, sessions, and audit events
- **FGA Admin** — manage resources, grants, roles, permissions, and test access decisions
- **Password-Protected** — optional password auth mode for production deployments

## Quick Start

1. **Add the package**

   ```bash
   dotnet add package SqlOS
   ```

2. **Use SQL Server for your EF `DbContext`**  
   SqlOS uses the same database as your context. Point EF at SQL Server like any other app.

3. **Wire your `DbContext`**  
   Add the two SqlOS interfaces. Add the FGA `IsResourceAccessible` query. Call `UseSqlOS` in `OnModelCreating`:

   ```csharp
   public sealed class AppDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
   {
       public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
           string subjectId,
           string permissionKey)
           => FromExpression(() => IsResourceAccessible(subjectId, permissionKey));

       protected override void OnModelCreating(ModelBuilder modelBuilder)
       {
           base.OnModelCreating(modelBuilder);
           modelBuilder.UseSqlOS(GetType());
       }
   }
   ```

4. **Register SqlOS on the host**

   ```csharp
   builder.AddSqlOS<AppDbContext>(options =>
   {
       options.UseFGA();
       options.UseAuthServer();
   });
   ```

5. **Map routes after `Build()`**

   ```csharp
   var app = builder.Build();
   app.MapSqlOS();
   ```

On startup, SqlOS updates its own schema. Default URLs: admin at `/sqlos`, OAuth at `/sqlos/auth`. Change the prefix with `DashboardBasePath` if you need to.

## Dashboard Access

Protect the dashboard in production with a password:

```csharp
options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
options.Dashboard.Password = builder.Configuration["SqlOS:Dashboard:Password"];
```

Or via environment variables:

```bash
SqlOS__Dashboard__AuthMode=Password
SqlOS__Dashboard__Password=<strong-password>
```

## Example App

The repo includes a full working example powered by .NET Aspire:

```bash
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

That starts SQL Server, the sample API, and a Next.js app. You get password login, OIDC, SAML, sessions, and FGA in the demo.

| | URL |
|---|---|
| Dashboard | `http://localhost:5062/sqlos/` |
| Auth Admin | `http://localhost:5062/sqlos/admin/auth/` |
| FGA Admin | `http://localhost:5062/sqlos/admin/fga/` |
| Web App | `http://localhost:3010/` |

## Requirements

- .NET 9.0+
- SQL Server (any edition, including LocalDB)
- EF Core 9.0+

## Testing

```bash
# Unit tests
dotnet test tests/SqlOS.Tests/SqlOS.Tests.csproj

# Integration tests (requires SQL Server)
dotnet test tests/SqlOS.IntegrationTests/SqlOS.IntegrationTests.csproj

# Full suite
dotnet test SqlOS.sln
```

## Repo Layout

```
src/SqlOS                                # The library
tests/SqlOS.Tests                        # Unit tests
tests/SqlOS.IntegrationTests             # Integration tests
tests/SqlOS.Benchmarks                   # Performance benchmarks
examples/SqlOS.Example.Api               # ASP.NET API example
examples/SqlOS.Example.Web               # Next.js frontend example
examples/SqlOS.Example.AppHost           # Aspire orchestration
```

## Documentation

- [Configuration](docs/CONFIGURATION.md) — service registration, EF integration, dashboard setup
- [Auth Page](docs/AUTH_PAGE.md) — hosted OAuth endpoints and branded UI
- [OIDC Auth](docs/OIDC_AUTH.md) — OpenID Connect provider support
- [Google OIDC](docs/GOOGLE_OIDC.md) · [Microsoft OIDC](docs/MICROSOFT_OIDC.md) · [Apple OIDC](docs/APPLE_OIDC.md) · [Custom OIDC](docs/CUSTOM_OIDC.md)
- [Entra SSO Testing](docs/ENTRA_SSO.md) — SAML SSO with Microsoft Entra
- [Example App](docs/EXAMPLE_APP.md) — running the demo stack
- [Testing](docs/TESTING.md) — test structure and conventions
- [Releasing](docs/RELEASE_VERSION.md) — versioning and release process

## License

MIT
