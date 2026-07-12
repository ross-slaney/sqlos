# Next.js example client

This Next.js 15 application is the most complete browser client in the repository. It demonstrates two ways to use SqlOS authentication, hands the resulting tokens to a NextAuth session, and uses the access token against FGA-protected retail APIs.

## What you can learn here

- OAuth authorization code flow with PKCE and state validation
- SqlOS-hosted sign-in and sign-up pages
- SqlOS headless auth when your product owns the full UI
- password, email OTP, phone OTP, provider, organization-selection, password-reset, and MFA states in one headless flow
- refresh-token handling inside a NextAuth JWT-backed session
- bearer calls to application APIs
- FGA-filtered chains, stores, and inventory
- TOTP enrollment and recovery-code display
- delegated enterprise SSO setup portal links
- local demo identity switching

This is a reference client for the [example API](../SqlOS.Example.Api/README.md), not a generic UI component package.

## Recommended: run under Aspire

From the repository root:

```bash
npm ci --prefix examples/SqlOS.Example.Web
npm ci --prefix examples/SqlOS.Example.AngularWeb
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

Open `http://localhost:3010`.

The AppHost starts the API and SQL Server, supplies `NEXT_PUBLIC_API_URL`, sets `NEXTAUTH_URL` to the `3010` origin, provides a local-only NextAuth secret, and seeds `http://localhost:3010/auth/callback` for `example-web`.

## Try both auth models

### Hosted AuthPage

Use **Sign in** or **Sign up** from the landing page.

1. The browser creates a PKCE verifier/challenge and random state.
2. It redirects to `/sqlos/auth/authorize` on the .NET API.
3. SqlOS owns the authentication UI and protocol.
4. SqlOS redirects back to `/auth/callback` with an authorization code.
5. The client verifies state, exchanges the code with the PKCE verifier, and establishes its NextAuth session.

This path is the smallest browser integration and keeps authentication UI inside SqlOS.

### Headless AuthPage

Choose the headless/custom UI entry point on the landing page. SqlOS still owns the authorization request and its server-side state, but redirects the interaction to `/auth/authorize` in this application.

[`sqlos-headless-auth-panel.tsx`](components/sqlos-headless-auth-panel.tsx) renders the returned view model and posts user actions to the SqlOS headless endpoints. It covers identification, password login/signup/reset, email and phone OTP, provider redirects, organization selection, MFA verification, and first-login TOTP enrollment.

The headless signup UI sends `firstName`, `lastName`, and a required `referralSource` custom field. The API's `OnHeadlessSignupAsync` hook validates and persists that application profile data.

## Explore the authenticated application

After sign-in:

- `/retail` summarizes data visible to the active subject;
- `/retail/chains` and `/retail/stores` exercise FGA-filtered reads and permission-checked writes;
- `/retail/account` enrolls and verifies a TOTP authenticator;
- `/retail/sso` requests a delegated SSO setup link for an organization;
- the identity switcher changes between seeded user, service-account, and agent subjects for demonstration.

The switcher exists to make authorization differences visible. It is not a production impersonation design.

## Run the client standalone

First start [`SqlOS.Example.Api`](../SqlOS.Example.Api/README.md) at `http://localhost:5062` with its standalone frontend settings. Then:

```bash
npm ci --prefix examples/SqlOS.Example.Web
NEXT_PUBLIC_API_URL=http://localhost:5062 \
NEXTAUTH_URL=http://localhost:3000 \
NEXTAUTH_SECRET=replace-for-local-development \
npm run dev --prefix examples/SqlOS.Example.Web
```

Open `http://localhost:3000`.

Standalone Next.js defaults to port `3000`, while the AppHost runs it on `3010`. The API configuration must use the matching origin and exact callback:

| Mode | Browser origin | Registered callback |
| --- | --- | --- |
| AppHost | `http://localhost:3010` | `http://localhost:3010/auth/callback` |
| Standalone defaults | `http://localhost:3000` | `http://localhost:3000/auth/callback` |

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
| [`lib/sqlos-auth.ts`](lib/sqlos-auth.ts) | PKCE/state generation, callback URI, browser flow storage |
| [`components/sqlos-auth-redirect.tsx`](components/sqlos-auth-redirect.tsx) | Starts hosted authorization |
| [`components/sqlos-auth-callback-panel.tsx`](components/sqlos-auth-callback-panel.tsx) | Validates state, exchanges the code, creates the app session |
| [`lib/sqlos-headless.ts`](lib/sqlos-headless.ts) | Typed calls to the SqlOS headless AuthPage API |
| [`components/sqlos-headless-auth-panel.tsx`](components/sqlos-headless-auth-panel.tsx) | Complete custom authentication UI state machine |
| [`lib/auth.ts`](lib/auth.ts) | NextAuth provider, JWT/session callbacks, token refresh |
| [`app/api/auth/[...nextauth]/route.ts`](app/api/auth/[...nextauth]/route.ts) | NextAuth route |
| [`middleware.ts`](middleware.ts) | Protects retail routes |
| [`lib/api.ts`](lib/api.ts) | Authenticated example API requests |
| [`lib/sqlos-signout.ts`](lib/sqlos-signout.ts) | SqlOS session/token sign-out integration |
| [`app/retail/page.tsx`](app/retail/page.tsx) | Representative protected retail page; sibling routes add MFA and SSO portal screens |
| [`components/user-switcher.tsx`](components/user-switcher.tsx) | Local demo subject switching |

## Token and session flow

The OAuth verifier, state, requested view, and post-login path live temporarily in browser `sessionStorage` so a callback in the same tab can complete the PKCE flow. After code exchange, the app gives the token response to a NextAuth credentials provider and uses NextAuth's JWT-backed session callbacks.

Before protected API calls, session logic refreshes an expired access token with its refresh token. Concurrent refreshes for the same token are coalesced. A failed refresh clears usable token data so the UI can return the user to sign-in.

These choices make the flow easy to inspect. Review your own threat model, cookie policy, server-side session requirements, and token-retention policy before adopting them.

## Build and validation

```bash
npm ci --prefix examples/SqlOS.Example.Web
npm run build --prefix examples/SqlOS.Example.Web
```

There is no checked-in browser test suite for this client. The build verifies TypeScript and Next.js bundling. OAuth, headless auth, session, SSO, MFA, and FGA backend behavior is exercised by:

```bash
dotnet test examples/SqlOS.Example.IntegrationTests/SqlOS.Example.IntegrationTests.csproj
```

## Reset and troubleshooting

- To clear only the client session, sign out. If a flow was interrupted, clear site data for the Next.js origin to remove its temporary PKCE state and NextAuth cookies.
- To clear users, grants, or retail data, follow the [AppHost database reset guidance](../SqlOS.Example.AppHost/README.md#persistent-data-and-reset-behavior).
- If callback validation fails, confirm that the browser origin, `NEXTAUTH_URL`, and seeded redirect URI all use the same port.
- If the headless page cannot call the API, confirm `NEXT_PUBLIC_API_URL` and the API's `ExampleFrontend:Origin` agree.
- If email or phone OTP is visible but delivery fails, configure the required provider on the .NET AppHost.

## Local-sample limitations

- The AppHost's `NEXTAUTH_SECRET` is committed orchestration code for localhost only. Use a strong secret from a secure store in deployed environments.
- The sample uses HTTP on localhost. Use HTTPS for deployed issuer, callback, and cookie origins.
- OAuth flow state is scoped to the initiating browser tab through `sessionStorage`.
- Seeded demo identity switching and inspectable service/agent credentials are for authorization exploration only.
- Provider-backed features require real external credentials.
