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

- **OAuth 2.0 with PKCE** — `/sqlos/auth/authorize`, `/sqlos/auth/token`, metadata, and JWKS
- **Branded AuthPage** — hosted `/sqlos/auth/login`, `/sqlos/auth/signup`, and `/sqlos/auth/logged-out`
- **Client Onboarding Modes** — seeded/manual owned apps, `CIMD` discovered clients, and optional `DCR` compatibility clients
- **Resource Indicators** — bind `resource` end to end and mint audience-aware access tokens
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
       options.AuthServer.SeedOwnedWebApp(
           "todo-web",
           "Todo Web App",
           "https://app.example.com/auth/callback");
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

## Todo Sample

If your goal is:

> "I want SqlOS to work with hosted auth, resource metadata, and MCP-style public clients."

Start with:

```bash
dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
```

That sample stays intentionally narrow:

- hosted auth first
- headless follow-on
- protected-resource metadata
- audience-aware token validation
- local preregistration with `todo-local`
- public-client onboarding with `CIMD` and optional `DCR`

Read more:

- [Todo sample README](examples/SqlOS.Todo.Api/README.md)
- [Todo sample guide](web/content/docs/authserver/todo-sample.mdx)

## Example App

The repo includes a full working example powered by .NET Aspire:

```bash
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

That starts SQL Server, the sample API, the Todo sample, and the web frontends in one stack. Use it when you want breadth: password login, headless auth, OIDC, SAML, sessions, org workflows, FGA, and the hosted-first MCP-oriented Todo flow side by side.

If you build headless auth on a different browser origin than the SqlOS host, make those browser requests credentialed so SqlOS can persist and reuse its auth-page session cookie. Follow-up `/sqlos/auth/authorize?prompt=none` requests should then silently succeed when that session exists, or return `login_required` when it does not.

| | URL |
|---|---|
| Dashboard | `http://localhost:5062/sqlos/` |
| Auth Admin | `http://localhost:5062/sqlos/admin/auth/` |
| FGA Admin | `http://localhost:5062/sqlos/admin/fga/` |
| Web App | `http://localhost:3010/` |
| Todo App | `http://localhost:5080/` |

## Requirements

- .NET 9.0+
- SQL Server (any edition, including LocalDB)
- EF Core 9.0+

## Testing

```bash
dotnet build SqlOS.sln
./scripts/unit-tests.sh
./scripts/integration-tests.sh
./scripts/docs-check.sh
```

## Repo Layout

```
src/SqlOS                                # The library
tests/SqlOS.Tests                        # Unit tests
tests/SqlOS.IntegrationTests             # Integration tests
tests/SqlOS.Benchmarks                   # Performance benchmarks
examples/SqlOS.Todo.Api                  # Canonical hosted-first Todo sample
examples/SqlOS.Todo.AppHost              # Aspire runner for the Todo sample
examples/SqlOS.Todo.IntegrationTests     # Todo sample end-to-end tests
examples/SqlOS.Example.Api               # ASP.NET API example
examples/SqlOS.Example.Web               # Next.js frontend example
examples/SqlOS.Example.AppHost           # Aspire orchestration
```

## Documentation

- [Configuration](docs/CONFIGURATION.md) — service registration, EF integration, dashboard setup
- [Auth Page](docs/AUTH_PAGE.md) — hosted OAuth endpoints and branded UI
- [Todo Sample](examples/SqlOS.Todo.Api/README.md) — hosted auth, simple FGA, and MCP-oriented protected-resource flows
- [Client Registration DevEx](docs/CLIENT_REGISTRATION_DEVEX_2026.md) — product vocabulary and onboarding model
- [Preregistration vs CIMD vs DCR](web/content/docs/authserver/preregistration-vs-cimd-vs-dcr.mdx) — choose the right client onboarding path
- [Client ID Metadata Documents](web/content/docs/authserver/client-id-metadata-documents.mdx) — portable public clients with metadata URLs
- [Dynamic Client Registration](web/content/docs/authserver/dynamic-client-registration.mdx) — compatibility-mode runtime registration
- [MCP Resource Indicators and Audience](web/content/docs/authserver/mcp-resource-indicators-and-audience.mdx) — resource-bound tokens and audience validation
- [OIDC Auth](web/content/docs/authserver/oidc-auth.mdx) — OpenID Connect provider support
- [Google OIDC](docs/GOOGLE_OIDC.md) · [Microsoft OIDC](docs/MICROSOFT_OIDC.md) · [Apple OIDC](docs/APPLE_OIDC.md) · [Custom OIDC](docs/CUSTOM_OIDC.md)
- [Entra SSO Testing](docs/ENTRA_SSO.md) — SAML SSO with Microsoft Entra
- [Example App](docs/EXAMPLE_APP.md) — running the demo stack
- [Testing](docs/TESTING.md) — test structure and conventions
- [Releasing](docs/RELEASE_VERSION.md) — versioning and release process

## License

MIT
