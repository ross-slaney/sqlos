# SqlOS AuthPage

`SqlOS AuthPage` is the branded browser surface for the embedded `AuthServer`.

V1 is intentionally narrow:

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

SqlOS is integrated as one product: register auth and FGA together, map routes after `Build()`, and implement both `ISqlOSAuthServerDbContext` and `ISqlOSFgaDbContext` on your `DbContext`.

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

## Browser Flow

1. Your browser app generates PKCE and redirects to `/authorize`.
2. SqlOS renders the AuthPage.
3. Home realm discovery checks organization primary domains.
4. Matching users are redirected to SAML.
5. Everyone else can use password or an enabled OIDC provider.
6. SqlOS issues the authorization code and later the access/refresh tokens.

## Dashboard

The auth dashboard now includes:

- `Clients`: preregistered PKCE apps, redirect URIs, scopes, audiences
- `Auth Page`: title, subtitle, colors, layout, signup toggle, base64 logo upload
- `Authorization Server`: standards-facing metadata and URL references
- `Providers`: OIDC and SAML connection management

Startup-seeded clients and auth-page settings are marked in the dashboard. They remain editable for inspection, but startup code reapplies them on restart.

## Notes

- V1 auto-approves consent.
- Password is the only credential type enabled out of the box.
- CIMD, DCR, OTP, passkeys, revocation, and full OIDC provider metadata are intentionally deferred.
