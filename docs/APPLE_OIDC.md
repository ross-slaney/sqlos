# Apple OIDC

This guide configures Sign in with Apple for the shared example app.

## Important Local Development Note

Apple requires a public HTTPS redirect URI. The example callback pattern is:

```text
https://your-public-host/api/v1/auth/oidc/callback/{connectionId}
```

`localhost` is not sufficient for a real Apple test. Use a public HTTPS tunnel or deployed example host.

## Apple Developer Setup

1. Open the Apple Developer portal.
2. Create or select a **Services ID** for the web flow.
3. Enable **Sign in with Apple** for that Services ID.
4. Register the final redirect URI using the SqlOS connection ID.
5. Create or select a **Sign in with Apple key**.
6. Copy:
   - Services ID / client ID
   - Team ID
   - Key ID
   - `.p8` private key contents

## SqlOS Dashboard Setup

Open:

- `http://localhost:5062/sqlos/admin/auth/oidc`

Create a connection with:

- Provider: `Apple`
- Display name: `Apple`
- Client ID: your Services ID
- Apple Team ID
- Apple Key ID
- Apple private key PEM (`.p8`)
- Allowed callback URIs: start with a placeholder, save, then update the callback URI to the final HTTPS callback containing the connection ID

Scopes normally include:

- `name`
- `email`

## Test

1. Run the example stack behind a public HTTPS host.
2. Open the example login page.
3. Enter the Apple-account email.
4. Click **Continue with Apple**.
5. Complete the Apple sign-in flow.
6. Confirm you land on `/app`.

## Notes

- Apple support in SqlOS is web-only in v1.
- Apple may return the name only on first consent.
- Apple uses a backend-generated client secret JWT; you do not paste a normal client secret into SqlOS.

## Common Failures

- Apple redirect URI mismatch
  Cause: the Apple Services ID configuration does not exactly match the final HTTPS callback URI.

- `The callback URI is not allowed for this OIDC connection.`
  Cause: the SqlOS allowed callback URI list does not match the callback URI used by the example backend.

- Missing email from Apple callback
  Cause: the Apple account or callback data did not yield a usable email address for linking/provisioning.
