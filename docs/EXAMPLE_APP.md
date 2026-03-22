# Example App

The Example stack is the **broad** SqlOS demo.

Use it when you want to explore:

- local password login
- hosted and headless auth UI
- Google, Microsoft, Apple, and custom OIDC login
- org membership
- SAML SSO initiation and callback flow
- refresh/logout
- FGA-protected workspace access
- shared dashboard administration

If your goal is specifically:

> "I want SqlOS to work with MCP clients, resource metadata, prereg/CIMD/DCR, and audience-aware APIs."

Start with the Todo sample first:

- `examples/SqlOS.Todo.Api`
- `examples/SqlOS.Todo.AppHost`
- `examples/SqlOS.Todo.IntegrationTests`

## Projects

- `examples/SqlOS.Example.Api`
- `examples/SqlOS.Example.Web`
- `examples/SqlOS.Example.AppHost`

## Run

```bash
cd examples/SqlOS.Example.Web
npm install

cd /path/to/SqlOS
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

## URLs

- dashboard shell: `http://localhost:5062/sqlos/`
- auth admin: `http://localhost:5062/sqlos/admin/auth/`
- FGA admin: `http://localhost:5062/sqlos/admin/fga/`
- API swagger: `http://localhost:5062/swagger`
- web app: `http://localhost:3010/`

## Validation flow

1. Create an organization, user, and membership in the auth admin dashboard.
2. Open the example web app and sign in through the hosted flow.
3. Confirm the app shows session and token debug data.
4. Switch to the headless route and compare the same auth server with app-owned UI.
5. Optionally configure an OIDC connection and repeat the sign-in flow with provider buttons.
6. Create and list workspaces through the protected app flow.
7. Return to the dashboard and validate auth sessions plus FGA resource/grant data.

For a customer-tenant SAML walkthrough with Microsoft Entra ID, use:

- [Entra SSO Testing](ENTRA_SSO.md)

For OIDC setup, use:

- [OIDC auth guide](../web/content/docs/authserver/oidc-auth.mdx)
- [Google OIDC](GOOGLE_OIDC.md)
- [Microsoft OIDC](MICROSOFT_OIDC.md)
- [Apple OIDC](APPLE_OIDC.md)
- [Custom OIDC](CUSTOM_OIDC.md)
