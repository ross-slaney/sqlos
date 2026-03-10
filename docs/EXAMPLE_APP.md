# Example App

The shared example demonstrates the merged runtime end to end:

- local password login
- org membership
- SAML SSO initiation and callback flow
- refresh/logout
- FGA-protected workspace access
- shared dashboard administration

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
- web app: `http://localhost:3001/`

## Validation Flow

1. Create an organization, user, and membership in the auth admin dashboard.
2. Open the example web app and sign in through `/login`.
3. Confirm `/app` shows session and token debug data.
4. Create and list workspaces through the protected app flow.
5. Return to the dashboard and validate auth sessions plus FGA resource/grant data.

For a customer-tenant SAML walkthrough with Microsoft Entra ID, use:

- [Entra SSO Testing](ENTRA_SSO.md)
