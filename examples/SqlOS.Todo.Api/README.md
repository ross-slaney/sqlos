# SqlOS Todo Sample

This is the narrow, MCP-oriented reference sample for SqlOS.

It shows:

- hosted auth first
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

## What to try

1. Start with `Hosted sign in`.
2. Create a user on the hosted SqlOS auth page.
3. Land in the Todo UI and create a few items.
4. Inspect `/.well-known/oauth-protected-resource`.
5. Try `headless.html` after enabling `TodoSample__EnableHeadless=true`.
6. Use `todo-local` for preregistered localhost development.
7. Publish the sample on HTTPS, then use:
   - `GET /clients/portable-client.json` as a sample `client_id` metadata document
   - `POST /sqlos/auth/register` after enabling `TodoSample__EnableDcr=true`

## Local preregistered client

Use:

- `client_id`: `todo-local`
- redirect URI: `http://localhost:8787/oauth/callback`
- PKCE: required
- token auth method: `none`

This keeps local development simple before you switch to public `CIMD` or `DCR`.
