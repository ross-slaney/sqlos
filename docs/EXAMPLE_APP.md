# Example App

The Example stack is the **broad** SqlOS demo.

Use it when you want to explore:

- local password login
- hosted and headless auth UI
- optional SMS OTP through Twilio Verify
- Google, Microsoft, GitHub, Apple, and custom OIDC/social login
- org membership
- organization email invitations
- SAML SSO initiation and callback flow
- delegated organization-admin SSO setup links with DNS TXT domain verification
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
- `examples/SqlOS.Todo.Api`

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
- FGA admin: `http://localhost:5062/sqlos/admin/fga/resources`
- API swagger: `http://localhost:5062/swagger`
- web app: `http://localhost:3010/`
- host-launched SSO portal demo: `http://localhost:3010/retail/sso`
- todo app: `http://localhost:5080/`

## Validation flow

1. Create an organization, user, and membership in the auth admin dashboard.
2. Open the organization Invitations tab, send an invite, and copy the accept link.
3. Accept the invite through hosted AuthPage using Email OTP, password, or configured SSO.
4. Open the example web app and sign in through the hosted flow.
5. Confirm the app shows session and token debug data.
6. Switch to the headless route and compare the same auth server with app-owned UI.
7. Optionally enable [SMS OTP](SMS_OTP.md) and repeat sign in or signup with a phone code.
8. Optionally configure an OIDC connection and repeat the sign-in flow with provider buttons.
9. Open `/retail/sso`, create a delegated SSO setup link for the signed-in organization, and open the portal.
10. In the portal, choose Entra, Okta, Google Workspace, or Generic SAML, verify the organization's email domain through the TXT record shown by the portal, paste/upload metadata XML, activate, and run a test redirect.
11. Create and list workspaces through the protected app flow.
12. Open the Retail app, create/edit/restock/delete inventory, then open **Governance > Audit Logs** in the SqlOS dashboard and filter by application key `northwind-retail`.
13. Return to the dashboard and validate auth sessions plus FGA resource/grant data.

The host-launched path calls the sample API endpoint `POST /api/sso-portal-links`, which wraps the SqlOS portal-session service for the current `org_id`. Platform admins can create and revoke the same delegated links from the dashboard organization SSO tab.

For local-only SSO portal testing without publishing DNS, create the organization with a primary domain in the dashboard before opening the portal. That exercises the operator-managed fallback. To test the self-serve verified-domain path, use a domain where you can publish the `_sqlos-verify.<domain>` TXT record, or replace `ISqlOSDomainDnsVerifier` in your host with a local test implementation.

For a customer-tenant SAML walkthrough with Microsoft Entra ID, use:

- [Customer-managed enterprise SSO](../web/content/docs/guides/customer-managed-sso.mdx)
- [Entra SSO Testing](ENTRA_SSO.md)

For OIDC setup, use:

- [OIDC auth guide](../web/content/docs/authserver/oidc-auth.mdx)
- [Google OIDC](GOOGLE_OIDC.md)
- [Microsoft OIDC](MICROSOFT_OIDC.md)
- [GitHub OIDC](GITHUB_OIDC.md)
- [Apple OIDC](APPLE_OIDC.md)
- [Custom OIDC](CUSTOM_OIDC.md)

For audit-log validation, use:

- [Audit Logs](AUDIT_LOGS.md)
