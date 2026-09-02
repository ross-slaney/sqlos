# Next.js example client

This Next.js 15 application is the most complete browser client in the repository. Hosted sign-in uses Auth.js as a standard OpenID Connect provider against SqlOS discovery — the same shape as Sign in with X App Y, plus `offline_access` for retail API tokens. Headless AuthPage still owns the custom UI state machine; Auth.js finishes the authorization code.

## What you can learn here

- Auth.js / NextAuth as an OIDC public client (`wellKnown`, S256 PKCE, `token_endpoint_auth_method: "none"`)
- SqlOS-hosted sign-in and sign-up pages (`prompt=login`, `view=signup` as authorization parameters)
- SqlOS headless auth when your product owns the full UI
- password, email OTP, phone OTP, provider, organization-selection, password-reset, and MFA states in one headless flow
- refresh-token handling through `POST /sqlos/auth/token`
- bearer calls to application APIs
- FGA-filtered chains, stores, and inventory
- TOTP enrollment and recovery-code display
- delegated enterprise SSO setup portal links
- local demo identity switching

This is a reference client for the [example API](../SqlOS.Example.Api/README.md), not a generic UI component package.

## Recommended: run under Aspire

From the repository root:

```bash
npm ci --prefix packages/headless && npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.Web
npm ci --prefix examples/SqlOS.Example.AngularWeb
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

Open `http://localhost:3010`.

The AppHost starts the API and SQL Server, supplies `NEXT_PUBLIC_API_URL`, sets `NEXTAUTH_URL` to the `3010` origin, provides a local-only NextAuth secret, and seeds both `http://localhost:3010/auth/callback` (legacy/headless bookmarks) and `http://localhost:3010/api/auth/callback/sqlos` (Auth.js).

## Try both auth models

### Hosted AuthPage

Use **Sign in** or **Sign up** from the landing page.

1. Auth.js starts `/sqlos/auth/authorize` with library-owned PKCE and state.
2. SqlOS owns the authentication UI (or redirects to `/auth/authorize` when headless is enabled).
3. SqlOS redirects to `/api/auth/callback/sqlos`.
4. Auth.js exchanges the code and establishes the JWT session.

This path is the smallest browser integration and keeps authentication UI inside SqlOS when headless is off.

### Headless AuthPage

Choose the headless/custom UI entry point on `/auth/authorize`. Auth.js still starts `/authorize`. SqlOS redirects interaction to this application's custom page, then the library finishes `/token`.

