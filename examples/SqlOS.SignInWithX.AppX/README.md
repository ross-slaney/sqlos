# App X — the identity provider

The "X" in "Sign in with X": a SqlOS host whose only job is identity. It has
no application tables and no API — `Program.cs` is a single `AddSqlOS` call
that brands the hosted pages, seeds the `app-y` relying-party client
(public PKCE, third-party, `openid profile email`), and names the scopes for
the consent screen with `SeedScopeDisplayName`.

Because OpenID Provider mode is on by default, that is everything required
for any standard OIDC library to federate against this app: discovery at
`/sqlos/auth/.well-known/openid-configuration`, RS256 ID tokens verifiable
against `/sqlos/auth/.well-known/jwks.json`, and `/sqlos/auth/userinfo`.

Run through `../SqlOS.SignInWithX.AppHost` (see its README).
