# Microsoft OIDC

This guide configures Microsoft sign-in through Microsoft Entra ID for the shared example app.

## Redirect URI

The example backend callback is connection-specific:

```text
http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}
```

Create the OIDC connection in SqlOS first, then copy its connection ID from the dashboard and register the final callback URI in Entra.

## Entra Setup

1. Open Microsoft Entra admin center.
2. Go to **App registrations**.
3. Create a new application registration.
4. Add the final callback URI above as a **Web** redirect URI.
5. Create a client secret.
6. Copy:
   - application (client) ID
   - client secret
   - optional tenant ID if you do not want to use `common`

## SqlOS Dashboard Setup

Open:

- `http://localhost:5062/sqlos/admin/auth/oidc`

Create a connection with:

- Provider: `Microsoft`
- Display name: whatever you want to show on the login page
- Client ID: the Entra application client ID
- Client secret: the Entra client secret
- Microsoft tenant:
  - leave empty to use `common`
  - or provide your tenant ID / tenant domain
- Allowed callback URIs: start with a placeholder URI, save, then update it to the exact callback URI using the connection ID shown in the connection list

Scopes can be left empty to use the default set:

- `openid`
- `email`
- `profile`

## Test

1. Open `http://localhost:3001/login`
2. Enter the email you plan to use.
3. Click **Continue with Microsoft**.
4. Complete the Microsoft sign-in flow.
5. Confirm you land on `/app`.
6. Confirm the page shows valid session and token data.

## Notes

- SqlOS currently trusts the resolved Microsoft organizational email / `preferred_username` for auto-linking in v1.
- If the email domain matches an enabled org SAML connection, SqlOS starts SAML instead of Microsoft login.
- If the resolved user has more than one active org membership, the v1 flow returns an error instead of showing an org picker.

## Common Failures

- `AADSTS50011` redirect URI mismatch
  Cause: the Entra app registration redirect URI does not exactly match the backend callback URI.

- `The callback URI is not allowed for this OIDC connection.`
  Cause: the SqlOS allowed callback URI list does not match the request callback URI.

- Provider callback returns no usable email
  Cause: the tenant or account did not return `email` or `preferred_username`.
