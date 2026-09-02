# Full example AppHost

`SqlOS.Example.AppHost` is the recommended way to run the broad SqlOS example locally. It uses .NET Aspire to provision SQL Server, create two databases, start both .NET APIs, and launch three web clients with the local URLs and dependencies they expect.

## What starts

| Aspire resource | Project or package | Address | Depends on |
| --- | --- | --- | --- |
| `sql` | SQL Server container | TCP `localhost:1434` | Docker |
| `sqlos-example` | Database | SQL resource | `sql` |
| `sqlos-todo` | Database | SQL resource | `sql` |
| `api` | [`SqlOS.Example.Api`](../SqlOS.Example.Api/README.md) | `http://localhost:5062` | `sqlos-example` |
| `todo-api` | [`SqlOS.Todo.Api`](../SqlOS.Todo.Api/README.md) | `http://localhost:5080` | `sqlos-todo` |
| `aspnet-web` | [`SqlOS.Example.AspNetCoreWeb`](../SqlOS.Example.AspNetCoreWeb/README.md) | `http://localhost:5090` | `todo-api` |
| `web` | [`SqlOS.Example.Web`](../SqlOS.Example.Web/README.md) | `http://localhost:3010` | `api` |
| `angular-web` | [`SqlOS.Example.AngularWeb`](../SqlOS.Example.AngularWeb/README.md) | `http://localhost:4200` | `api` |

The AppHost does **not** start the [Expo app](../SqlOS.Example.ExpoApp/README.md) or [Todo CLI](../SqlOS.Todo.Cli/README.md). Start either client separately after its backend is running.

## Prerequisites

- .NET 9 SDK
- Docker Desktop or another Docker-compatible runtime
- Node.js and npm
- free local ports `1434`, `3010`, `4200`, `5062`, `5080`, `5090`, `18888`, and `18889`

The SQL image is started with the `linux/amd64` platform argument. Docker may emulate that architecture on an ARM64 development machine.

## Run it

Install the two JavaScript applications once, then start Aspire from the repository root:

```bash
npm ci --prefix packages/headless && npm run build --prefix packages/headless
npm ci --prefix examples/SqlOS.Example.Web
npm ci --prefix examples/SqlOS.Example.AngularWeb
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

The AppHost does not run `npm install` for you. If an npm resource exits immediately with a missing-package error, install that project's dependencies and restart.

The terminal prints the authenticated Aspire dashboard link. Its configured listener is `https://localhost:18888`, with OTLP ingestion on `http://localhost:18889`. Use the URL printed by Aspire because it may include the local dashboard login token.

## First-run tour

1. Open `http://localhost:5090` for the most idiomatic .NET client. Sign in through the Todo-hosted SqlOS page and inspect the claims and protected `/api/me` result.
2. Open `http://localhost:3010` to compare hosted and headless authentication in Next.js. After sign-in, explore the retail/FGA screens, MFA page, and delegated SSO setup page.
3. Open `http://localhost:4200` for the equivalent Angular browser flow and FGA-filtered retail UI.
4. Open `http://localhost:5062/swagger` for the application API surface. SqlOS internals are intentionally absent from this application-focused OpenAPI document.
5. Open `http://localhost:5062/sqlos` for the SqlOS dashboard. The local sample password is `your-strong-password`.
6. Open `http://localhost:5080` for the smaller Todo sample and `http://localhost:5080/swagger` for its API.

The first API startup applies EF Core migrations and seeds the example identity/FGA configuration. The retail seed service also creates demo retail data and identities used by the UI switchers.

## Configuration injected by the AppHost

The orchestration code in [`Program.cs`](Program.cs) deliberately makes local callback URLs explicit:

| Target | Injected setting | Value |
| --- | --- | --- |
| Example API | `ConnectionStrings__DefaultConnection` | Aspire connection string for `sqlos-example` |
| Example API | `SqlOS__Issuer` | `http://localhost:5062/sqlos/auth` |
| Example API | `SqlOS__HeadlessFrontendUrl` | `http://localhost:3010` |
| Example API | `ExampleFrontend__Origin` | `http://localhost:3010` |
| Example API | `ExampleFrontend__CallbackUrl` | `http://localhost:3010/auth/callback` |
| ASP.NET Core client | `SqlOS__Origin` | `http://localhost:5080` |
| ASP.NET Core client | `SqlOS__ClientId` | `example-aspnet` |
| Next.js | `NEXT_PUBLIC_API_URL` | The example API HTTP endpoint |
| Next.js | `NEXTAUTH_URL` | `http://localhost:3010` |
| Next.js | `NEXTAUTH_SECRET` | A local-only sample secret |
| Todo API | connection string, issuer, origin, resource | Local `sqlos-todo` and `http://localhost:5080` URLs |

