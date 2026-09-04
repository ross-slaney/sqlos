# Expo mobile example

This Expo Router application demonstrates SqlOS from a native public client: it can complete hosted AuthPage in a secure browser session, or native headless auth in-app via `@sqlos/headless`. Both paths finish authorization code + PKCE through a custom URL scheme, store tokens with Expo SecureStore, refresh access tokens, and call FGA-protected retail APIs.

It is a separate client. The full .NET Aspire AppHost starts the backend but does **not** launch Expo.

## What it demonstrates

- native OAuth browser handoff with `expo-auth-session` (`AuthRequest`, discovery, `exchangeCodeAsync`)
- native headless auth with `createHeadlessFlow` + `flow.start()` from `@sqlos/headless`
- library-owned S256 PKCE (hosted) and start-input PKCE (headless)
- a custom `sqlos-expo://` callback scheme
- access and refresh token storage with `expo-secure-store`
- automatic refresh before protected API calls
- Expo Router public/authenticated route groups
- FGA-filtered chains, stores, and inventory
- local demo switching between user, service-account, and agent subjects

Login and signup screens default to in-app headless login/password/email-code/org/MFA via `useHeadlessAuth`. Any other view SqlOS returns falls back to hosted AuthPage through `startHostedAuth`, which also remains the companion hosted-OIDC path.

[`metro.config.js`](metro.config.js) watches the `file:`-linked package and resolves its `react` / `react-native` peers from this app, so the package's own devDependencies never introduce a second React copy.

## Start the backend

Prerequisites for the backend:

- .NET 9 SDK
- Docker Desktop or another Docker-compatible runtime
- free local ports used by the [full AppHost](../SqlOS.Example.AppHost/README.md)

From the repository root:

```bash
npm ci --prefix packages/headless && npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.Web
npm ci --prefix examples/SqlOS.Example.AngularWeb
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

The AppHost starts the API and SqlOS AuthServer at `http://localhost:5062`. It also starts browser examples, but Expo still needs its own process.

