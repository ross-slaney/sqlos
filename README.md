# SqlOS

**Auth for .NET B2B SaaS that lives in your app and your SQL Server database — no separate identity service to deploy.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/SqlOS)](https://www.nuget.org/packages/SqlOS)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple)](https://dotnet.microsoft.com)

SqlOS adds a full OAuth server, branded hosted login, organizations, sessions, and an admin dashboard to your ASP.NET Core application with one NuGet package. It runs inside your process and stores everything in the SQL Server database you already have, so there's no extra service to stand up, pay for, or keep in sync with your data.

```csharp
builder.AddSqlOS<AppDbContext>(
    db => db.UseSqlServer(connectionString),
    options => options.UseSingleApplication("Acme", app =>
    {
        app.Origin = "http://localhost:5050";
        app.Audience = "http://localhost:5050/api";
    }));
```

Start small with one application and hosted login, then turn on more as your product grows:

- **Sign-in options** — passwords, Email OTP, magic links, social login (Google, Microsoft, GitHub, Apple, custom OIDC), SAML SSO, and TOTP MFA
- **B2B building blocks** — organizations, memberships, invitations, sessions, refresh tokens, and per-application access rules
- **Authorization in your queries** — hierarchical roles and grants with filters that run inside your EF Core queries, not in a sidecar
- **Operations** — audit logs and an embedded admin dashboard at `/sqlos`
- **Integrations** — Google Calendar and Microsoft 365 calendar connections
- **Client flexibility** — OAuth flows for web apps, native apps, CLIs, and MCP clients

None of these are prerequisites — the single-application path works on its own.

## See it running in 2 minutes

The Todo sample gives you a working login flow and authorized EF Core queries without touching your own code:

```bash
dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
```

Then open `http://localhost:5090/`. The Aspire AppHost starts SQL Server, the Todo API with SqlOS at `http://localhost:5080`, and a Razor Pages client at `http://localhost:5090`.

[Todo sample walkthrough](https://sqlos.dev/docs/quickstarts/run-todo) · [All documentation](https://sqlos.dev/docs)

## Add SqlOS to your app

You'll need **.NET 9**, **EF Core 9**, and a **SQL Server** database your application can reach.

```bash
dotnet add package SqlOS --version 3.24.1
```

Derive your `DbContext` from `SqlOSDbContext<TContext>` so SqlOS can register its EF Core model, then declare your application:

```csharp
using Microsoft.EntityFrameworkCore;
using SqlOS;
using SqlOS.Configuration;
using SqlOS.Extensions;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "Connection string 'DefaultConnection' was not configured.");

const string appOrigin = "http://localhost:5050";
var dashboardPassword = builder.Configuration["SqlOS:Dashboard:Password"]
    ?? throw new InvalidOperationException(
        "Configure SqlOS:Dashboard:Password with user secrets or your secret store.");

builder.AddSqlOS<AppDbContext>(
    db => db.UseSqlServer(connectionString),
    options =>
    {
        options.UseSingleApplication("Acme", app =>
        {
            app.Origin = appOrigin;
            app.Audience = $"{appOrigin}/api";
        });

        options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
        options.Dashboard.Password = dashboardPassword;
    });

var app = builder.Build();

app.MapSqlOS();
app.MapGet("/", () => "SqlOS is running");

app.Run();

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : SqlOSDbContext<AppDbContext>(options)
{
}
```

Run it on the origin you declared:

```bash
dotnet run --urls http://localhost:5050
```

And you have:

| What | Where |
| --- | --- |
| Admin dashboard | `http://localhost:5050/sqlos` |
| Hosted login | `http://localhost:5050/sqlos/auth/login` |
| OAuth metadata | `http://localhost:5050/sqlos/auth/.well-known/oauth-authorization-server` |

SqlOS creates and upgrades its own tables at startup — your EF migrations keep owning only your application's tables. Signing-key protection is configured automatically.

A couple of notes worth knowing up front:

- `AddSqlOS` doesn't auto-bind the `SqlOS` configuration section — read secrets from `builder.Configuration` and assign them in the options callback, as shown above.
- If NuGet doesn't list 3.24.1 yet, run the repository examples from source rather than pairing these docs with an older package.

[Full add-to-app quickstart](https://sqlos.dev/docs/quickstarts/add-to-app)

## Protect an API

`RequireSqlOSAccessToken` validates a SqlOS access token for an exact audience and populates `HttpContext.User`:

```csharp
using SqlOS.AuthServer.Extensions;

var api = app.MapGroup("/api")
    .RequireSqlOSAccessToken("http://localhost:5050/api");

api.MapGet("/me", (HttpContext http) =>
{
    var token = http.GetSqlOSValidatedToken();
    return token is null
        ? Results.Unauthorized()
        : Results.Ok(new { token.UserId, token.OrganizationId, token.ClientId, token.Audience });
});
```

[Protect an API](https://sqlos.dev/docs/quickstarts/protect-api) · [Authorize EF Core queries](https://sqlos.dev/docs/quickstarts/ef-authorization)

## Configure it your way

Every administrative capability in SqlOS works through three equivalent paths, so you can pick what fits your team without losing anything:

- **Code** — strongly typed options and seeds, reconciled deterministically at startup, so configuration lives in source control
- **API** — authenticated admin APIs and SDKs for automation: create connections, rotate credentials, preview policies, trigger syncs
- **Dashboard** — complete operator workflows for setup, testing, troubleshooting, rotation, and audit history

All three share the same validation, authorization, tenancy, secret handling, and audit behavior. Code-owned records stay visible in the dashboard (clearly marked as source-controlled), and dashboard changes never silently clobber what code owns.

## Documentation

- [Documentation home](https://sqlos.dev/docs)
- [Getting started](https://sqlos.dev/docs/getting-started)
- [Quickstarts](https://sqlos.dev/docs/quickstarts/run-todo)
- [Guides](https://sqlos.dev/docs/guides/index)
- [SDK reference](https://sqlos.dev/docs/reference/sdk-reference)
- [HTTP API reference](https://sqlos.dev/docs/reference/api-reference)

## Contributing

```bash
dotnet build SqlOS.sln
./scripts/unit-tests.sh
./scripts/integration-tests.sh
./scripts/docs-check.sh
```

SqlOS is [MIT licensed](LICENSE). Issues and pull requests are welcome.