The Angular API URL and client ID are compile-time values in [`environment.ts`](../SqlOS.Example.AngularWeb/src/app/environments/environment.ts): `http://localhost:5062` and `example-angular`.

The fixed ports are part of the seeded OAuth redirect URIs and issuer configuration. If you change one, update the AppHost environment, the corresponding client, and the client redirect registration together.

## Add optional email, phone, or Microsoft login

The base sample works with password authentication and does not require third-party credentials. `Program.cs` forwards optional settings to the APIs only when they are present.

### Email delivery and email OTP

Set both:

```text
SqlOS:Email:AzureCommunicationServicesConnectionString
SqlOS:Email:FromAddress
```

The aliases `AZURE_EMAIL_CONNECTION_STRING` and `AZURE_EMAIL_SENDER_ADDRESS` are also recognized.

The broad example API already includes email OTP in its AuthPage credential list. To expose it in the Todo AuthPage too, launch with `TodoSample__EnableEmailOtp=true` in addition to valid delivery settings; see the [Todo API guide](../SqlOS.Todo.Api/README.md#what-to-try).

### Phone OTP

Enable it and provide a Twilio Verify service:

```text
SqlOS:PhoneOtp:Enabled=true
TWILIO_ACCOUNT_SID
TWILIO_AUTH_TOKEN
TWILIO_VERIFY_SERVICE_SID
```

`TWILIO_DEFAULT_REGION` is optional.

### Microsoft social login

Set:

```text
AZURE_OIDC_MICROSOFT_CLIENT_ID
AZURE_OIDC_MICROSOFT_CLIENT_SECRET
```

`AZURE_OIDC_MICROSOFT_TENANT` is optional. When the required pair is missing, the API does not seed the Microsoft connection and the button does not appear.

The AppHost project has the user-secrets ID `sqlos-example-apphost`, so you can keep these settings out of shell history:

```bash
cd examples/SqlOS.Example.AppHost
dotnet user-secrets set "SqlOS:Email:AzureCommunicationServicesConnectionString" "<connection-string>"
dotnet user-secrets set "SqlOS:Email:FromAddress" "<verified-sender>"
```

Never commit provider credentials. Return to the repository root before running commands that use root-relative paths.

## Persistent data and reset behavior

The SQL resource uses both `ContainerLifetime.Persistent` and `WithDataVolume()`. This is useful during development: stopping the AppHost does not delete users, sessions, OAuth clients, FGA grants, or sample application data.

If the sample must start from an empty state:

1. stop the AppHost;
2. identify the SQL container and volume created for this AppHost in Docker;
3. remove them only if the stored data is disposable;
4. restart the AppHost and let migrations and seeds run again.

The `sqlos-example` and `sqlos-todo` databases are independent, even though they share the same container. Prefer dropping only the disposable database you intend to reset when preserving the other sample matters.

## Troubleshooting

### A resource is stuck waiting

Open the Aspire dashboard and inspect its dependency. Both APIs wait for their database; Next.js and Angular wait for `api`, while ASP.NET Core waits for `todo-api`. Fix the first failing dependency rather than restarting downstream resources repeatedly.

### SQL Server does not start

Confirm Docker is running, port `1434` is free, and your runtime can run or emulate `linux/amd64` images. An existing persistent container may already own the port.

### OAuth returns an invalid redirect URI

Use the AppHost URLs exactly: Next.js Auth.js finishes at `http://localhost:3010/api/auth/callback/sqlos`, Angular at `http://localhost:4200/auth/callback`, and ASP.NET Core at `5090/signin-sqlos` (Todo API). A standalone Next.js process defaults to `3000` and therefore needs the example API's standalone Auth.js callback (`http://localhost:3000/api/auth/callback/sqlos`) instead of the AppHost's `3010` callback.

### The dashboard password is rejected

For a fresh checkout it is `your-strong-password`. Check whether environment or user-secret configuration overrides `SqlOS:Dashboard:Password`, then restart the API. The password is runtime configuration; deleting the sample database is not a password-recovery step.

### Email or phone buttons fail

Provider-backed delivery is not simulated by this AppHost. Supply valid ACS or Twilio settings and confirm the sender/service is configured with that provider.

## Build and test

Build the orchestrated .NET projects:

```bash
dotnet build examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

Build both browser clients:

```bash
npm run build --prefix examples/SqlOS.Example.Web
npm run build --prefix examples/SqlOS.Example.AngularWeb
```

Run the example integration suite with Docker available:

```bash
dotnet test examples/SqlOS.Example.IntegrationTests/SqlOS.Example.IntegrationTests.csproj
```

Those tests use the dedicated integration-test AppHost in `tests/SqlOS.IntegrationTests.AppHost`. They do not depend on a manually running copy of this public example AppHost.
