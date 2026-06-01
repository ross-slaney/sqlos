# SqlOS

**Embedded auth server and fine-grained authorization for .NET — one NuGet package, zero external services.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/SqlOS)](https://www.nuget.org/packages/SqlOS)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple)](https://dotnet.microsoft.com)

SqlOS adds auth and fine-grained authorization to your .NET app. OAuth, login UI, orgs, SAML, OIDC, and FGA live in **your** SQL Server. An embedded admin UI ships with the package.

Think **WorkOS / AuthKit**, but **self-hosted** and **your database**.

## Why SqlOS?

| External auth services                  | SqlOS                                                  |
| --------------------------------------- | ------------------------------------------------------ |
| Data lives on someone else's servers    | Data lives in **your** SQL Server                      |
| Per-MAU pricing that scales against you | **MIT-licensed**, no usage fees                        |
| Another vendor dependency to manage     | **Single NuGet package**, ships with your app          |
| Limited customization of login flows    | **Full control** — branded AuthPage, custom OIDC, SAML |

## Features

### AuthServer

- **OAuth 2.0 with PKCE** — `/sqlos/auth/authorize`, `/sqlos/auth/token`, metadata, and JWKS
- **Branded AuthPage** — hosted `/sqlos/auth/login`, `/sqlos/auth/signup`, and `/sqlos/auth/logged-out`
- **Client Onboarding Modes** — seeded/manual owned apps, `CIMD` discovered clients, and optional `DCR` compatibility clients
- **Resource Indicators** — bind `resource` end to end and mint audience-aware access tokens
- **Organizations & Users** — multi-tenant user management with memberships and roles
- **Password Credentials** — secure local authentication with session management
- **Social Login** — Google, Microsoft, Apple, and any custom OIDC provider
- **SAML SSO** — enterprise single sign-on with home realm discovery by email domain
- **Sessions & Refresh Tokens** — full lifecycle management with revocation
- **Signing Key Rotation** — automatic RS256 key rotation with configurable intervals
- **Email OTP** — passwordless sign-in with Azure Communication Services Email
- **Audit Logging** — track authentication events across your system

### FGA (Fine-Grained Authorization)

- **Hierarchical Resource Authorization** — define resource types, permissions, and roles
- **Access Grants** — assign permissions to users, user groups, and service accounts
- **EF Core Query Filters** — filter authorized resources directly in LINQ queries
- **Access Tester** — verify authorization decisions through the dashboard

### Embedded Admin Dashboard

- **Auth Admin** — manage organizations, users, clients, OIDC/SAML connections, security settings, sessions, and audit events
- **FGA Admin** — manage resources, grants, roles, permissions, and test access decisions
- **Password-Protected** — optional password auth mode with dashboard-specific throttling

## Quick Start

Full walkthrough: **[sqlos.dev/docs/getting-started](https://sqlos.dev/docs/getting-started)**

1. **Add the package**

   ```bash
   dotnet add package SqlOS
   ```

2. **Use SQL Server for your EF `DbContext`**  
   SqlOS uses the same database as your context. Point EF at SQL Server like any other app.

3. **Wire your `DbContext`**  
   Add the two SqlOS interfaces. Add the FGA `IsResourceAccessible` query. Call `UseSqlOS` in `OnModelCreating`:

   ```csharp
   public sealed class AppDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
   {
       public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
           string subjectId,
           string permissionKey)
           => FromExpression(() => IsResourceAccessible(subjectId, permissionKey));

       protected override void OnModelCreating(ModelBuilder modelBuilder)
       {
           base.OnModelCreating(modelBuilder);
           modelBuilder.UseSqlOS(GetType());
       }
   }
   ```

4. **Register SqlOS on the host**

   ```csharp
   builder.AddSqlOS<AppDbContext>(options =>
   {
       options.AuthServer.SeedOwnedWebApp(
           "todo-web",
           "Todo Web App",
           "https://app.example.com/auth/callback");
   });
   ```

5. **Map routes after `Build()`**

   ```csharp
   var app = builder.Build();
   app.MapSqlOS();
   ```

On startup, SqlOS updates its own schema. Default URLs: admin at `/sqlos`, OAuth at `/sqlos/auth`. Change the prefix with `DashboardBasePath` if you need to.

## Dashboard Access

Protect the dashboard in production with a password:

```csharp
options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
options.Dashboard.Password = builder.Configuration["SqlOS:Dashboard:Password"];
options.Dashboard.LoginThrottling.MaxFailuresPerIp = 5;
options.Dashboard.LoginThrottling.MaxGlobalFailures = 25;
options.Dashboard.LoginThrottling.Window = TimeSpan.FromMinutes(5);
options.Dashboard.LoginThrottling.LockoutDuration = TimeSpan.FromMinutes(5);
```

Or via environment variables:

```bash
SqlOS__Dashboard__AuthMode=Password
SqlOS__Dashboard__Password=<strong-password>
```

`/sqlos` and `/sqlos/admin` are administrative surfaces. Do not expose them directly to the public internet unless they are behind HTTPS, trusted reverse-proxy or edge rate limiting, and a stronger admin access strategy such as a private network, VPN, identity-aware proxy, or admin SSO/MFA. The built-in password mode includes per-IP throttling, global backoff, temporary lockout, and audit events for login, lockout, rate-limit rejection, and logout, but it should not be the only control for a public admin endpoint.

## Email OTP with Azure Communication Services

SqlOS includes an Azure Communication Services Email sender for passwordless email-code login. Provision ACS Email with the helper script:

```bash
AZURE_SUBSCRIPTION_ID=<subscription-id> \
AZURE_RESOURCE_GROUP=<resource-group> \
AZURE_DNS_ZONE_NAME=example.com \
AZURE_DNS_ZONE_RESOURCE_GROUP=<dns-zone-resource-group> \
ACS_EMAIL_DOMAIN=example.com \
ACS_EMAIL_SENDER_USERNAME=no-reply \
ACS_EMAIL_SENDER_DISPLAY_NAME="Example" \
./scripts/azure/setup-acs-email.sh --apply-dns --yes
```

`AZURE_DNS_ZONE_NAME` is the DNS zone apex (for example `example.com`), not a resource group. `AZURE_DNS_ZONE_RESOURCE_GROUP` is the resource group that contains that Azure DNS zone; it defaults to `AZURE_RESOURCE_GROUP` when omitted. Set it when the zone is in a different group than the ACS Email resources, or drop that line from the command when they match.

The script creates an ACS Email Service, ACS Communication Service, customer-managed email domain, sender username, and optional Azure DNS verification records. If your DNS is not hosted in Azure, omit `--apply-dns`; the script prints the records to create manually.

Store the connection string securely, then configure SqlOS:

```bash
SqlOS__EmailOtp__AzureCommunicationServicesConnectionString=<acs-connection-string>
SqlOS__EmailOtp__FromAddress=no-reply@example.com
```

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.ConfigureEmailOtp(email =>
    {
        email.AzureCommunicationServicesConnectionString =
            builder.Configuration["SqlOS:EmailOtp:AzureCommunicationServicesConnectionString"];
        email.FromAddress = builder.Configuration["SqlOS:EmailOtp:FromAddress"];
        email.ApplicationName = "Example";
    });

    options.AuthServer.SeedAuthPage(page =>
    {
        page.EnabledCredentialTypes = ["password", "email_otp"];
    });
});
```

Hosted AuthPage, headless browser flows, invite acceptance, and backend SDK usage all use the same OTP primitives. A backend can run a passwordless signup without adding its own REST API surface:

```csharp
var start = await sqlosAuth.RequestEmailOtpSignupAsync(
    new SqlOSEmailOtpSignupStartRequest(
        DisplayName: "Jane Doe",
        Email: "jane@example.com",
        ClientId: "example-web",
        OrganizationName: "Example Co",
        OrganizationId: null,
        CustomFields: null),
    httpContext);

