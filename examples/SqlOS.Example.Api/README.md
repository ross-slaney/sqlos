# SqlOS example API

This ASP.NET Core application is the primary .NET reference for embedding SqlOS in an existing service. It combines the SqlOS identity, authorization, dashboard, and email data model with application-owned EF Core entities, then exposes protected workspace and retail APIs for the example clients.

Use this project to answer two questions:

1. What belongs in my .NET composition root?
2. Where does SqlOS stop and my application's domain code begin?

## Run the complete example

The full AppHost is the normal development path because it supplies SQL Server, the connection string, fixed issuer/callback URLs, and all web clients.

From the repository root:

```bash
npm ci --prefix examples/SqlOS.Example.Web
npm ci --prefix examples/SqlOS.Example.AngularWeb
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

Then open:

- application Swagger: `http://localhost:5062/swagger`
- SqlOS dashboard: `http://localhost:5062/sqlos`
- Next.js client: `http://localhost:3010`
- Angular client: `http://localhost:4200`

The local dashboard password in [`appsettings.json`](appsettings.json) is `your-strong-password`. It is a runnable sample default, not a deployment recommendation.

See the [AppHost guide](../SqlOS.Example.AppHost/README.md) for the complete resource map, optional provider configuration, persistence, and reset instructions.

## The .NET integration boundary

The important setup is intentionally concentrated in [`Program.cs`](Program.cs).

### 1. Use your application DbContext

[`ExampleAppDbContext`](Data/ExampleAppDbContext.cs) derives from `SqlOSDbContext<TContext>` and adds normal application sets:

```csharp
public sealed class ExampleAppDbContext
    : SqlOSDbContext<ExampleAppDbContext>
{
    public ExampleAppDbContext(
        DbContextOptions<ExampleAppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Chain> Chains => Set<Chain>();
    public DbSet<Location> Locations => Set<Location>();

    protected override void OnApplicationModelCreating(
        ModelBuilder modelBuilder)
    {
        // Configure application-owned entities here.
    }
}
```

This keeps one EF Core unit of work for SqlOS and application data. The [migration model snapshot](Migrations/ExampleAppDbContextModelSnapshot.cs) contains that combined model.

### 2. Register SqlOS with the same provider

The sample resolves the connection string, tells SqlOS to use SQL Server, and configures the product surfaces:

```csharp
builder.AddSqlOS<ExampleAppDbContext>(
    db => db.UseSqlServer(connectionString),
    options =>
    {
        options.DashboardBasePath = "/sqlos";
        options.AuthServer.Issuer =
            builder.Configuration["SqlOS:Issuer"]
            ?? "https://localhost/sqlos/auth";

        options.AuthServer.SeedBrowserClient(
            "example-angular",
            "Example Angular Client",
            "http://localhost:4200/auth/callback");

        options.Fga.Seed(seed =>
        {
            // Resource types, permissions, roles, and role mappings.
        });
    });
```

The actual project also configures hosted/headless AuthPage behavior, email and phone OTP, MFA, client registrations, optional Microsoft OIDC, auth email branding, workspace FGA, and a signup hook. Read the surrounding source rather than copying one concern blindly.

### 3. Map SqlOS before application endpoints

```csharp
var app = builder.Build();

app.MapSqlOS();

// Apply application migrations, middleware, and endpoints.
app.MapExampleEndpoints();

app.Run();
```

Together, `AddSqlOS()` and `MapSqlOS()` expose the configured SqlOS dashboard, OAuth authorization endpoints and metadata, social OIDC relying-party callbacks, hosted auth UI, and admin APIs. `AddSqlOS` installs the dashboard middleware; `MapSqlOS` maps the endpoint groups. Application endpoints remain explicit and independently testable.

## What the sample demonstrates

| Capability | Where to look | What to try |
| --- | --- | --- |
| Hosted AuthPage | `Program.cs` and `/sqlos/auth/authorize` | Sign in from the Next.js, Angular, or Expo client |
| Headless AuthPage | `Program.cs` headless callback and Next/Angular authorize pages | Choose the headless flow in a browser client |
| Password auth | AuthPage seed and `ExampleAuthEndpoints.cs` | Create a local user and session |
| Email OTP | Email/OTP configuration in `Program.cs` | Add ACS settings, then request an email code |
| Phone OTP | Twilio configuration in `Program.cs` | Enable phone OTP and add a Verify service |
| MFA | `ConfigureMfa` and `ExampleEndpoints.cs` | Enroll and verify TOTP from the Next.js account page |
| Microsoft social login | Conditional OIDC seed in `Program.cs` | Add Microsoft client credentials; the button appears only when configured |
| Enterprise SSO helpers | `ExampleAuthEndpoints.cs` and delegated portal link endpoint | Use the Next.js `/retail/sso` page |
| Fine-grained authorization | `ExampleFgaService.cs` and `FgaRetail` | Switch demo identities and compare visible retail resources |
| Application profile hook | `OnHeadlessSignupAsync` | Complete headless signup with the required referral source |
| Calendar connection APIs | `ExampleCalendarEndpoints.cs` | Inspect delegated connection/sync endpoints |
| Multiple client styles | Seeded browser clients | Compare .NET server, Next.js, Angular, and Expo callbacks |

