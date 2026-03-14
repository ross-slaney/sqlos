# OIDC Auth

`SqlOS.AuthServer` supports first-party OIDC login for:

- Google
- Microsoft
- Apple
- custom OpenID Connect providers

The current model is:

- OIDC connections are global to the auth server
- the example frontend never calls `SqlOS` auth endpoints directly
- the example backend uses `SqlOSOidcAuthService`
- OIDC login coexists with password login and org SAML SSO
- org SSO wins when the entered email domain matches an enabled SAML organization

## What SqlOS Stores

OIDC auth adds:

- `SqlOSAuthOidcConnections`
- OIDC-linked `SqlOSExternalIdentities`

The downstream identity/session pipeline stays the same:

- `Users`
- `UserEmails`
- `Memberships`
- `Sessions`
- `RefreshTokens`
- `AuditEvents`

Provider secrets are stored encrypted at rest through ASP.NET Data Protection.

## Dashboard Setup

Use the dashboard route:

- `http://localhost:5062/sqlos/admin/auth/oidc`

From there you can:

- create Google, Microsoft, Apple, or custom OIDC connections
- choose discovery or manual endpoint configuration for custom providers
- rotate client secrets
- paste Apple Team ID, Key ID, and private key material
- enable or disable a provider

The dashboard shows the connection ID after a provider is created. The example app callback pattern is:

```text
http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}
```

That exact callback URI must appear in:

- the OIDC connection's allowed callback URI list inside SqlOS
- the provider application's registered redirect URIs

## Example App Flow

1. The user enters an email on `/login`.
2. The frontend loads available providers from `GET /api/v1/auth/oidc/providers`.
3. Clicking a provider calls `POST /api/v1/auth/oidc/start`.
4. The example backend runs home realm discovery first.
5. If the email domain matches org SSO, the backend starts SAML instead of OIDC.
6. Otherwise the backend starts the provider authorization flow using `SqlOSOidcAuthService`.
7. The provider redirects back to the example backend callback.
8. The backend completes provider auth through `SqlOSOidcAuthService`, creates a short-lived handoff token, and redirects the browser to the frontend callback.
9. The frontend calls `POST /api/v1/auth/oidc/complete`.
10. The backend creates the SqlOS session and tokens, then the frontend finishes sign-in through NextAuth.

## Org Behavior

Current OIDC login behavior is intentionally narrow:

- zero org memberships: session/token issued with `org_id = null`
- one org membership: session/token issued for that org
- more than one org membership: sign-in fails with a clear error

That limitation is only for the current v1 OIDC flow.

## Local Testing

For the shared example stack:

1. Run the AppHost:

```bash
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

2. Open the dashboard and create an OIDC connection.
3. Copy the connection ID from the OIDC connection list.
4. Update the provider redirect URI and the SqlOS allowed callback URI list to:

```text
http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}
```

5. Open `http://localhost:3010/login`.
6. Enter an email, then click the provider button.

For provider-specific setup, use:

- [Google OIDC](GOOGLE_OIDC.md)
- [Microsoft OIDC](MICROSOFT_OIDC.md)
- [Apple OIDC](APPLE_OIDC.md)
- [Custom OIDC](CUSTOM_OIDC.md)
