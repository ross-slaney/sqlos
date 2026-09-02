# `@sqlos/headless`

Typed state machine for SqlOS **headless AuthPage**. Your app draws the screens. This package holds the current view model and opaque flow tokens, posts user input to `/sqlos/auth/headless`, and stops at an authorization code.

It is **not** a general OAuth/OIDC client. Do not use it to call `/token`, store refresh tokens, or replace Auth.js, `angular-oauth2-oidc`, `expo-auth-session`, or `AddOpenIdConnect`.

## Install

```bash
npm install @sqlos/headless
```

Pin the npm version to the same SqlOS NuGet release (`4.1.0` and later). In this repository, examples resolve the package from `file:../../packages/headless` so local and CI installs never need npmjs.com.

## Browser (after `/authorize?request=`)

```ts
import { createHeadlessFlow } from "@sqlos/headless";

const flow = createHeadlessFlow({
  issuer: "https://id.example.com/sqlos/auth",
  clientId: "acme-app",
  redirectUri: "https://app.example.com/api/auth/callback/sqlos",
  credentials: "include",
});

await flow.resume(window.location);
await flow.identify({ email });
await flow.password.login({ password });

if (flow.status === "redirect" && flow.redirectUrl) {
  window.location.assign(flow.redirectUrl);
}
```

React:

```ts
import { useHeadlessAuth } from "@sqlos/headless/react";
```

## Native (`POST /headless/start`)

```ts
await flow.start({ scope: "openid profile email offline_access", view: "login" });

if (flow.status === "redirect" && flow.authorization) {
  await exchangeCodeAsync({
    code: flow.authorization.code,
    extraParams: { code_verifier: flow.authorization.codeVerifier },
    redirectUri: flow.authorization.redirectUri,
  });
}
```

Native start may generate PKCE as flow input because SqlOS requires it. The package still does not call `/token`.

## What this package does not do

- Token exchange, refresh, ID-token validation, logout, or device-code polling
- Admin APIs, FGA, or the dashboard
- Writing tokens to `localStorage`