Email OTP is offered by the seeded auth-page configuration, but real code delivery requires Azure Communication Services settings. Phone OTP is not enabled until valid Twilio settings are supplied.

## Registered local clients

| Client ID | Redirect URI | Consumer |
| --- | --- | --- |
| `example-web` | `http://localhost:3010/auth/callback` under AppHost; `http://localhost:3000/auth/callback` standalone | Next.js; the current Expo config also uses this client because it includes the custom-scheme callback |
| `example-angular` | `http://localhost:4200/auth/callback` | Angular |
| `example-expo` | `sqlos-expo://auth-callback` | Separately seeded native-client registration |

The `example-web` seed also includes `sqlos-expo://auth-callback`, which is why the current Expo sample's [`CLIENT_ID`](../SqlOS.Example.ExpoApp/services/config.ts) value works. If you switch Expo to `example-expo`, keep its callback registration aligned.

Redirect URIs are exact security boundaries. Changing a client port or callback path requires changing both the client and seed configuration.

The [ASP.NET Core Razor Pages client](../SqlOS.Example.AspNetCoreWeb/README.md) is registered by and requests the protected resource from the Todo API at `http://localhost:5080`. It runs in the full AppHost, but it is intentionally not a client of this broad example API.

## Application API map

| Surface | Purpose |
| --- | --- |
| `/swagger` and `/swagger/v1/swagger.json` | Application-focused OpenAPI UI and document |
| `/sqlos` | SqlOS dashboard |
| `/sqlos/auth/*` | OAuth authorization server metadata/endpoints, social OIDC relying-party callbacks, and hosted AuthPage |
| `/api/v1/auth/*` | Example-specific discovery, headless login/OTP, SSO/OIDC handoff, refresh, logout, and session facade |
| `/api/hello`, `/api/me`, `/api/profile` | Small protected identity examples |
| `/api/workspaces` | Organization-scoped workspace creation/listing with FGA |
| `/api/chains*`, `/api/locations*`, `/api/inventory*` | Retail domain APIs with FGA-filtered reads and permission-checked writes |
| `/api/calendar/*` | Calendar connection, event, sync, and token examples |
| `/api/demo/*` | Demo identities and identity switching for the sample UIs |

The Swagger document deliberately excludes SqlOS library/admin routes and example-only auth/helper routes. It presents the external application API a consumer should use, while the integration suite asserts that boundary.

## How protected requests are handled

[`ExampleBearerTokenMiddleware`](Middleware/ExampleBearerTokenMiddleware.cs) protects application `/api/*` routes and demonstrates three local sample identities:

- a SqlOS bearer access token;
- an `X-Api-Key` mapped to a seeded FGA service account;
- an `X-Agent-Token` mapped to a seeded FGA agent.

This middleware is example code, not a requirement to write custom authentication in every service. Because this sample hosts SqlOS and the API in one process, it can call `SqlOSAuthService` directly.

For a separate resource API, use ASP.NET Core's standard JWT bearer handler against the SqlOS issuer and metadata/JWKS endpoint. `Program.cs` includes a commented `AddJwtBearer` configuration showing issuer, audience, lifetime validation, and automatic signing-key refresh. Standard JWKS validation does not query the SqlOS session table, so a revoked token can remain accepted until its JWT expiry. When immediate logout/session revocation is required, add a session-aware check through a trusted SqlOS host or keep the resource API in the same process and use `ValidateAccessTokenAsync`/`RequireSqlOSAccessToken`.

## Run the API without Aspire

Standalone mode is useful when developing one client at its default port. You must provide a reachable SQL Server connection string; the application applies EF Core migrations at startup.

```bash
ConnectionStrings__DefaultConnection='Server=localhost,1433;Database=sqlos-example;User Id=sa;Password=<password>;TrustServerCertificate=True' \
dotnet run --project examples/SqlOS.Example.Api/SqlOS.Example.Api.csproj --launch-profile http
```

The HTTP launch profile listens on `http://localhost:5062`. In standalone configuration:

- the Next.js origin and callback default to `http://localhost:3000`;
- Angular remains `http://localhost:4200`;
- the issuer remains `http://localhost:5062/sqlos/auth`.

Use a SQL login with permission to create/update the sample schema. Do not point the sample at a production database.