var login = await sqlosAuth.VerifyEmailOtpSignupAsync(
    new SqlOSEmailOtpSignupVerifyRequest(
        start.SignupToken,
        start.ChallengeToken,
        code),
    httpContext);
```

Headless browser clients use `/sqlos/auth/headless/email-otp/start`, `/sqlos/auth/headless/email-otp/verify`, `/sqlos/auth/headless/signup/email-otp/start`, and `/sqlos/auth/headless/signup/email-otp/verify`.

Run `./scripts/azure/setup-acs-email.sh --help` for dry-run, DNS, and connection-string options.

## Invite by Email

SqlOS can send one-time organization invitations that are bound to the invited email address. The hosted accept page is:

```text
GET /sqlos/auth/invitations/accept?token=...
```

Admins can create, resend, revoke, and copy invite links from the organization **Invitations** tab in the Auth dashboard. Backend code can use the SDK facade:

```csharp
var invite = await sqlosAuth.CreateEmailInvitationAsync(
    new SqlOSCreateEmailInvitationRequest(
        OrganizationId: organizationId,
        Email: "jane@example.com",
        Role: "member",
        ClientId: "web",
        RedirectUri: "https://app.example.com/auth/callback"),
    httpContext);
```

Invite acceptance works with Email OTP, password login/signup when enabled, and trusted SSO. See [Email Invitations](docs/INVITATIONS.md).

## Todo Sample

If your goal is:

> "I want SqlOS to work with hosted auth, resource metadata, and MCP-style public clients."

Start with:

```bash
dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
```

That sample stays intentionally narrow:

- hosted AuthPage first
- passwordless email-code sign in/sign up when `TodoSample__EnableEmailOtp=true`
- protected-resource metadata
- audience-aware token validation
- local preregistration with `todo-local`
- public-client onboarding with `CIMD` and optional `DCR`

Read more:

- [Todo sample README](examples/SqlOS.Todo.Api/README.md)
- [Todo sample guide](web/content/docs/authserver/todo-sample.mdx)

## Example App

The repo includes a full working example powered by .NET Aspire:

```bash
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

