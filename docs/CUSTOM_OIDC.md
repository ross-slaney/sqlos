# Custom OIDC

This guide configures a custom OpenID Connect provider for the shared example app.

## Supported Modes

SqlOS supports two custom OIDC modes:

- discovery-based configuration
- manual endpoint configuration

Use discovery when the provider publishes a valid OpenID Connect discovery document. Use manual mode only when discovery is incomplete or unavailable.

## Callback URI

The example backend callback is connection-specific:

```text
http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}
```

Create the OIDC connection first, then copy its connection ID from the dashboard and update the allowed callback URI plus the provider redirect URI.

## SqlOS Dashboard Setup

Open:

- `http://localhost:5062/sqlos/admin/auth/oidc`

Create a connection with:

- Provider: `Custom`
- Display name: whatever should appear on the login page
- Client ID
- Client secret
- Allowed callback URIs
- either:
  - `Use discovery` checked with a discovery URL, or
  - `Use discovery` unchecked with issuer, authorization endpoint, token endpoint, and JWKS URI filled in

Optional settings:

- user info endpoint
- client auth method: `ClientSecretPost` or `ClientSecretBasic`
- scopes
- claim mapping JSON
- use user info toggle

## Claim Mapping

When the provider does not use standard OpenID Connect claim names, set a custom claim mapping.

Example:

```json
{
  "SubjectClaim": "custom_sub",
  "EmailClaim": "email_address",
  "EmailVerifiedClaim": "email_verified_flag",
  "DisplayNameClaim": "full_name"
}
```

## Test

1. Create the custom OIDC connection in the dashboard.
2. Update the callback URI after the connection ID is available.
3. Open `http://localhost:3010/login`.
4. Enter an email and click the custom provider button.
5. Complete the provider flow and confirm you land on `/app`.

## Common Failures

- Invalid issuer / token validation failure
  Cause: discovery or manual issuer/JWKS configuration does not match the provider.

- Missing email during callback
  Cause: claim mapping is wrong or the provider does not return an email.

- `The callback URI is not allowed for this OIDC connection.`
  Cause: the provider redirect URI or the SqlOS allowed callback URI list does not exactly match the request callback URI.
