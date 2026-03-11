# Google OIDC

This guide configures Google sign-in for the shared example app.

## Redirect URI

The example backend callback is connection-specific:

```text
http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}
```

Create the OIDC connection in SqlOS first, then copy its connection ID from the dashboard and register the final callback URI in Google Cloud.

## Google Cloud Setup

1. Open Google Cloud Console.
2. Create or select a project.
3. Configure the OAuth consent screen.
4. Create an **OAuth client ID** for a web application.
5. Add the final callback URI from SqlOS.
6. Copy:
   - client ID
   - client secret

## SqlOS Dashboard Setup

Open:

- `http://localhost:5062/sqlos/admin/auth/oidc`

Create a connection with:

- Provider: `Google`
- Display name: whatever you want to show on the login page
- Client ID: the Google OAuth client ID
- Client secret: the Google OAuth client secret
- Allowed callback URIs: start with a placeholder URI, save, then update it to the exact callback URI using the connection ID shown in the connection list

Scopes can be left empty to use the default set:

- `openid`
- `email`
- `profile`

## Test

1. Open `http://localhost:3001/login`
2. Enter the email you plan to use.
3. Click **Continue with Google**.
4. Complete the Google sign-in flow.
5. Confirm you land on `/app`.
6. Confirm the page shows valid session and token data.

## Notes

- Google auto-linking requires a verified Google email.
- If the email domain matches an enabled org SAML connection, SqlOS will start SSO instead of Google login.
- If the user has more than one active org membership, the current v1 OIDC flow returns an error instead of showing an org picker.

## Common Failures

- `The callback URI is not allowed for this OIDC connection.`
  Cause: the SqlOS allowed callback URI list does not match the request callback URI.

- `redirect_uri_mismatch`
  Cause: Google Cloud does not have the exact backend callback URI registered.

- `The provider email must be verified before it can be linked.`
  Cause: Google returned an unverified email address.
