# SqlOS AuthPage

AuthPage is SqlOS’s **hosted** login and signup UI.

V1 stays small on purpose:

- `authorization_code` + `refresh_token`
- PKCE public clients
- preregistered clients
- hosted `/authorize`, `/token`, metadata, and JWKS
- branded `/login`, `/signup`, and `/logged-out` pages
- local password, OIDC, and SAML behind the scenes

## Default Endpoints

When `BasePath = "/sqlos/auth"` the library exposes:

- `GET /sqlos/auth/authorize`
- `POST /sqlos/auth/token`
- `GET /sqlos/auth/.well-known/oauth-authorization-server`
- `GET /sqlos/auth/.well-known/jwks.json`
- `GET /sqlos/auth/login`
- `GET /sqlos/auth/signup`
- `GET /sqlos/auth/logged-out`

## Registration

Use one SqlOS registration. Include auth + FGA. Map routes after `Build()`. Your `DbContext` implements both SqlOS interfaces.

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.UseAuthServer(auth =>
    {
        auth.PublicOrigin = "https://app.example.com";
        auth.Issuer = "https://app.example.com/sqlos/auth";
        auth.SeedAuthPage(page =>
        {
            page.PageTitle = "Sign in";
            page.PageSubtitle = "Use the hosted SqlOS auth page for your first-party browser app.";
        });
        auth.SeedBrowserClient("web", "Main Web App", "https://app.example.com/auth/callback");
    });

    options.UseFGA();
});

var app = builder.Build();
app.MapSqlOS();
```

## Presentation mode

The DB stores **hosted** or **headless**. That controls `/authorize`: SqlOS page vs redirect to your app.

In `SeedAuthPage`:

- `page.PresentationMode = "headless"` — your UI (with `UseHeadlessAuthPage` + `BuildUiUrl`).
- `page.PresentationMode = "hosted"` — SqlOS built-in pages.

If you skip `PresentationMode` but set `BuildUiUrl`, seed sets headless. If you skip both, you get hosted.

Dashboard can edit this. Seed reapplies on startup. Keep them in sync.

Headless guide: site docs path `/docs/guides/authserver/headless-auth`.

## Browser Flow

1. App builds PKCE. Sends user to `/authorize`.
2. SqlOS shows AuthPage (hosted mode).
3. Home realm discovery checks email / org domains.
4. SAML org? Redirect to IdP.
5. Else password or OIDC.
6. SqlOS returns a code. App swaps code for tokens.

## Dashboard

Auth admin tabs:

- **Clients** — PKCE apps, redirects, scopes, audience
- **Auth Page** — title, colors, layout, signup toggle, logo
- **Authorization Server** — metadata URLs
- **Providers** — OIDC and SAML

Seeded rows are labeled. You can edit them; seed runs again on restart.

## Notes

- V1 auto-approves consent.
- Default credential is password only.
- CIMD, DCR, OTP, passkeys, revocation, full OIDC metadata: not in V1.