You can instead run the [example API standalone](../SqlOS.Example.Api/README.md#run-the-api-without-aspire) against your own SQL Server.

## Run on iOS or Android

Install dependencies:

```bash
npm ci --prefix packages/headless && npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.ExpoApp
```

For the checked-in custom-scheme callback, use a native build that registers the `sqlos-expo` scheme:

```bash
cd examples/SqlOS.Example.ExpoApp
npx expo run:ios
```

or:

```bash
cd examples/SqlOS.Example.ExpoApp
npx expo run:android
```

These commands require the corresponding local Xcode/iOS Simulator or Android SDK/emulator toolchain. The package also exposes `npm run ios`, `npm run android`, and `npm start` for subsequent development.

Expo Go may generate an `exp://` redirect URI instead of the registered `sqlos-expo://auth-callback` URI. SqlOS will correctly reject an unregistered redirect. Use an app/development build that owns the checked-in scheme, or explicitly register the redirect URI produced by your chosen runtime.

## Host addresses by platform

[`services/config.ts`](services/config.ts) chooses the API host at runtime:

```typescript
const localhost =
  Platform.OS === "android" ? "10.0.2.2" : "localhost";

export const API_URL = `http://${localhost}:5062`;
export const CLIENT_ID = "example-expo";
```

| Runtime | API origin | Why |
| --- | --- | --- |
| iOS Simulator | `http://localhost:5062` | The simulator can reach the Mac host through localhost |
| Android emulator | `http://10.0.2.2:5062` | Android's emulator alias for the host loopback |
| Physical device | not configured | The device cannot use the development machine's loopback |

For a physical device, make the API listen on a network-reachable, development-safe address, update `API_URL`, and keep firewall, issuer/provider callback, and redirect configuration aligned. Do not expose the sample dashboard or HTTP development endpoints to an untrusted network.

## Why the client ID is `example-expo`

The API seeds:

- `example-web` with browser callbacks **and** `sqlos-expo://auth-callback` (hosted AuthPage);
- `example-expo` with `sqlos-expo://auth-callback` and `AllowNativeHeadlessAuth = true`.

The Expo source uses `example-expo` so native `POST /headless/start` is allowed. Keep the callback URI registered exactly (`sqlos-expo://auth-callback`).

Both are public PKCE clients. A mobile application cannot safely hold an OAuth client secret.

## Authentication flow

Headless (default login/signup screens):

1. `flow.start(...)` posts `POST /sqlos/auth/headless/start`. PKCE comes from `createPkceGenerator` with expo-crypto primitives (`services/sqlos-auth.ts`); the package owns the verifier format.
2. In-app screens collect login/password (and org/MFA when returned). Flow errors come from the hook; try/catch is only used around token exchange.
3. When `status === "redirect"` and `authorization` is present, `exchangeHeadlessAuthorization` uses `expo-auth-session` `exchangeCodeAsync`.
4. The app stores access and refresh tokens in SecureStore.

Hosted AuthPage (`startHostedAuth`):

1. `AuthRequest` + discovery start hosted authorize with library-owned PKCE.
2. `promptAsync` opens the system browser to `/sqlos/auth/authorize`.
3. SqlOS renders hosted sign-in/sign-up and redirects to the app's custom scheme.
4. `exchangeCodeAsync` finishes `/token`.

Relevant code:

| File | Responsibility |
| --- | --- |
| [`app.json`](app.json) | Registers the `sqlos-expo` scheme and Expo plugins |
| [`services/config.ts`](services/config.ts) | Platform API origin and public client ID (`example-expo`) |
| [`metro.config.js`](metro.config.js) | Watches the linked `@sqlos/headless` and pins its React peers to this app |
| [`services/sqlos-auth.ts`](services/sqlos-auth.ts) | Issuer, expo-crypto PKCE primitives, redirect URI, hosted `AuthRequest`, and `exchangeCodeAsync` |
| [`components/HeadlessAuthForm.tsx`](components/HeadlessAuthForm.tsx) | Native `useHeadlessAuth` snapshots + in-app screens |
| `app/(auth)/login.tsx` | Headless login (hosted remains in `sqlos-auth.ts`) |
| `app/(auth)/signup.tsx` | Headless signup |
| `app/(auth)/callback.tsx` | Deep-link fallback back to login |
| [`services/auth.ts`](services/auth.ts) | SecureStore session, refresh, local logout, demo override |
| [`services/AuthContext.tsx`](services/AuthContext.tsx) | React authentication state |
| [`services/api.ts`](services/api.ts) | Authenticated retail API calls |
| `app/(app)` | Protected dashboard, chain, store, inventory, and settings screens |

## Explore the app

After authentication:

- the home dashboard summarizes resources visible to the active user;
- Chains and Stores show FGA-filtered collections;
- detail screens read and mutate protected retail resources;
- Settings displays identity/session information and exposes the demo identity switcher.

Switching to a seeded service account or agent replaces the bearer header with the example API's demo header. It exists to make FGA behavior visible and is not a production mobile credential design.

## Type-check and validation

The Expo package has no dedicated build or test script. Type-check it from the repository root:

```bash
npm ci --prefix packages/headless && npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.ExpoApp
npm exec --prefix examples/SqlOS.Example.ExpoApp -- tsc --noEmit -p examples/SqlOS.Example.ExpoApp/tsconfig.json
```

The backend OAuth and FGA behavior is covered by:

```bash
dotnet test examples/SqlOS.Example.IntegrationTests/SqlOS.Example.IntegrationTests.csproj
```

There is no checked-in simulator/device automation for this app, so complete one interactive login on the target runtime after changing callback, networking, or SecureStore behavior.

## Reset and troubleshooting

- Use Settings sign-out to clear SecureStore session and demo overrides.
- The current mobile sign-out is local only; it does not call the API's refresh-token/session revocation endpoint.
- If SqlOS reports an invalid redirect, log `getRedirectUri()` for the current runtime and compare it byte-for-byte with a seeded client redirect URI.
- If Android cannot reach the API, confirm you are using the Android emulator and `10.0.2.2`. A physical Android device needs a reachable host address.
- If iOS cannot reach `localhost`, confirm the API is running on the same Mac and port `5062`.
- Persistent users and grants live in the backend SQL database. Follow the [AppHost reset guidance](../SqlOS.Example.AppHost/README.md#persistent-data-and-reset-behavior) only when that state is disposable.

## Local-sample limitations

- API URL and client ID are source constants rather than build-time environment configuration.
- The documented callback assumes an installed native build with the `sqlos-expo` scheme, not an arbitrary Expo Go redirect.
- HTTP is used for local development. Production mobile auth should use a publicly trusted HTTPS issuer.
- Tokens are stored in SecureStore, but logout does not currently revoke them server-side.
- Demo service-account/agent credentials are intentionally inspectable and must not ship in a real client.
- `npm run web` exists in `package.json`, but browser behavior is not the documented or tested target of this native sample.
