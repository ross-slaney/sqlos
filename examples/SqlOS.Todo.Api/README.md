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

Use the AppHost to get PostgreSQL plus the Todo sample on one command:

```bash
dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
```

The API calls `UseNpgsql` for the AppHost connection string. Set `SqlOS:DatabaseProvider=SqlServer` to start SQL Server instead.

Prove the Postgres path with the Playwright suite (hosted signup into the Razor client, plus the real Todo CLI through the device-approve journey):

```bash
./scripts/todo-e2e.sh
```

Or run the broader Aspire stack and get the Todo app there too:

```bash
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

Open `http://localhost:5080/`.

Swagger UI is available at `http://localhost:5080/swagger`, and the generated spec is served from `http://localhost:5080/swagger/v1/swagger.json`.

If you already ran an older version of the sample, reset the Todo sample SQL database or persistent volume once before rerunning. Existing todos are **not** backfilled into the FGA graph.

## Multi-application configuration

`Program.cs` uses `ConfigureApplication("SqlOS Todo", ...)` with `Brand`, `Authorization`, and optional `Headless`. It seeds each client explicitly: `todo-web`, `example-aspnet`, `todo-cli`, `todo-local`, and `todo-mcp-local`. The Razor Pages app and CLI are runnable clients of the same identity host; AppHost launches Razor Pages and the CLI runs separately.

Todo keeps its configurable resource audience (`http://localhost:5080/api/todos` by default), explicit resource metadata, and per-operation scope/FGA checks for `/api/todos` and `/api/me`. A declaration of `Api = "/api"` would derive a different audience, so it is intentionally not used here. The external MCP broker remains a client; this host does not serve MCP tools.

## Start a single application

For the full `UseSingleApplication` API/MCP experience, run the [Notes sample](../SqlOS.OneCall.Api/README.md). A new one-app host starts with:

```csharp
builder.AddSqlOS<TodoSampleDbContext>(options =>
{
    options.UseSingleApplication("Todo", app =>
    {
        app.Origin = "https://todo.example.com";
    });
});
```

That creates one first-party public PKCE application with `openid`, `profile`, `email`, and `offline_access` scopes, a `{Origin}/auth/callback` redirect URI, and open `all_organizations` application access. When adding another owned client such as a CLI, migrate to `ConfigureApplication` and explicitly preserve the original client identity. A same-host MCP surface can already be declared in single-application mode.

## FGA model

Resource hierarchy:

- `root`
- `tenant::{subjectId}`
- `todo::{todoId}`

Role and permission matrix:

- `tenant_owner` on `tenant::{subjectId}`
- permissions: `TENANT_CREATE_TODO`, `TODO_READ`, `TODO_WRITE`

Each authenticated user gets one tenant root resource under `root`. Every todo is created as a child resource beneath that tenant node, so the dashboard shows the hierarchy directly and list queries can use the SqlOS FGA filter instead of hand-written owner predicates.

`GET /api/todos` returns one page, newest first: `{ data, pageSize, nextCursor, hasNextPage }`. Pass `pageSize` (1 to 100, default 20) and the previous page's `nextCursor` as `cursor`. The endpoint pages with `ToPageAsync` and declares no page order: SqlOS reads the caller's own todos through the node index it keeps for `TodoItem` (`SqlOSFgaPage_TodoItems_Id`), so a page costs about the caller's todos, however many todos other users have. The sample creates its schema with `EnsureCreated`, so a database created by an earlier version of the sample has no node index table (and may still have an unused `SqlOSFgaPage_TodoItems_CreatedAt`); delete the local database to recreate it. Until then, startup logs the missing table and pages still return the right rows, reading the todos table in order.

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

If your headless frontend runs on a different origin than the SqlOS host, use credentialed browser requests to the SqlOS headless endpoints so SqlOS can persist its reusable issuer session cookie. Follow-up `/sqlos/auth/authorize?prompt=none` requests should then silently complete when that session exists, or return `login_required` when it does not.
