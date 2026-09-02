# Angular example client

This Angular 19 application shows how a single-page app integrates with SqlOS through `angular-oauth2-oidc`: discovery, authorization code + PKCE, refresh, and FGA-protected retail APIs. It also demonstrates headless AuthPage, where the OIDC library still starts `/authorize` and finishes `/token`.

## What it demonstrates

- standalone Angular components and route guards
- hosted SqlOS sign-in and sign-up via `angular-oauth2-oidc`
- headless password, email OTP, signup, provider, and organization-selection states
- library-owned PKCE, discovery, and refresh
- bearer API requests
- FGA-filtered retail navigation and CRUD screens
- local demo switching between user, service-account, and agent identities

The Angular client is intentionally independent of the Next.js implementation so you can compare framework-specific plumbing without changing the .NET host.

## Recommended: run under Aspire

From the repository root:

```bash
npm ci --prefix packages/headless && npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.Web
npm ci --prefix examples/SqlOS.Example.AngularWeb
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

Open `http://localhost:4200`.

The AppHost starts the example API at `http://localhost:5062` and waits for it before starting Angular. The API seeds public client `example-angular` with callback `http://localhost:4200/auth/callback`.

## Try the authentication flows

### Hosted AuthPage

Use the hosted sign-in or sign-up action.

1. `angular-oauth2-oidc` loads discovery and starts the code + PKCE flow.
2. The browser redirects to `http://localhost:5062/sqlos/auth/authorize`.
3. SqlOS renders and processes the auth page (or the custom `/auth/authorize` UI when headless is enabled).
4. The browser returns to `/auth/callback`.
5. The library exchanges the code; `AuthService` copies the tokens into the sample session.

### Headless AuthPage

Start the custom/headless path. The same OIDC library starts `/authorize`. SqlOS directs interaction to Angular's `/auth/authorize` route, then the library finishes the code at `/auth/callback`.

[`AuthAuthorizeComponent`](src/app/pages/auth-authorize/auth-authorize.component.ts) uses `createHeadlessFlow` from `@sqlos/headless` and renders password, email-code, signup, organization-selection, and provider states.

Headless signup sends first name, last name, and a required referral source to the API's application hook.

The Angular headless UI is deliberately smaller than the Next.js reference: it does not currently implement phone OTP, password reset, or MFA screens.

## Explore authorization

Protected routes live below `/retail`:

| Route | Purpose |
| --- | --- |
| `/retail` | Dashboard based on resources visible to the active subject |
| `/retail/chains` | List and manage retail chains |
| `/retail/chains/:chainId` | Chain detail and related locations |
| `/retail/stores` | FGA-filtered store list |
| `/retail/locations/:locationId` | Store detail and inventory |

The sidebar identity switcher uses example-only API keys/agent tokens so you can see how the same API response changes with FGA grants. Do not use that switcher as a production impersonation model.

Unlike the Next.js sample, Angular does not currently include the delegated SSO portal or account/MFA pages.

## Run standalone

Start [`SqlOS.Example.Api`](../SqlOS.Example.Api/README.md) at `http://localhost:5062`, then:

```bash
npm ci --prefix packages/headless && npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.AngularWeb
npm run dev --prefix examples/SqlOS.Example.AngularWeb
```

Open `http://localhost:4200`.

The development script binds to `0.0.0.0` and uses port `4200` unless `PORT` is supplied. The OAuth registration is fixed to `http://localhost:4200/auth/callback`, so changing `PORT` also requires changing the seeded redirect URI.

## Configuration

The client configuration is checked into [`src/app/environments/environment.ts`](src/app/environments/environment.ts):

```typescript
export const environment = {
  apiUrl: 'http://localhost:5062',
  clientId: 'example-angular',
};
```

The current build does not read an AppHost-provided API URL. To target another host, replace these values through your Angular environment/build configuration and update the SqlOS client's exact redirect URI. Never add a client secret: this browser application is a public PKCE client.

