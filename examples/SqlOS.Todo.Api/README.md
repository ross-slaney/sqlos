# SqlOS Todo Sample

This is the hosted-first Todo sample for SqlOS.

It shows:

- hosted auth first
- simple per-user FGA with inherited todo access
- a documented headless follow-on path
- a protected resource with audience enforcement
- protected-resource metadata at `/.well-known/oauth-protected-resource`
- preregistered local development for `todo-local`
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

1. Start with `Hosted sign in`.
2. Create a user on the hosted SqlOS auth page.
3. Land in the Todo UI and create a few items.
4. Open `/sqlos/admin/fga/resources` and confirm the tree shows your tenant plus child todo resources.
5. Inspect `/.well-known/oauth-protected-resource`.
6. Try `headless.html` after enabling `TodoSample__EnableHeadless=true`.
7. Use `todo-local` for preregistered localhost development.
8. Publish the sample on HTTPS, then use:
   - `GET /clients/portable-client.json` as a sample `client_id` metadata document
   - `POST /sqlos/auth/register` after enabling `TodoSample__EnableDcr=true`

## Local preregistered client

Use:

- `client_id`: `todo-local`
- redirect URI: `http://localhost:3100/oauth/callback`
- PKCE: required
- token auth method: `none`

This keeps local development simple before you switch to public `CIMD` or `DCR`.
