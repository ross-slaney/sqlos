# `@sqlos/headless`

Typed state machine for a **product-owned login UI** on top of SqlOS AuthPage.

Your app draws the screens. This package holds the current view, opaque flow tokens, and field errors; posts each user action to SqlOS; and stops when SqlOS returns an authorization code (or an external provider URL). Pin the npm version to the same SqlOS NuGet release.

It is **not** a general OAuth/OIDC client. Do not use it to call `/token`, store refresh tokens, or replace Auth.js, `angular-oauth2-oidc`, `expo-auth-session`, or ASP.NET Core `AddOpenIdConnect`. Those libraries still own PKCE startup, callback handling, and token exchange.

## Install

```bash
npm install @sqlos/headless
```

In this repository, examples resolve the package from `file:../../packages/headless` so local and CI installs never need npmjs.com.

## Web app with a custom login page

Your OIDC library starts `/authorize` with S256 PKCE. SqlOS redirects the browser to your page (for example `/auth/authorize?request=…`). This package resumes that saved request, then you render a loop over the current view until SqlOS is ready to leave your page.

```ts
import { createHeadlessFlow } from "@sqlos/headless";

const flow = createHeadlessFlow({
  issuer: "https://id.example.com/sqlos/auth",
  clientId: "acme-app",
  redirectUri: "https://app.example.com/api/auth/callback/sqlos",
  credentials: "include",
});

await flow.resume(window.location);

flow.subscribe(() => {
  if (flow.status === "redirect" && flow.redirectUrl) {
    window.location.assign(flow.redirectUrl);
    return;
  }

  switch (flow.viewModel?.view) {
    case "login":
      // email form → flow.identify({ email })
      break;
    case "password":
      // password form → flow.password.login({ password })
      break;
    case "organization":
    case "mfa":
    case "consent":
      // render the returned view model
      break;
    default:
      break;
  }
});
```

React (and React Native) use the same snapshots so memoized children update without mirroring flow state into `useState`:

```ts
import { useHeadlessAuth } from "@sqlos/headless/react";

const { flow, status, view, viewModel, error, fieldErrors, redirectUrl } =
  useHeadlessAuth({
    issuer,
    clientId,
    redirectUri,
    credentials: "include",
  });

// status === "loading" while an action runs
// error / fieldErrors are the only error channels for normal failures
// when status === "redirect", window.location.assign(redirectUrl)
```

## Native app (React Native / Expo)

On native there is no browser redirect into your page. Call `flow.start()` so SqlOS creates the authorization request, then render the same view loop in-app. When the flow reaches an authorization code, hand that code to your OIDC library for `/token` — this package never calls `/token`.

```ts
import { exchangeCodeAsync } from "expo-auth-session";
import * as Crypto from "expo-crypto";
import { createHeadlessFlow, createPkceGenerator } from "@sqlos/headless";

// Hermes has no Web Crypto `subtle`: inject expo-crypto primitives. The
// package still owns the verifier format (43-char base64url) and S256.
const generatePkce = createPkceGenerator({
  randomBytes: (size) => Crypto.getRandomBytesAsync(size),
  sha256: async (data) =>
    new Uint8Array(await Crypto.digest(Crypto.CryptoDigestAlgorithm.SHA256, data)),
});

const flow = createHeadlessFlow({
  issuer: "https://id.example.com/sqlos/auth",
  clientId: "example-expo",
  redirectUri: "sqlos-expo://auth-callback",
  generatePkce,
});

await flow.start({
  scope: "openid profile email offline_access",
  view: "login",
});

// Render switch (flow.viewModel.view) → identify / password / MFA / …

if (flow.status === "redirect" && flow.authorization) {
  const tokens = await exchangeCodeAsync(
    {
      clientId: "example-expo",
      code: flow.authorization.code,
      redirectUri: flow.authorization.redirectUri,
      extraParams: {
        code_verifier: flow.authorization.codeVerifier ?? "",
      },
    },
    { tokenEndpoint: `${issuer}/token` },
  );
  // Store tokens in your app session — not in this package.
}
```

If `status === "redirect"` and `authorization` is null, SqlOS returned an external provider URL in `redirectUrl`. Open that URL in a system browser; do not treat it as a code callback.

## Errors and status

| `status` | Meaning |
| --- | --- |
| `idle` | No request loaded yet |
| `loading` | An action is in flight |
| `view` | Render `viewModel.view` |
| `redirect` | Leave via `redirectUrl` (and read `authorization` when a code is present) |
| `error` | Read `error` and `fieldErrors` |

Server and validation failures **do not reject**. Actions resolve with the new status, and `error` / `fieldErrors` update on the flow. You do not need try/catch or local error state for the normal path.

Programmer mistakes reject with a `HeadlessProgrammerError` subclass and also set `status === "error"`: `HeadlessFlowBusyError` (a second action while one is in flight — disable inputs while `status === "loading"`), `HeadlessFlowNotLoadedError` (action before `resume`/`start`, or a view without the token the action needs), `HeadlessApiPathMismatchError`, and `HeadlessTokenEndpointError`. Fix the integration when those appear. `resume` and `start` coalesce an identical in-flight call, so React StrictMode's double effect needs no guard.

Subscribe with `flow.subscribe(listener)` outside React, or use `useHeadlessAuth` inside React. Inline `fetch` / `generatePkce` options do not rebuild the hook's flow; only the identity options (issuer, client, redirect URI, base path, credentials) do.

## Beyond the named actions

- `credentialEnabled(viewModel.settings, "email_otp")` applies the hosted AuthPage rule (enabled type **and** runtime configured) so every custom UI shows the same sign-in methods.
- `flow.submit(path, body)` posts to a headless route the SDK does not name yet, through the same status and token bookkeeping, and applies the returned view model or action result.
- `flow.passwordReset` carries the `password.forgot()` result (`maskedEmail`, `expiresAt`, `nextAllowedSendAt`) for resend-cooldown UX.
- Render a fallback for `HeadlessView` values your UI does not draw; the examples send those users to hosted AuthPage.

## Docs

- Guide: [Build your own login and signup UI](https://sqlos.dev/docs/guides/custom-login-ui)
- Package reference: [@sqlos/headless](https://sqlos.dev/docs/reference/headless-js)
- Wire protocol: [Headless Auth](https://sqlos.dev/docs/authserver/headless-auth)

## What this package does not do

- Token exchange, refresh, ID-token validation, logout, or device-code polling
- Admin APIs, FGA, or the dashboard
- Writing tokens to `localStorage`