That starts SQL Server, the sample API, the Todo sample, and the web frontends in one stack. Use it when you want breadth: password login, headless auth, OIDC, SAML, sessions, org workflows, FGA, and the hosted-first MCP-oriented Todo flow side by side.

If you build headless auth on a different browser origin than the SqlOS host, make those browser requests credentialed so SqlOS can persist and reuse its auth-page session cookie. Follow-up `/sqlos/auth/authorize?prompt=none` requests should then silently succeed when that session exists, or return `login_required` when it does not.

|            | URL                                       |
| ---------- | ----------------------------------------- |
| Dashboard  | `http://localhost:5062/sqlos/`            |
| Auth Admin | `http://localhost:5062/sqlos/admin/auth/` |
| FGA Admin  | `http://localhost:5062/sqlos/admin/fga/`  |
| Web App    | `http://localhost:3010/`                  |
| Todo App   | `http://localhost:5080/`                  |

## Requirements

- .NET 9.0+
- SQL Server (any edition, including LocalDB)
- EF Core 9.0+

## Testing

```bash
dotnet build SqlOS.sln
./scripts/unit-tests.sh
./scripts/integration-tests.sh
./scripts/docs-check.sh
```

## Repo Layout

```
src/SqlOS                                # The library
tests/SqlOS.Tests                        # Unit tests
tests/SqlOS.IntegrationTests             # Integration tests
tests/SqlOS.Benchmarks                   # Performance benchmarks
examples/SqlOS.Todo.Api                  # Canonical hosted-first Todo sample
examples/SqlOS.Todo.AppHost              # Aspire runner for the Todo sample
examples/SqlOS.Todo.IntegrationTests     # Todo sample end-to-end tests
examples/SqlOS.Example.Api               # ASP.NET API example
examples/SqlOS.Example.Web               # Next.js frontend example
examples/SqlOS.Example.AppHost           # Aspire orchestration
```

## Documentation

