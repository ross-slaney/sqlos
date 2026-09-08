# SqlOS

**Authentication and authorization for .NET applications, running in your process and your SQL Server or PostgreSQL database.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/SqlOS)](https://www.nuget.org/packages/SqlOS)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple)](https://dotnet.microsoft.com)

SqlOS gives your application hosted sign-in, enterprise SSO, organizations, sessions, an admin dashboard, and fine-grained authorization that filters EF Core queries in SQL. You own the accounts and data, and run the infrastructure.

Start with either shape:

| What you are building | Configuration | What users experience |
| --- | --- | --- |
| **One application** with a browser, native, or agent client | `UseSingleApplication(...)` derives one first-party public PKCE client | Your branded sign-in, your API, optional enterprise SSO and inbound SCIM |
| **An identity provider for multiple applications** | `ConfigureApplication(...)` describes the host; explicit clients describe each relying party | Several applications sign in with your accounts through OIDC; partner applications can request consent |

Both use the same users, sessions, organizations, FGA services, and dashboard. An API and an MCP endpoint are protected resources, not additional clients: one application can expose both.

## One application: describe it once

`builder.AddSqlOS<TContext>(...)` registers SqlOS, maps its auth endpoints and dashboard, and protects the surfaces you declare. Nothing else is placed or ordered by your code.

### Add it to a project

Use .NET 9, EF Core 9, and an accessible SQL Server or PostgreSQL database. The currently published package version is:

```bash
dotnet add package SqlOS --version 5.0.0
```

Optional packages for the MCP and custom-login examples:

```bash
dotnet add package SqlOS.Mcp --version 5.0.0
npm install @sqlos/headless@5.0.0
```

This is a complete `Program.cs`. Supply `ConnectionStrings:DefaultConnection` through user secrets or your deployment configuration, then run on `http://localhost:5050` in Development. Use an HTTPS origin in production.

```csharp
using Microsoft.EntityFrameworkCore;
using SqlOS;
using SqlOS.AuthServer.Extensions;
using SqlOS.Extensions;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection.");

builder.AddSqlOS<AppDbContext>(db => db.UseSqlServer(connectionString), options =>
    options.UseSingleApplication("Acme", app =>
    {
        app.Origin = "http://localhost:5050";
        app.Api = "/api";
        app.Brand(page =>
        {
            page.PageTitle = "Sign in to Acme";
            page.PageSubtitle = "Your team, in one place.";
            page.PrimaryColor = "#0f172a";
        });
    }));

var app = builder.Build();

var api = app.MapGroup("/api"); // already protected
api.MapGet("/me", (HttpContext http) => Results.Ok(new
{
    userId = http.GetSqlOSValidatedToken()!.UserId
}));

app.Run();

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : SqlOSDbContext<AppDbContext>(options);
```

Run it and `curl -i http://localhost:5050/api/me` returns `401` with a Bearer challenge that names `resource_metadata`. The hosted sign-in is at `http://localhost:5050/sqlos/auth`, the dashboard at `http://localhost:5050/sqlos`, and the derived client is `acme` with callback `http://localhost:5050/auth/callback`. Your frontend (a SPA, a native app, or any OIDC library) completes the authorization-code flow against that client, requests `resource=http://localhost:5050/api`, and sends the access token as a bearer. The [runnable Notes application](examples/SqlOS.OneCall.Api) is this setup plus MCP tools, a permission model, and real-database integration tests.

### What the application description controls

| Option | Effect |
| --- | --- |
| `Origin` | Public origin used to derive the default issuer, callback and surface audiences. Configure the externally visible URL, not a container address. |
| `Api = "/api"` | Requires a valid bearer token for `{Origin}/api` on mapped endpoints under `/api` before the handler runs. A sibling such as `/apiary` is outside it. |
| `Mcp("/mcp", ...)` | Registers and maps a stateless Streamable HTTP MCP server, with a separate `{Origin}/mcp` audience and OAuth discovery. Requires `SqlOS.Mcp`. |
| `Brand(...)` | Reconciles hosted sign-in branding into code-owned settings. Equivalent to `AuthServer.SeedAuthPage`. |
| `Authorization(...)` | Reconciles resource types, permissions, and roles. Equivalent to `Fga.Seed`; application services must still create grants and enforce access. |
| `Headless("/auth/authorize")` | Sends browser interaction to your UI at `{Origin}/auth/authorize`; SqlOS continues to own the authentication protocol. You must implement that UI. |
| `ClientId`, `RedirectPath`, `RedirectUris` | Identify the derived client and its allowed callbacks. The default callback is `{Origin}/auth/callback`; add your native app's callback to `RedirectUris`. |
| `AllowedScopes` | The derived client's scope allowlist and advertised resource scopes. This does not grant a user any FGA permission. |
| `EnablePasswordSignup`, `EnabledCredentialTypes` | Configure which sign-in/signup options appear. Enabling email or phone flows also requires their delivery configuration. |

SqlOS creates and upgrades its own tables at startup. Your EF migrations own your application's tables. The dashboard at `/sqlos` is available without login only in Development by default; outside Development it returns `404` until you configure operator access. For password-protected access, assign `options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password` and resolve `options.Dashboard.Password` from your secret store. See [dashboard configuration](https://sqlos.dev/docs/reference/hosting-api).

### How the surfaces are protected

`AddSqlOS` attaches that validation to the endpoints you map under a declared surface. It does not add middleware to the host pipeline and does not handle CORS. Signature, issuer, expiry, audience, and SqlOS session state are checked when the endpoint runs, after your `UseCors`, `UseAuthentication`, and the rest of the pipeline. Handlers read the result with `GetSqlOSValidatedToken()` or `HttpContext.User`. API and MCP tokens are not interchangeable. Unmatched paths under the prefix are ordinary 404s; a raw `app.Map("/api/...")` middleware branch is not an endpoint and is not protected.

The challenge's `resource_metadata` URL points to `/.well-known/oauth-protected-resource` for the API and `/.well-known/oauth-protected-resource/mcp` for MCP.

The token's `scope` claim is the client's delegation ceiling. Inspect it on `GetSqlOSValidatedToken()?.Scope` in the handler when a third-party client must not reach an operation, then still authorize the user with FGA.

## MCP and authorization: the same Notes service

The [Notes sample](examples/SqlOS.OneCall.Api) is a complete version of this setup. Each user gets a personal notebook on first use. Both HTTP handlers and MCP tools call `NotesService`, which checks the same permissions before reading or writing.

### `app.Mcp(...)`: register tools and protect the server

Add the `SqlOS.Mcp` package, then describe the surfaces and permission vocabulary together. This is the registration used with the sample's `NotesDbContext`, `NotesService`, and `NotesMcpTools`:

```csharp
using Microsoft.EntityFrameworkCore;
using SqlOS.Extensions;
using SqlOS.Mcp;
using SqlOS.OneCall.Api;

builder.Services.AddScoped<NotesService>();
builder.AddSqlOS<NotesDbContext>(db => db.UseSqlServer(connectionString), options =>
    options.UseSingleApplication("Notes", app =>
    {
        app.Origin = "http://localhost:5085";
        app.ClientId = "notes";
        app.Api = "/api";
        app.Mcp("/mcp", mcp => mcp.WithTools<NotesMcpTools>());
        app.Authorization(fga => fga
            .ResourceType("notebook", "Notebook")
            .Permission("NOTES_READ", "Read notes", "notebook")
            .Permission("NOTES_WRITE", "Write notes", "notebook")
            .Role("notebook_owner", "Notebook owner").Can("NOTES_READ", "NOTES_WRITE"));
    }));
```

Here is the entire tool class. It obtains the authenticated user from SqlOS and delegates to the application service:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using SqlOS.Mcp;
using SqlOS.OneCall.Api;

public sealed class NotesMcpTools
{
    [McpServerTool(Name = "list_notes"), Description("Lists the connecting user's notes.")]
    public static async Task<IReadOnlyList<string>> ListNotes(
        ISqlOSMcpUserContext user, NotesService notes, CancellationToken ct)
        => (await notes.ListAsync(RequireUser(user), ct)).Select(note => note.Text).ToArray();

    [McpServerTool(Name = "add_note"), Description("Adds a note to the connecting user's notebook.")]
    public static async Task<string> AddNote(
        ISqlOSMcpUserContext user, NotesService notes,
        [Description("The note text.")] string text, CancellationToken ct)
        => (await notes.AddAsync(RequireUser(user), text, ct)).Id.ToString();

    private static string RequireUser(ISqlOSMcpUserContext user)
        => user.UserId ?? throw new InvalidOperationException("This tool requires a user token.");
}
```

**What changes:** SqlOS hosts these tools at `/mcp`, validates tokens for `http://localhost:5085/mcp`, publishes the protected-resource document, and enables client ID metadata documents (CIMD) plus resource indicators. Compatible clients can use their metadata URL as `client_id` and request the MCP resource. This does not enable dynamic client registration (DCR); clients needing DCR require [explicit compatibility configuration](https://sqlos.dev/docs/authserver/dynamic-client-registration). Internet-hosted clients also need an HTTPS endpoint they can reach.

Tool calls are audited with their name, subject, client, and outcome, without arguments or tokens. `ISqlOSMcpUserContext` exposes the validated user, organization, client, and scopes. Hosting a tool does **not** automatically authorize its database operations: the service below supplies that enforcement. Use this when agents should act through the same business rules as your UI and API.

### `app.Authorization(...)`: vocabulary, grants, and enforcement

The registration above creates one resource type, two permissions, and one flat role. It creates no notebooks and grants nobody access. The sample's full [Notes.cs](examples/SqlOS.OneCall.Api/Notes.cs) defines the EF entities and handles initial provisioning; the core read/write methods are:

```csharp
public async Task<IReadOnlyList<Note>> ListAsync(string userId, CancellationToken ct)
{
    var notebookId = await EnsureNotebookAsync(userId, NotesAuthorization.ReadPermission, ct);
    return await db.Notes.Where(n => n.NotebookId == notebookId)
        .OrderBy(n => n.CreatedAt).ToListAsync(ct);
}

public async Task<Note> AddAsync(string userId, string text, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > 2000)
        throw new ArgumentException("A note must contain between 1 and 2000 characters.", nameof(text));

    var notebookId = await EnsureNotebookAsync(userId, NotesAuthorization.WritePermission, ct);
    var note = new Note
    {
        Id = Guid.NewGuid(), NotebookId = notebookId,
        Text = text.Trim(), CreatedAt = DateTime.UtcNow
    };
    db.Notes.Add(note);
    await db.SaveChangesAsync(ct);
    return note;
}

private async Task<string> EnsureNotebookAsync(string userId, string permission, CancellationToken ct)
{
    var notebookId = NotesAuthorization.NotebookId(userId);
    await CreateNotebookIfMissingAsync(userId, notebookId, ct);
    var access = await fga.CheckAccessAsync(userId, permission, notebookId);
    return access.Allowed ? notebookId : throw new UnauthorizedAccessException("Notebook access denied.");
}
```

Creation provisions the user subject, notebook resource, and owner grant **once**, in the same transaction as the application's unique notebook row:

```csharp
private async Task CreateNotebookIfMissingAsync(string userId, string notebookId, CancellationToken ct)
{
    if (await db.Notebooks.AnyAsync(x => x.UserId == userId, ct)) return;

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    db.Notebooks.Add(new Notebook { UserId = userId });
    try
    {
        // The unique user key reserves creation across requests and replicas. Another creator
        // waits for this transaction; it can only observe a notebook whose grant also committed.
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException)
    {
        await transaction.RollbackAsync(ct);
        await transaction.DisposeAsync();
        db.ChangeTracker.Clear();
        if (!await db.Notebooks.AnyAsync(x => x.UserId == userId, ct)) throw;
        return;
    }

    await db.ProvisionUserSubjectAsync(userId, userId, cancellationToken: ct);
    await db.ProvisionResourceWithIdAsync(notebookId, NotesAuthorization.NotebookType, $"Notebook of {userId}", cancellationToken: ct);
    await db.GrantRoleAsync(userId, notebookId, NotesAuthorization.OwnerRole, ct);
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
}
```

The unique notebook row prevents concurrent first requests from creating duplicate grants. Later requests only check permissions: removing `notebook_owner` in the dashboard or through the grant API makes both API reads/writes and MCP calls fail. Repeated calls do not restore it. Restoring the role is an explicit administrative action. Alice and Bob have separate resources, so neither can read the other's notebook.

**Why use this:** keep the authorization model reproducible in code while memberships, resources, and grants remain runtime data. For a workspace/project hierarchy, attach projects to their workspace resource and grant at the workspace to inherit access below it. For lists spanning many resources, use `BuildFilterAsync<T>` on entities implementing `IHasResourceId` to filter in SQL; see the [complete EF authorization example](https://sqlos.dev/docs/quickstarts/ef-authorization).

Startup reconciliation uses stable keys and explicit configuration ownership. Code-owned definitions are visible in the dashboard; operator-owned definitions are not silently taken over. Renaming a display label keeps its identity; changing a key is a model migration. See the [authorization guide](https://sqlos.dev/docs/quickstarts/ef-authorization) for model and grant management.

## `app.Brand(...)`: hosted pages and ownership

Use this inside either application description:

```csharp
app.Brand(page =>
{
    page.PageTitle = "Welcome to Acme";
    page.PageSubtitle = "Sign in to your workspace.";
    page.PrimaryColor = "#0f172a";
    page.AccentColor = "#2563eb";
    page.BackgroundColor = "#f8fafc";
    page.Layout = "stacked"; // "split" is the other layout
    page.EnablePasswordSignup = false;
    page.EnabledCredentialTypes = ["password"];
});
```

**What changes:** hosted sign-in screens use these colors, layout, and copy; this example removes self-service password signup while allowing existing users to sign in. It neither creates users nor configures an SSO connection. Add `page.LogoBase64` with an image data URL such as `data:image/png;base64,...`. The settings are reconciled at startup and marked code-owned, so the dashboard shows their source and prevents conflicting edits.

The application preset seeds default auth-page and email branding even when you omit `Brand`. `Brand` customizes the auth pages; it does not copy its colors into emails. Customize email visuals separately with `options.AuthServer.SeedAuthEmails(...)`.

**Why use it:** deploy the same sign-in experience with your application in each environment. For a new host where operators should own branding instead, disable both automatic seeds and omit `Brand`:

```csharp
app.ConfigureAuthPageBranding = false;
app.ConfigureEmailBranding = false;
```

Operators can then configure branding in the dashboard. Removing an existing seed does not silently transfer ownership: it marks the code-owned configuration orphaned, and the next authorized dashboard save can explicitly claim the orphaned settings. Adding a seed over dashboard-owned settings fails rather than overwriting them.

For custom-rendered screens, `app.Headless("/auth/authorize")` redirects interaction to your UI; implement it with [`@sqlos/headless`](https://sqlos.dev/docs/guides/custom-login-ui). SqlOS still handles credentials, SSO, MFA, consent, sessions, and token issuance. `Brand` remains available through the headless view model.

## Multiple applications: SqlOS as your identity provider

Use this shape when multiple products or partners need to sign in with your accounts. **Downstream OIDC is supported**: SqlOS issues ID tokens and exposes UserInfo to the applications. Upstream social/enterprise OIDC connections are a separate capability: those let users sign in to SqlOS with another provider.

### Complete identity-provider host

This host serves two downstream applications, retains a protected local API, and enables inbound SCIM. Configure the database, dashboard password, and partner client secret through the host's secret mechanism. The partner receives the same client secret through a secure setup process; it is never embedded in a browser application.

```csharp
using Microsoft.EntityFrameworkCore;
using SqlOS;
using SqlOS.AuthServer.Extensions;
using SqlOS.Configuration;
using SqlOS.Extensions;

var builder = WebApplication.CreateBuilder(args);
const string origin = "https://id.acme.example.com";
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection.");
var dashboardPassword = builder.Configuration["SqlOS:Dashboard:Password"]
    ?? throw new InvalidOperationException("Configure SqlOS:Dashboard:Password.");
var partnerSecret = builder.Configuration["SqlOS:PartnerClientSecret"]
    ?? throw new InvalidOperationException("Configure SqlOS:PartnerClientSecret.");

builder.AddSqlOS<AppDbContext>(db => db.UseSqlServer(connectionString), options =>
{
    options.ConfigureApplication("Acme Identity", app =>
    {
        app.Origin = origin;
        app.Api = "/api";
        app.Brand(page =>
        {
            page.PageTitle = "Sign in with Acme";
            page.PrimaryColor = "#0f172a";
        });
    });
    options.AuthServer.PublicOrigin = origin;
    options.AuthServer.Issuer = origin + "/sqlos/auth";
    options.AuthServer.ConfigureOpenIdProvider(oidc =>
    {
        oidc.Enabled = true;
        oidc.PublishDiscoveryDocument = true;
        oidc.EnableUserInfoEndpoint = true;
    });
    options.AuthServer.EnableScim = true;
    options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
    options.Dashboard.Password = dashboardPassword;

    options.AuthServer.SeedClient(client =>
    {
        client.ClientId = "acme-web";
        client.Name = "Acme Web";
        client.ClientType = "public_pkce";
        client.IsFirstParty = true;
        client.RequirePkce = true;
        client.Audience = origin + "/api";
        client.RedirectUris = ["https://app.acme.example.com/auth/callback"];
        client.AllowedScopes = ["openid", "profile", "email"];
    });
    options.AuthServer.SeedClient(client =>
    {
        client.ClientId = "partner-portal";
        client.Name = "Partner Portal";
        client.ClientType = "confidential";
        client.TokenEndpointAuthMethod = "client_secret_post";
        client.ClientSecretResolver = () => partnerSecret;
        client.IsFirstParty = false;
        client.RequirePkce = true;
        client.Audience = "partner-portal";
        client.RedirectUris = ["https://portal.partner.example/auth/callback"];
        client.AllowedScopes = ["openid", "profile", "email"];
    });
});

var app = builder.Build();
app.MapGet("/api/me", (HttpContext http) => Results.Ok(new
{
    userId = http.GetSqlOSValidatedToken()!.UserId
}));
app.Run();

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : SqlOSDbContext<AppDbContext>(options);
```

`ConfigureApplication` keeps the same `Api`, `Mcp`, `Brand`, `Headless`, and `Authorization` options but seeds **no client**. Each explicit client has its own redirect allowlist, scopes, audience, and consent behavior. Acme Web can call this host's API. The partner's token cannot: its audience is `partner-portal`. First-party status skips consent; it does not bypass application access policy or FGA.

The issuer is `https://id.acme.example.com/sqlos/auth`; OIDC discovery is at `https://id.acme.example.com/sqlos/auth/.well-known/openid-configuration`. Code-owned clients reconcile on startup. Dashboard/API-created clients can coexist under different IDs, using the same domain validation and audit behavior. Use [application access policies](https://sqlos.dev/docs/authserver/application-access) to restrict which organizations and principals may sign in to each application.

### Complete downstream OIDC application

This is `Program.cs` in the **partner portal**, a separate ASP.NET Core web application with the `Microsoft.AspNetCore.Authentication.OpenIdConnect` package. Deploy it at the registered HTTPS origin and configure its copy of the client secret:

```csharp
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);
var clientSecret = builder.Configuration["Authentication:SqlOS:ClientSecret"]
    ?? throw new InvalidOperationException("Configure Authentication:SqlOS:ClientSecret.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = "SqlOS";
}).AddCookie().AddOpenIdConnect("SqlOS", options =>
{
    options.Authority = "https://id.acme.example.com/sqlos/auth";
    options.ClientId = "partner-portal";
    options.ClientSecret = clientSecret;
    options.CallbackPath = "/auth/callback";
    options.ResponseType = "code";
    options.UsePkce = true;
    options.GetClaimsFromUserInfoEndpoint = true;
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = "name";
    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");
});
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/", (HttpContext http) => Results.Ok(new
{
    name = http.User.Identity!.Name,
    email = http.User.FindFirst("email")?.Value
})).RequireAuthorization();
app.Run();
```

Visit the portal: it redirects to SqlOS, the user signs in and consents, then returns to `/auth/callback`. The handler validates the ID token, fetches the requested profile claims from UserInfo, and establishes the portal's own cookie. This is the outbound OIDC identity-provider use case; no SqlOS database or package is required in the relying party. A complete [Sign in with X guide](https://sqlos.dev/docs/guides/sign-in-with-x) also demonstrates a Next.js relying party.

### SCIM: provision into SqlOS

The host above enables the **SCIM 2.0 server** at `/sqlos/scim/v2`. An upstream directory such as Entra or Okta sends users and groups into one SqlOS organization:

1. Create or select the organization in the SqlOS dashboard.
2. Open its SCIM configuration and create an enabled connection. Copy the returned SCIM base URL and one-time bearer token into the upstream provider's provisioning configuration. SqlOS stores only the token hash.
3. Test provisioning a user and group from that provider. Inspect sync outcomes in SqlOS, then configure group mappings to application roles or FGA roles on chosen resources.
4. Disable a provisioned user or remove a group membership upstream and synchronize again; inspect the resulting user/membership and managed-grant changes. Keep SSO and each application's access policy configured separately.

For trusted server-side administration, the same creation operation is available through `SqlOSAdminService` after startup:

```csharp
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;

await using var scope = app.Services.CreateAsyncScope();
var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
var connection = await admin.CreateScimConnectionAsync(
    new SqlOSCreateScimConnectionRequest(organizationId, "Acme directory"));
// Hand connection.BaseUrl and connection.Token to the authorized operator once.
// Do not log or persist the raw token; rotation creates a replacement.
```

Run this as an explicit setup operation for an existing organization, not on every restart. The dashboard uses the same administration service and validation. A bearer token is scoped to its connection's organization; enabling SCIM globally does not create a connection or grant applications access.

**Outbound SCIM provisioning is not implemented.** SqlOS can be the central OIDC provider for your applications and receive enterprise-directory provisioning, but it does not push accounts to downstream SaaS services over SCIM. That requires a separate provisioning integration. See [SCIM directory sync](https://sqlos.dev/docs/authserver/scim-directory-sync) for connection lifecycle, rotation, group mapping, and protocol examples.

For runnable multi-client setups, the [retail AppHost](examples/SqlOS.Example.AppHost/README.md) runs one `ConfigureApplication` host with Next.js and Angular clients (Expo connects separately). The [Todo AppHost](examples/SqlOS.Todo.Api/README.md) runs the host and Razor Pages client, with a CLI available separately. [Sign in with X](examples/SqlOS.SignInWithX.AppHost/README.md) demonstrates a dedicated provider and a third-party Auth.js relying party.

### Growing an existing single application

Replace `UseSingleApplication` with `ConfigureApplication`, keeping the same host block, API/MCP paths, issuer, origin, branding, and permission keys. Remove the single-client-only properties from that block and explicitly seed the existing client with its **same client ID, audience, redirect URIs, scopes, PKCE, and first-party settings**. Then add the second client.

Do not combine `UseSingleApplication` with explicit startup client seeds; startup rejects the ambiguous ownership. Changing the hosting mode alone does not require a new issuer, database, API implementation, or MCP tools. Moving to another domain is a separate migration because it changes issuer and audience values. See [multiple applications](https://sqlos.dev/docs/authserver/multiple-applications) for the complete migration example.

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

If the hosted pages don't fit your product, keep SqlOS as the protocol engine and draw every screen yourself: `app.Headless("/auth/authorize")` in the one-call setup, or `AuthServer.UseHeadlessAuthPage` on a multi-app host. Your frontend talks to a typed state machine — login, signup, OTP, MFA, consent — while SqlOS still owns OAuth, PKCE, sessions, and tokens. Extra signup fields, A/B tests, and native-feeling popups all become your UI's decisions.

<p align="center">
  <img src="https://sqlos.dev/docs/guides-custom-login-ui.svg" alt="Product-owned login UI connected to the SqlOS headless authentication state machine" width="900" />
</p>

→ [Build your own login and signup UI](https://sqlos.dev/docs/guides/custom-login-ui) · [Headless auth reference](https://sqlos.dev/docs/authserver/headless-auth)

## See it running in 2 minutes

The Todo sample gives you a working login flow and authorized EF Core queries without touching your own code:

```bash
dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
```

Then open `http://localhost:5090/`. The Aspire AppHost starts PostgreSQL, the Todo API with SqlOS at `http://localhost:5080`, and a Razor Pages client at `http://localhost:5090`. Set `SqlOS:DatabaseProvider=SqlServer` to start SQL Server instead.

<p align="center">
  <img src="https://sqlos.dev/docs/guides-password-login.png" alt="Hosted AuthPage password step from the SqlOS Todo sample" width="560" />
</p>

[Todo sample walkthrough](https://sqlos.dev/docs/quickstarts/run-todo) · [All documentation](https://sqlos.dev/docs)

## Guides

Everything is documented as a task, not a feature tour. Start with whichever matches your next milestone:

| I want to… | Guide |
| --- | --- |
| Run on SQL Server or PostgreSQL | [Choose a database](https://sqlos.dev/docs/guides/choosing-a-provider) |
| Protect an API with access tokens | [Protect an API](https://sqlos.dev/docs/quickstarts/protect-api) |
| Return only the rows a user may see | [Authorize EF Core queries](https://sqlos.dev/docs/quickstarts/ef-authorization) |
| Add native ASP.NET Core password login | [Password login](https://sqlos.dev/docs/guides/password-login) |
| Add Google/Microsoft/GitHub sign-in | [Social OIDC login](https://sqlos.dev/docs/guides/social-oidc) |
| Sell to enterprises that require SSO | [SAML SSO](https://sqlos.dev/docs/guides/saml-sso) · [SCIM directory sync](https://sqlos.dev/docs/guides/scim-directory-sync) |
| Let other apps sign in with my app's accounts | [Sign in with X](https://sqlos.dev/docs/guides/sign-in-with-x) |
| Build my own login UI | [Custom login UI](https://sqlos.dev/docs/guides/custom-login-ui) |
| Host an MCP server agents can sign in to | [MCP server](https://sqlos.dev/docs/authserver/mcp-server) |
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
