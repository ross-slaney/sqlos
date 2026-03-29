# SqlOS AuthPage

AuthPage is SqlOS's hosted browser UI for sign in, sign up, organization selection, and the rest of the authorization-code flow.

Start here when you want the fastest path for an owned app.

AuthPage now sits inside a larger client-onboarding story:

- **Owned apps** use seeded or manual clients and hosted or headless UI
- **Portable MCP clients** can arrive through `CIMD`
- **Compatibility clients** can arrive through optional `DCR`

The UI is still just one switch:

- **Hosted**: SqlOS renders the browser pages
- **Headless**: your app renders the pages and SqlOS still owns OAuth, PKCE, codes, and tokens

## Default endpoints

When `BasePath = "/sqlos/auth"` the library exposes:

- `GET /sqlos/auth/authorize`
- `POST /sqlos/auth/token`
- `GET /sqlos/auth/.well-known/oauth-authorization-server`
- `GET /sqlos/auth/.well-known/jwks.json`
- `GET /sqlos/auth/login`
- `GET /sqlos/auth/signup`
- `GET /sqlos/auth/logged-out`

When enabled, SqlOS can also expose:

- `POST /sqlos/auth/register` for dynamic client registration
- `GET /sqlos/auth/headless/*` and `POST /sqlos/auth/headless/*` for headless UI flows

## Fastest hosted setup

Use one SqlOS registration. Include auth and FGA on the same `DbContext`. Map routes after `Build()`.

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.PublicOrigin = "https://app.example.com";
    options.AuthServer.Issuer = "https://app.example.com/sqlos/auth";
    options.AuthServer.SeedAuthPage(page =>
    {
        page.PageTitle = "Sign in";
        page.PageSubtitle = "Use the hosted SqlOS auth page for your owned web app.";
    });
    options.AuthServer.SeedOwnedWebApp(
        "web",
        "Main Web App",
        "https://app.example.com/auth/callback");
});

var app = builder.Build();
app.MapSqlOS();
```

That gives you the simplest path:

1. Seed or create a client.
2. Send the browser to `/sqlos/auth/authorize` with PKCE.
3. Let SqlOS render the hosted pages.
4. Exchange the code at `/sqlos/auth/token`.

## Hosted vs headless

`/sqlos/auth/authorize` follows one rule:

- `BuildUiUrl` exists: your app renders the auth UI
- `BuildUiUrl` does not exist: SqlOS renders AuthPage

Read more:

- site docs: `/docs/guides/authserver/hosted-vs-headless`
- site docs: `/docs/guides/authserver/headless-auth`

## Clients and AuthPage

AuthPage is not limited to one client-registration mode.

You can use it with:

- seeded or dashboard-created owned clients
- discovered `CIMD` clients
- registered `DCR` clients

For most teams, the best order is:

1. start with a hosted first-party client
2. switch to headless only when you want custom UI
3. add `CIMD` or `DCR` only when you need portable or compatibility clients

## Dashboard

The auth dashboard now keeps client onboarding in one place:

- **Clients** — owned, discovered, and registered clients with filters, inspect views, and lifecycle actions
- **Auth Page** — title, colors, layout, signup toggle, logo
- **Security** — session and refresh settings
- **Providers** — OIDC and SAML

## Notes

- SqlOS still keeps consent simple by default.
- Password is still the default local credential path.
- `CIMD`, `DCR`, and resource indicators are now supported. Use them when you need portable or compatibility clients, not for the very first hosted setup.
