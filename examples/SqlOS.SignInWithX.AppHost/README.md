# Sign in with X — OpenID Provider federation demo

Two apps, one Aspire host:

| App | Where | Role |
| --- | --- | --- |
| **App X** (`SqlOS.SignInWithX.AppX`) | http://localhost:5100 | A SqlOS host running in OpenID Provider mode: hosted sign-in/sign-up, OIDC discovery, ID tokens, UserInfo, and the consent screen |
| **App Y** (`SqlOS.SignInWithX.AppY`) | http://localhost:3020 | A Next.js app with a **"Sign in with X"** button, built on [Auth.js](https://authjs.dev) (next-auth) — no SqlOS SDK, only the standard OIDC discovery document |

## Run it

```bash
cd examples/SqlOS.SignInWithX.AppY && npm install && cd -
dotnet run --project examples/SqlOS.SignInWithX.AppHost
```

Aspire starts SQL Server (Docker, port 1436), App X, and App Y. Then open
http://localhost:3020 and click **Sign in with X**:

1. X's hosted page appears — create an account (or sign in).
2. Because App Y is a third-party client, X shows its **consent screen** with
   the operator-defined scope display names ("Sign you in", "See your name",
   "See your email address").
3. Approve, and you land back in App Y signed in — Auth.js validated the ID
   token against X's JWKS and read your profile from UserInfo.
4. **Sign out** is federated: it ends App Y's session, then X's, so the next
   sign-in shows a real login page — but no consent screen, because the grant
   is remembered.
5. Clear only App Y's cookies instead and sign in again: the live X session
   plus the remembered grant make it fully silent — no password, no consent.

## What to look at

- `AppX/Program.cs` — the entire identity provider is one `AddSqlOS` call:
  branding, the `app-y` client seed (public PKCE, deliberately
  `IsFirstParty = false`), and `SeedScopeDisplayName` entries for the consent
  screen.
- `AppY/lib/auth.ts` — the entire integration is one standard provider block:
  `wellKnown` discovery, `checks: ["pkce", "state"]`, and
  `token_endpoint_auth_method: "none"` (public client, no secret).
- X's discovery document: http://localhost:5100/sqlos/auth/.well-known/openid-configuration
- X's SqlOS dashboard: http://localhost:5100/sqlos — see App Y's client,
  its OIDC capability and discovery URL, per-user **App grants** (revoke one
  and the next App Y sign-in re-prompts consent), and the scope display-name
  catalog.

## Tests that cover this example

- `examples/SqlOS.SignInWithX.E2eTests` — Playwright Chromium drives the real
  journeys (first sign-up + consent, federated sign-out with the grant
  remembered, silent SSO) against this app host booted on alternate ports
  (5110/3030/1438), so the tests can run while a manual demo is up. CI runs
  them on every PR and on main (`dotnet test examples/SqlOS.SignInWithX.E2eTests`).
- `tests/SqlOS.Conformance` — the OpenID Foundation conformance suite (Config
  OP + Basic OP certification plans) runs against App X in conformance mode,
  also gated in CI.

The consumer-facing walkthrough of this pattern is the
[Sign in with X guide](https://sqlos.dev/docs/guides/sign-in-with-x).
