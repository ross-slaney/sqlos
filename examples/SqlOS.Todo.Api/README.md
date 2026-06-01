# SqlOS Todo Sample

This is the hosted-first Todo sample for SqlOS.

It shows:

- hosted AuthPage first
- passwordless email-code sign in/sign up when `TodoSample__EnableEmailOtp=true`
- seeded email branding for the built-in auth email templates
- simple per-user FGA with inherited todo access
- a protected resource with audience enforcement
- protected-resource metadata at `/.well-known/oauth-protected-resource`
- preregistered local development for `todo-local`
- an Emcy-hosted MCP broker client for local Todo demos: `todo-mcp-local`
- portable public-client onboarding paths for `CIMD` and optional `DCR`

## Run locally

Use the AppHost to get SQL Server plus the Todo sample on one command:

```bash
dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
```

Or run the broader Aspire stack and get the Todo app there too:

```bash
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

Open `http://localhost:5080/`.

Swagger UI is available at `http://localhost:5080/swagger`, and the generated spec is served from `http://localhost:5080/swagger/v1/swagger.json`.

If you already ran an older version of the sample, reset the Todo sample SQL database or persistent volume once before rerunning. Existing todos are **not** backfilled into the FGA graph.

## Single-app quickstart

The Todo sample intentionally demonstrates advanced client setup for local web, CLI, MCP, CIMD, and DCR flows. A new one-app project can start smaller:

```csharp
builder.AddSqlOS<TodoSampleDbContext>(options =>
{
    options.UseSingleApplication("Todo", app =>
    {
        app.Origin = "https://todo.example.com";
    });
});
```

That creates one first-party public PKCE application with `openid`, `profile`, `email`, and `offline_access` scopes, a `{Origin}/auth/callback` redirect URI, and open `all_organizations` application access. Add explicit clients later when you need CLI, MCP, or portable public-client demos.

## FGA model

Resource hierarchy:

- `root`
- `tenant::{userId}`
- `todo::{todoId}`

Role and permission matrix:

- `tenant_owner` on `tenant::{userId}`
- permissions: `TENANT_CREATE_TODO`, `TODO_READ`, `TODO_WRITE`

Each authenticated user gets one tenant root resource under `root`. Every todo is created as a child resource beneath that tenant node, so the dashboard shows the hierarchy directly and list queries can use the SqlOS FGA filter instead of hand-written owner predicates.

## What to try

1. For the email OTP demo, run the AppHost with `TodoSample__EnableEmailOtp=true` and ACS email settings.
2. Start with `Email code sign up` or `Email code sign in`.
3. Create or sign into a user on the hosted SqlOS auth page. The Todo app only starts the OAuth request; SqlOS owns the OTP challenge, verification, email branding, and redirect back to the Todo callback.
4. Land in the Todo UI and create a few items.
5. Open `/sqlos/admin/fga/resources` and confirm the tree shows your tenant plus child todo resources.
6. Inspect `/.well-known/oauth-protected-resource`.
7. Use `todo-local` for preregistered localhost direct-client development.
8. Use `todo-mcp-local` when Emcy is brokering Todo auth through a hosted MCP server.
9. Publish the sample on HTTPS, then use:
   - `GET /clients/portable-client.json` as a sample `client_id` metadata document
   - `POST /sqlos/auth/register` after enabling `TodoSample__EnableDcr=true`

## Local preregistered client

Use:

- `client_id`: `todo-local`
- redirect URI: `http://localhost:3100/oauth/callback`
- PKCE: required
- token auth method: `none`

This keeps local development simple before you switch to public `CIMD` or `DCR`.

## Local Emcy broker client

Use:

- `client_id`: `todo-mcp-local`
- redirect URI: `http://localhost:5150/api/v1/hosted-mcp/todo-local/oauth/callback`
- PKCE: required
- token auth method: `none`

This is the local downstream client for the Emcy-hosted Todo MCP demo. The Todo API still validates the same Todo audience; Emcy just brokers the auth flow and holds the downstream grant server-side.

## Headless session reuse

If your headless frontend runs on a different origin than the SqlOS host, use credentialed browser requests to the SqlOS headless endpoints so SqlOS can persist its reusable auth-page session cookie. Follow-up `/sqlos/auth/authorize?prompt=none` requests should then silently complete when that session exists, or return `login_required` when it does not.
