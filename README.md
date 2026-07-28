# SqlOS

**Embedded authentication and SQL-backed authorization for .NET B2B SaaS.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/SqlOS)](https://www.nuget.org/packages/SqlOS)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple)](https://dotnet.microsoft.com)

SqlOS 3.24.1 provides a hosted OAuth server, branded login, organizations, sessions, and optional fine-grained authorization to an ASP.NET Core application. It runs in your process and stores its data in your SQL Server database, so you do not need a separate identity or authorization service to get started.

Start with one application and hosted login. Add SAML SSO, social login, Email OTP, audit logs, calendar connections, or hierarchical authorization when your product needs them.

## One capability, three control planes

SqlOS keeps infrastructure optional without making operations opaque. Administrative capabilities are designed as one underlying implementation exposed in three ways:

### Code-first configuration

Use strongly typed options and seeds when configuration should be reproducible in source control. Startup reconciliation is deterministic and idempotent: code-owned records can be kept aligned with code without silently overwriting records owned by dashboard operators.

### Programmable administration

Use authenticated services/SDKs and admin APIs to automate work such as creating connections, rotating credentials, previewing policies, triggering synchronization, and inspecting outcomes. These operations share the same validation, authorization, tenancy, secret handling, and audit behavior as every other control plane.

### Dashboard workflow

Use the embedded dashboard for complete operator workflows: setup, validation, testing, troubleshooting, rotation, disablement, audit history, ownership visibility, and copy-ready integration values. Code-owned records remain visible and testable while clearly identifying fields controlled by source code.

The three paths do not implement separate policy. When a capability supports all three, code-first, API-created, and dashboard-created configuration must produce equivalent runtime behavior. Invisible security defaults remain automatic; they do not gain unnecessary switches merely to appear in the dashboard.

## Choose your starting point

### See SqlOS work first

Run the Todo sample if you want a working login and authorized EF Core queries before changing your application:

```bash
dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
```

Open `http://localhost:5090/`. The Aspire AppHost starts SQL Server, the Todo API/SqlOS host at `http://localhost:5080`, and the Razor Pages client at `http://localhost:5090`.

[Run the Todo sample](https://sqlos.dev/docs/quickstarts/run-todo) · [Browse all documentation](https://sqlos.dev/docs)

### Add SqlOS to an application

Requirements:

- .NET 9 (`net9.0`)
- EF Core 9
- SQL Server with an existing database your application can access

Install the package:

```bash
dotnet add package SqlOS --version 3.24.1
```

> The `main` branch is staged for the 3.24.1 package contract. If NuGet does not list 3.24.1 yet, run the repository examples from source or wait for the package release; do not pair these source docs with an older package.

Use `SqlOSDbContext<TContext>` so SqlOS can register its EF Core model, then declare one application with `UseSingleApplication`:

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

The configuration lookup in this example is deliberate: `AddSqlOS` does not automatically bind the `SqlOS` configuration section. Read secrets from `builder.Configuration` and assign them inside the options callback. SqlOS configures its signing-key protection automatically; production replicas only need to share durable ASP.NET Core Data Protection storage during their readiness review.

Run the host on the origin used above:

```bash
dotnet run --urls http://localhost:5050
```

Then verify:

- dashboard: `http://localhost:5050/sqlos`
- OAuth metadata: `http://localhost:5050/sqlos/auth/.well-known/oauth-authorization-server`
- hosted login: `http://localhost:5050/sqlos/auth/login`

SqlOS initializes and upgrades its own tables when the host starts. Your EF migrations continue to own only your application tables.

[Complete add-to-app quickstart](https://sqlos.dev/docs/quickstarts/add-to-app)

## Protect an API

`RequireSqlOSAccessToken` validates a SqlOS access token for an exact audience and populates `HttpContext.User`:

```csharp
var api = app.MapGroup("/api")
    .RequireSqlOSAccessToken("http://localhost:5050/api");

api.MapGet("/me", (HttpContext http) =>
{
    var token = http.GetSqlOSValidatedToken();
    return token is null
        ? Results.Unauthorized()
        : Results.Ok(new
        {
            token.UserId,
            token.OrganizationId,
            token.ClientId,
            token.Audience
        });
});
```

Add `using SqlOS.AuthServer.Extensions;` for `GetSqlOSValidatedToken`.

[Protect an API](https://sqlos.dev/docs/quickstarts/protect-api) · [Authorize EF Core queries](https://sqlos.dev/docs/quickstarts/ef-authorization)

## What you can add next

- Hosted or headless login, password credentials, Email OTP, social login, and SAML SSO
- Organizations, memberships, invitations, sessions, refresh tokens, and application access rules
- Hierarchical roles and grants with authorization filters that remain inside EF Core queries
- Audit logs and an embedded admin dashboard
- Google Calendar and Microsoft 365 calendar connections
- OAuth client modes for owned web apps, native apps, CLIs, and portable MCP clients

These capabilities are available from the same package, but they are not prerequisites for the one-application path.

## Documentation

- [Documentation home](https://sqlos.dev/docs)
- [Getting started](https://sqlos.dev/docs/getting-started)
- [Quickstarts](https://sqlos.dev/docs/quickstarts/run-todo)
- [Guides](https://sqlos.dev/docs/guides/index)
- [SDK reference](https://sqlos.dev/docs/reference/sdk-reference)
- [HTTP API reference](https://sqlos.dev/docs/reference/api-reference)

## Build and test the repository

```bash
dotnet build SqlOS.sln
./scripts/unit-tests.sh
./scripts/integration-tests.sh
./scripts/docs-check.sh
```

SqlOS is MIT licensed. Issues and contributions are welcome in this repository.