The API allows credentialed CORS requests from `http://localhost:4200` in addition to its configured primary frontend origin. Those credentials are used by the headless AuthPage session; application API requests use bearer or example demo headers.

## Code map

| File | Responsibility |
| --- | --- |
| [`src/app/app.routes.ts`](src/app/app.routes.ts) | Public auth routes and guarded retail routes |
| [`src/app/environments/environment.ts`](src/app/environments/environment.ts) | API origin and public client ID |
| [`src/app/auth.config.ts`](src/app/auth.config.ts) | `angular-oauth2-oidc` issuer, redirect URI, and scopes |
| [`src/app/app.config.ts`](src/app/app.config.ts) | Discovery + `tryLogin` on startup |
| [`src/app/services/auth.service.ts`](src/app/services/auth.service.ts) | OIDC session sync, library refresh, sign-out, demo overrides |
| [`src/app/services/api.service.ts`](src/app/services/api.service.ts) | Protected API requests |
| [`src/app/guards/auth.guard.ts`](src/app/guards/auth.guard.ts) | Retail route protection |
| [`src/app/pages/auth-callback/auth-callback.component.ts`](src/app/pages/auth-callback/auth-callback.component.ts) | Hosted OAuth callback completion |
| [`src/app/pages/auth-authorize/auth-authorize.component.ts`](src/app/pages/auth-authorize/auth-authorize.component.ts) | Custom headless auth UI via `createHeadlessFlow` |
| [`src/app/pages/retail/dashboard/dashboard.component.ts`](src/app/pages/retail/dashboard/dashboard.component.ts) | Representative protected retail page |
| [`src/app/components/user-switcher/user-switcher.component.ts`](src/app/components/user-switcher/user-switcher.component.ts) | Local demo identity selection |

## Session behavior

`angular-oauth2-oidc` stores the OIDC tokens. [`AuthService`](src/app/services/auth.service.ts) copies them into a sample session for the retail UI, refreshes through the library, and coalesces concurrent refresh work. Demo identity switching is labeled separately and is not the OIDC path. Sign-out asks the example API to revoke the refresh/session identifiers, clears local state, and visits the SqlOS logout endpoint.

This makes protocol behavior easy to inspect, but it is still sample storage. Choose browser or server-side session handling based on your application's XSS model, cookie strategy, and token-retention requirements.

## Build and test

```bash
npm ci --prefix packages/headless && npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.AngularWeb
npm run build --prefix examples/SqlOS.Example.AngularWeb
```

The repository does not currently contain Angular `*.spec.ts` files or end-to-end browser automation. The `npm test` script is the Angular/Karma harness, but it is not additional checked-in behavior coverage.

The backend auth, OAuth, session, email OTP, and FGA behavior is covered by:

```bash
dotnet test examples/SqlOS.Example.IntegrationTests/SqlOS.Example.IntegrationTests.csproj
```

## Reset and troubleshooting

- Sign out to revoke the current sample session and clear Angular storage. If a callback was interrupted, clear site data for `localhost:4200`.
- An `invalid_redirect_uri` error usually means the browser is not actually on port `4200` or the API seed changed.
- A headless request failing CORS usually means the Angular origin or API origin no longer matches the .NET configuration.
- Email-code delivery requires the API/AppHost's Azure Communication Services settings.
- Persistent users, grants, and retail data live in SQL Server. Use the [AppHost reset guidance](../SqlOS.Example.AppHost/README.md#persistent-data-and-reset-behavior) only when that state is disposable.

## Local-sample limitations

- The development command uses Angular's `--disable-host-check` flag. Keep that convenience local; use normal production host validation when deploying.
- URLs and the client ID are source constants rather than runtime environment injection.
- HTTP is used on localhost. Production issuer and redirect origins should use HTTPS.
- Tokens and demo override credentials are visible to browser code.
- Phone OTP, password reset, MFA, SSO portal, and frontend automated tests are not implemented in this Angular client.