The docs walkthrough is [Build your own login and signup UI](https://sqlos.dev/docs/guides/custom-login-ui).

[`sqlos-headless-auth-panel.tsx`](components/sqlos-headless-auth-panel.tsx) uses `useHeadlessAuth` from `@sqlos/headless/react` and renders the returned view. It covers identification, password login/signup/reset, email and phone OTP, provider redirects, organization selection, MFA verification, and first-login TOTP enrollment. On redirect it follows the callback URL; Auth.js finishes `/token`.

The headless signup UI sends `firstName`, `lastName`, and a required `referralSource` custom field. The API's `OnHeadlessSignupAsync` hook validates and persists that application profile data.

## Explore the authenticated application

After sign-in:

- `/retail` summarizes data visible to the active subject;
- `/retail/chains` and `/retail/stores` exercise FGA-filtered reads and permission-checked writes;
- `/retail/account` enrolls and verifies a TOTP authenticator;
- `/retail/sso` requests a delegated SSO setup link for an organization;
- the identity switcher changes between seeded user, service-account, and agent subjects for demonstration.

The switcher exists to make authorization differences visible. It is not a production impersonation design. It uses an **example-API** credentials provider (`example-api`), not the OIDC integration.

## Run the client standalone

First start [`SqlOS.Example.Api`](../SqlOS.Example.Api/README.md) at `http://localhost:5062` with its standalone frontend settings. Then:

```bash
npm ci --prefix packages/headless && npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.Web
NEXT_PUBLIC_API_URL=http://localhost:5062 \
NEXTAUTH_URL=http://localhost:3000 \
NEXTAUTH_SECRET=replace-for-local-development \
npm run dev --prefix examples/SqlOS.Example.Web
```

Open `http://localhost:3000`.

Standalone Next.js defaults to port `3000`, while the AppHost runs it on `3010`. The API configuration must use the matching origin and exact Auth.js callback:

| Mode | Browser origin | Auth.js callback |
| --- | --- | --- |
| AppHost | `http://localhost:3010` | `http://localhost:3010/api/auth/callback/sqlos` |
| Standalone defaults | `http://localhost:3000` | `http://localhost:3000/api/auth/callback/sqlos` |

Do not start a `3000` client against an API configured only for the `3010` callback; SqlOS will correctly reject the redirect URI.

## Environment variables

| Variable | Fallback in source | Purpose |
| --- | --- | --- |
| `NEXT_PUBLIC_API_URL` | `http://localhost:5062` | Example API and SqlOS AuthServer origin |
| `NEXT_PUBLIC_SQL_OS_CLIENT_ID` | `example-web` | OAuth public client ID |
| `NEXTAUTH_URL` | NextAuth runtime behavior | Canonical application URL; AppHost sets it explicitly |
| `NEXTAUTH_SECRET` | none | Encrypts/signs NextAuth state and session material; required outside ephemeral development |

Variables prefixed with `NEXT_PUBLIC_` are exposed to browser code. Never put client secrets or provider credentials in them. `example-web` is a public PKCE client and intentionally has no client secret.

## Code map

| File | Responsibility |
| --- | --- |
| [`lib/auth.ts`](lib/auth.ts) | Auth.js OIDC provider, JWT/session callbacks, token refresh |
| [`lib/sqlos-config.ts`](lib/sqlos-config.ts) | Issuer, client ID, and post-login path helpers (no PKCE) |
| [`components/sqlos-hosted-sign-in.tsx`](components/sqlos-hosted-sign-in.tsx) | Starts `signIn("sqlos")` with `prompt` / `view` |
| [`components/sqlos-headless-auth-panel.tsx`](components/sqlos-headless-auth-panel.tsx) | Custom UI on `useHeadlessAuth` from `@sqlos/headless/react` |
| [`app/api/auth/[...nextauth]/route.ts`](app/api/auth/[...nextauth]/route.ts) | Auth.js route, including `/api/auth/callback/sqlos` |
| [`middleware.ts`](middleware.ts) | Protects retail routes |
| [`lib/api.ts`](lib/api.ts) | Authenticated example API requests |
| [`lib/sqlos-signout.ts`](lib/sqlos-signout.ts) | SqlOS session/token sign-out integration |
| [`app/retail/page.tsx`](app/retail/page.tsx) | Representative protected retail page; sibling routes add MFA and SSO portal screens |
| [`components/user-switcher.tsx`](components/user-switcher.tsx) | Local demo subject switching |

## Token and session flow

Auth.js owns PKCE, state, the authorization-code exchange, and in-flight refresh coalescing in the JWT callback. Refresh calls `POST /sqlos/auth/token` with `grant_type=refresh_token`. The demo identity switcher uses a separately labeled `example-api` credentials provider and is not the OIDC path.

These choices make the flow easy to inspect. Review your own threat model, cookie policy, server-side session requirements, and token-retention policy before adopting them.

## Build and validation

```bash
npm ci --prefix packages/headless && npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.Web
npm run build --prefix examples/SqlOS.Example.Web
```

There is no checked-in browser test suite for this client. The build verifies TypeScript and Next.js bundling. OAuth, headless auth, session, SSO, MFA, and FGA backend behavior is exercised by:

```bash
dotnet test examples/SqlOS.Example.IntegrationTests/SqlOS.Example.IntegrationTests.csproj
```

## Reset and troubleshooting

- To clear only the client session, sign out. If a flow was interrupted, clear site data for the Next.js origin to remove NextAuth cookies.
- To clear users, grants, or retail data, follow the [AppHost database reset guidance](../SqlOS.Example.AppHost/README.md#persistent-data-and-reset-behavior).
- If callback validation fails, confirm that the browser origin, `NEXTAUTH_URL`, and seeded Auth.js redirect URI all use the same port.
- If the headless page cannot call the API, confirm `NEXT_PUBLIC_API_URL` and the API's `ExampleFrontend:Origin` agree.
- If email or phone OTP is visible but delivery fails, configure the required provider on the .NET AppHost.

## Local-sample limitations

- The AppHost's `NEXTAUTH_SECRET` is committed orchestration code for localhost only. Use a strong secret from a secure store in deployed environments.
- The sample uses HTTP on localhost. Use HTTPS for deployed issuer, callback, and cookie origins.
- Seeded demo identity switching and inspectable service/agent credentials are for authorization exploration only.
- Provider-backed features require real external credentials.
