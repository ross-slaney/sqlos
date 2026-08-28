# SqlOS

**A complete auth stack for .NET B2B SaaS — inside your app, on the SQL Server you already run. No identity service to deploy.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/SqlOS)](https://www.nuget.org/packages/SqlOS)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple)](https://dotnet.microsoft.com)

One NuGet package adds authentication *and* authorization to an ASP.NET Core app: an OAuth 2.0 / OpenID Connect server, a branded login and signup UI, organizations and sessions, hierarchical role-based access control that filters your EF Core queries in SQL, and an admin dashboard. Everything runs in your process and stores in your database — nothing extra to stand up, pay for, or keep in sync with your data.

```csharp
builder.AddSqlOS<AppDbContext>(
    db => db.UseSqlServer(connectionString),
    options => options.UseSingleApplication("Acme", app =>
    {
        app.Origin = "http://localhost:5050";
        app.Audience = "http://localhost:5050/api";
    }));
```

That's a working auth server with hosted login at `/sqlos/auth/login` and a dashboard at `/sqlos`.

## What's in the box

### 1. An auth server

A full OAuth 2.0 authorization server and OpenID Connect Provider mounted inside your app: authorization code + PKCE, refresh tokens, device flow, client credentials, discovery, ID tokens, UserInfo, and a consent screen for third-party clients — verified against the OpenID Foundation conformance suite in CI.

Sign-in methods are configuration, not projects: passwords, email OTP, magic links, SMS, social login (Google, Microsoft, GitHub, Apple, any OIDC provider), SAML enterprise SSO, SCIM directory sync, and TOTP MFA. B2B primitives — organizations, memberships, invitations, per-application access rules — are built in. Other apps can even use yours as their identity provider ([Sign in with X](https://sqlos.dev/docs/guides/sign-in-with-x)).

<p align="center">
  <img src="https://sqlos.dev/docs/dashboard-home.png" alt="SqlOS admin dashboard home showing Auth Server and Fine-Grained Auth counts" width="900" />
</p>

<p align="center">
  <img src="https://sqlos.dev/docs/guides-sign-in-with-x-consent.png" alt="SqlOS consent screen for Sign in with X, listing scopes by display name" width="560" />
</p>

→ [Auth server overview](https://sqlos.dev/docs/authserver/overview) · [OpenID Provider](https://sqlos.dev/docs/authserver/openid-provider) · [Organizations](https://sqlos.dev/docs/authserver/organizations)

### 2. AuthPage — hosted login UI

Login, signup, OTP entry, MFA, organization selection, and consent ship as hosted pages, ready on day one. Brand them with your name, logo, and colors — from code seeds, the Admin API, or the dashboard — and the same identity carries into the built-in OTP, invitation, and password-reset emails.

<p align="center">
  <img src="https://sqlos.dev/docs/guides-social-sign-in.png" alt="Hosted AuthPage with email continue plus GitHub and Microsoft social providers" width="560" />
</p>

→ [Brand hosted auth and email](https://sqlos.dev/docs/guides/auth-branding) · [Hosted vs. headless](https://sqlos.dev/docs/authserver/hosted-vs-headless)

### 3. SHRBAC — authorization inside your EF Core queries

SqlOS's hierarchical role-based access control models your resources as a tree (org → workspace → project), defines permissions and roles, and grants them to users, groups, service accounts, or agents. Point checks answer "can this user do X to this resource?", and — the part that changes how you write code — list queries get an authorization filter that runs **in SQL**, so users only ever receive rows they're allowed to see:

```csharp
var filter = await authorization.BuildFilterAsync<Project>(userId, "project.read");
var projects = await db.Projects.Where(filter).ToListAsync();
```

No sidecar, no policy service round-trips, no post-filtering in memory. The same grants shape product UI — a company admin and a store clerk hit the same endpoints and see different rows:

<table>
  <tr>
    <td width="50%" valign="top">
      <p><strong>Company Admin</strong> — five chains visible</p>
      <img src="https://sqlos.dev/docs/retail-app-admin-dashboard.png" alt="Retail app as Company Admin with five chains and multi-store inventory" />
    </td>
    <td width="50%" valign="top">
      <p><strong>Store Clerk</strong> — one store, filtered in SQL</p>
      <img src="https://sqlos.dev/docs/retail-app-clerk-dashboard.png" alt="Retail app as Store Clerk with zero chains and one store visible" />
    </td>
  </tr>
</table>

→ [Authorize EF Core queries](https://sqlos.dev/docs/quickstarts/ef-authorization) · [Model your FGA](https://sqlos.dev/docs/guides/model-fga) · [EF query filters](https://sqlos.dev/docs/guides/ef-query-filters)

### 4. Headless auth — bring your own UI

If the hosted pages don't fit your product, keep SqlOS as the protocol engine and draw every screen yourself. Your frontend talks to a typed state machine — login, signup, OTP, MFA, consent — while SqlOS still owns OAuth, PKCE, sessions, and tokens. Extra signup fields, A/B tests, and native-feeling popups all become your UI's decisions.

<p align="center">
  <img src="https://sqlos.dev/docs/guides-custom-login-ui.svg" alt="Product-owned login UI connected to the SqlOS headless authentication state machine" width="900" />
</p>

→ [Build your own login and signup UI](https://sqlos.dev/docs/guides/custom-login-ui) · [Headless auth reference](https://sqlos.dev/docs/authserver/headless-auth)

## See it running in 2 minutes

The Todo sample gives you a working login flow and authorized EF Core queries without touching your own code:

```bash
dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
```

Then open `http://localhost:5090/`. The Aspire AppHost starts SQL Server, the Todo API with SqlOS at `http://localhost:5080`, and a Razor Pages client at `http://localhost:5090`.

<p align="center">
  <img src="https://sqlos.dev/docs/guides-password-login.png" alt="Hosted AuthPage password step from the SqlOS Todo sample" width="560" />
</p>

[Todo sample walkthrough](https://sqlos.dev/docs/quickstarts/run-todo) · [All documentation](https://sqlos.dev/docs)

## Add SqlOS to your app

You'll need **.NET 9**, **EF Core 9**, and a **SQL Server** database your application can reach.

```bash
dotnet add package SqlOS --version 4.0.0
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

[Full add-to-app quickstart](https://sqlos.dev/docs/quickstarts/add-to-app)

## Guides

Everything is documented as a task, not a feature tour. Start with whichever matches your next milestone:

| I want to… | Guide |
| --- | --- |
| Protect an API with access tokens | [Protect an API](https://sqlos.dev/docs/quickstarts/protect-api) |
| Return only the rows a user may see | [Authorize EF Core queries](https://sqlos.dev/docs/quickstarts/ef-authorization) |
| Add native ASP.NET Core password login | [Password login](https://sqlos.dev/docs/guides/password-login) |
| Add Google/Microsoft/GitHub sign-in | [Social OIDC login](https://sqlos.dev/docs/guides/social-oidc) |
| Sell to enterprises that require SSO | [SAML SSO](https://sqlos.dev/docs/guides/saml-sso) · [SCIM directory sync](https://sqlos.dev/docs/guides/scim-directory-sync) |
| Let other apps sign in with my app's accounts | [Sign in with X](https://sqlos.dev/docs/guides/sign-in-with-x) |
| Build my own login UI | [Custom login UI](https://sqlos.dev/docs/guides/custom-login-ui) |
| Authenticate CLIs, background jobs, or MCP clients | [Terminal auth](https://sqlos.dev/docs/guides/terminal-auth) · [Service accounts](https://sqlos.dev/docs/guides/service-account-jobs) · [MCP OAuth](https://sqlos.dev/docs/guides/mcp-oauth) |
| Go to production safely | [Production readiness](https://sqlos.dev/docs/guides/production-readiness) |

More at the [guides index](https://sqlos.dev/docs/guides/index), plus the [SDK reference](https://sqlos.dev/docs/reference/sdk-reference) and [HTTP API reference](https://sqlos.dev/docs/reference/api-reference).

Every administrative capability works through three equivalent control planes — typed code seeds, authenticated admin APIs, and the dashboard — sharing the same validation, tenancy, secret handling, and audit behavior. Configuration can live in source control, in automation, or with operators, without losing anything.

## Contributing

```bash
dotnet build SqlOS.sln
./scripts/unit-tests.sh
./scripts/integration-tests.sh
./scripts/docs-check.sh
```

SqlOS is MIT licensed. Issues and pull requests are welcome.
