# App Y — "Sign in with X" via Auth.js

A Next.js App Router app whose only authentication is **Sign in with X**,
where X is the SqlOS OpenID Provider in `../SqlOS.SignInWithX.AppX`.

The whole integration is the provider block in `lib/auth.ts`: Auth.js
(next-auth) discovers X's endpoints from `/.well-known/openid-configuration`,
runs authorization code + PKCE as a public client
(`token_endpoint_auth_method: "none"` — no client secret), validates the ID
token against X's JWKS, and reads `sub`/`name`/`email`/`email_verified` from
UserInfo. App Y has no user database and never sees a password.

Environment (set by the Aspire AppHost; defaults work for bare `npm run dev`):

| Variable | Default | Purpose |
| --- | --- | --- |
| `SQLOS_ISSUER` | `http://localhost:5100/sqlos/auth` | X's issuer; discovery is derived from it |
| `SQLOS_CLIENT_ID` | `app-y` | The client X seeds for this app |
| `NEXTAUTH_URL` | — | `http://localhost:3020` |
| `NEXTAUTH_SECRET` | — | Any local value |

Run through `../SqlOS.SignInWithX.AppHost` (see its README).