- [Configuration](https://sqlos.dev/docs/guides/configuration) — service registration, EF integration, dashboard setup
- [Auth Page](docs/AUTH_PAGE.md) — hosted OAuth endpoints and branded UI
- [Email OTP](docs/EMAIL_OTP.md) — passwordless login/signup across hosted, headless, and SDK flows
- [SMS OTP](docs/SMS_OTP.md) — passwordless phone-code login/signup through Twilio Verify
- [Email Invitations](docs/INVITATIONS.md) — organization invite links, dashboard, SDK, hosted, and headless flows
- [Todo Sample](examples/SqlOS.Todo.Api/README.md) — hosted auth, simple FGA, and MCP-oriented protected-resource flows
- [Client Registration DevEx](docs/CLIENT_REGISTRATION_DEVEX_2026.md) — product vocabulary and onboarding model
- [Preregistration vs CIMD vs DCR](web/content/docs/authserver/preregistration-vs-cimd-vs-dcr.mdx) — choose the right client onboarding path
- [Client ID Metadata Documents](web/content/docs/authserver/client-id-metadata-documents.mdx) — portable public clients with metadata URLs
- [Dynamic Client Registration](web/content/docs/authserver/dynamic-client-registration.mdx) — compatibility-mode runtime registration
- [MCP Resource Indicators and Audience](web/content/docs/authserver/mcp-resource-indicators-and-audience.mdx) — resource-bound tokens and audience validation
- [OIDC Auth](web/content/docs/authserver/oidc-auth.mdx) — OpenID Connect provider support
- [Google OIDC](https://sqlos.dev/docs/authserver/google-oidc) · [Microsoft OIDC](https://sqlos.dev/docs/authserver/microsoft-oidc) · [Apple OIDC](https://sqlos.dev/docs/authserver/apple-oidc) · [Custom OIDC](https://sqlos.dev/docs/authserver/custom-oidc)
- [Guides](https://sqlos.dev/docs/guides/index) — task-oriented walkthroughs
- [Entra SSO Testing](docs/ENTRA_SSO.md) — SAML SSO with Microsoft Entra
- [Example App](docs/EXAMPLE_APP.md) — running the demo stack
- [Testing](https://sqlos.dev/docs/guides/testing) — test structure and conventions
- [Releasing](docs/RELEASE_VERSION.md) — versioning and release process

## Testing Email OTP in the Todo Sample

Run it like this:

```bash
ACS_COMMUNICATION_SERVICE_NAME=sqlos-dev-comm
AZURE_RESOURCE_GROUP=rg-sqlos-web-prod
ACS_FROM_ADDRESS=no-reply@sqlos.dev

ACS_CONN=$(az communication list-key \
  --name "$ACS_COMMUNICATION_SERVICE_NAME" \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --query primaryConnectionString \
  -o tsv)

TodoSample__EnableEmailOtp=true \
SqlOS__EmailOtp__AzureCommunicationServicesConnectionString="$ACS_CONN" \
SqlOS__EmailOtp__FromAddress="$ACS_FROM_ADDRESS" \
dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
```

## Testing the CLI Auth

In another terminal:

dotnet run --project examples/SqlOS.Todo.Cli -- login
Open the printed URL, sign in through SqlOS AuthPage, approve the Todo CLI request, then run:

dotnet run --project examples/SqlOS.Todo.Cli -- whoami
dotnet run --project examples/SqlOS.Todo.Cli -- add "Ship CLI OAuth"
dotnet run --project examples/SqlOS.Todo.Cli -- list
dotnet run --project examples/SqlOS.Todo.Cli -- toggle <todo-id>

Then open `http://localhost:5080/`.

Use **Email code sign in** or **Email code sign up**. In this mode the Todo app only starts the OAuth request; the SqlOS hosted auth page sends the OTP, verifies the code, creates the account on signup, and redirects back with the authorization code.

## Testing SMS OTP in the Todo Sample

Use a Twilio pay-as-you-go account before testing arbitrary user phone numbers. Twilio trial accounts can only send OTP messages to phone numbers that are already verified on the Twilio account.

Set up Twilio Verify like this:

1. Sign in to the [Twilio Console](https://console.twilio.com/).
2. On the Console dashboard, open **Account Info**.
3. Copy **Account SID**. It starts with `AC`.
4. Click **Show** for **Auth Token**, then copy it. Treat this as a secret.
5. Open **Verify** in the left navigation, then **Services**.
6. Click **Create new Service**.
7. Set the friendly name to `SqlOS Todo Dev`.
8. Ensure SMS is enabled and the code length is 6 digits.
9. Save the **Service SID**. It starts with `VA`.

Run the Todo sample from the repo root:

```bash
TWILIO_ACCOUNT_SID=<account-sid> \
TWILIO_AUTH_TOKEN=<auth-token> \
TWILIO_VERIFY_SERVICE_SID=<verify-service-sid> \
TWILIO_DEFAULT_REGION=US \
TodoSample__EnablePhoneOtp=true \
dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
```

Open `http://localhost:5080/`. The home page should show **SMS code sign in** and **SMS code sign up**. `/sample/config` should report `"phoneOtpEnabled": true`.

Start SMS testing from the Todo sample home page, not directly from `/sqlos/auth/login`. A direct AuthPage sign-in creates a SqlOS auth-page session and shows `/sqlos/auth/login?status=signed-in`, but it does not give the Todo SPA an OAuth access token. The Todo app gets its token only when the flow starts at `http://localhost:5080/` and returns through `/callback.html`.

Do not buy or configure a Programmable Messaging phone number for this SqlOS integration. SqlOS calls Twilio Verify v2 with the Verify Service SID and `sms` channel; Verify manages the SMS sender path for the verification message.

## Running the Example App with Transactional Email

```bash
AZURE_EMAIL_CONNECTION_STRING="$(az communication list-key \
  --name <communication-service-name> \
  --resource-group <resource-group> \
  --query primaryConnectionString \
  -o tsv)" \
AZURE_EMAIL_SENDER_ADDRESS="hello@domain.com" \
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

## License

MIT