To run the Next.js client alongside standalone API mode:

```bash
npm ci --prefix examples/SqlOS.Example.Web
NEXT_PUBLIC_API_URL=http://localhost:5062 \
NEXTAUTH_URL=http://localhost:3000 \
NEXTAUTH_SECRET=replace-for-local-development \
npm run dev --prefix examples/SqlOS.Example.Web
```

## Configuration reference

| Setting | Sample default | Purpose |
| --- | --- | --- |
| `ConnectionStrings:DefaultConnection` | empty | Required SQL Server connection |
| `SqlOS:Issuer` | `http://localhost:5062/sqlos/auth` in development | Token issuer and OAuth server origin |
| `SqlOS:HeadlessFrontendUrl` | `http://localhost:3000` | UI origin used by headless AuthPage |
| `SqlOS:EnableHeadlessAuthPage` | `true` | Enables the custom headless UI handoff |
| `SqlOS:Dashboard:AuthMode` | `Password` | Local dashboard authentication mode |
| `SqlOS:Dashboard:Password` | `your-strong-password` | Local dashboard password |
| `SqlOS:Dashboard:SessionLifetimeMinutes` | `480` | Dashboard session lifetime |
| `ExampleFrontend:Origin` | `http://localhost:3000` | CORS and example callback origin |
| `ExampleFrontend:CallbackUrl` | `http://localhost:3000/auth/callback` | Primary Next.js redirect URI |
| `ExampleFrontend:ClientId` | `example-web` | Primary example browser client |

Provider configuration is optional:

- ACS email: `SqlOS:Email:AzureCommunicationServicesConnectionString` and `SqlOS:Email:FromAddress`
- Twilio Verify: `SqlOS:PhoneOtp:Enabled` plus account, token, and service SID settings
- Microsoft OIDC: `SqlOS:Oidc:Microsoft:ClientId` and `SqlOS:Oidc:Microsoft:ClientSecret`

The [AppHost guide](../SqlOS.Example.AppHost/README.md#add-optional-email-phone-or-microsoft-login) lists accepted environment aliases.

## Key files

| File or folder | Responsibility |
| --- | --- |
| [`Program.cs`](Program.cs) | Composition root, SqlOS configuration, seeding, pipeline, endpoint mapping |
| [`Data/ExampleAppDbContext.cs`](Data/ExampleAppDbContext.cs) | Combined SqlOS and application EF Core model |
| [`Migrations/ExampleAppDbContextModelSnapshot.cs`](Migrations/ExampleAppDbContextModelSnapshot.cs) | Snapshot for application-owned combined schema migrations |
| [`Endpoints/ExampleAuthEndpoints.cs`](Endpoints/ExampleAuthEndpoints.cs) | Example headless auth, SSO, OIDC, refresh, logout, and session facade |
| [`Endpoints/ExampleEndpoints.cs`](Endpoints/ExampleEndpoints.cs) | Identity, profile, MFA, portal link, and workspace endpoints |
| [`FgaRetail/Endpoints/ChainEndpoints.cs`](FgaRetail/Endpoints/ChainEndpoints.cs) | Representative retail domain CRUD and FGA endpoint implementation |
| [`Services/ExampleFgaService.cs`](Services/ExampleFgaService.cs) | Organization/workspace subject, resource, role, and grant provisioning |
| [`Middleware/ExampleBearerTokenMiddleware.cs`](Middleware/ExampleBearerTokenMiddleware.cs) | Same-process bearer/service-account/agent demo authentication |

## Tests

Run the real-SQL suite with Docker available:

```bash
dotnet test examples/SqlOS.Example.IntegrationTests/SqlOS.Example.IntegrationTests.csproj
```

The suite covers password flows, multiple-organization selection, logout/refresh, hosted and headless OAuth, email OTP, OIDC seeding and callback variants, SSO, delegated portal links, MFA/workspaces, dashboard behavior, Swagger boundaries, transactional email, and retail FGA role behavior.

The tests use `tests/SqlOS.IntegrationTests.AppHost` to provision their database and test host. You do not need to start the public example AppHost first. `SqlOS.Example.Tests` separately covers the Razor Pages client's access-token refresh, ticket renewal, and logout fallback; it is not additional API protocol coverage.

## Local-sample limitations

- The checked-in dashboard password and AppHost NextAuth secret are deliberately local defaults. Replace them outside a developer workstation.
- The API runs over HTTP on localhost. Production issuers, callbacks, cookies, and provider callbacks should use HTTPS.
- Demo identities, API keys, and agent tokens are intentionally inspectable. Do not copy the demo switcher or raw token model into production authorization.
- Provider-backed email, phone, calendar, and social-login flows require real provider configuration.
- SQL state persists under the AppHost. See its reset instructions before assuming a code change caused stale seed data.
