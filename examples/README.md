# SqlOS examples

These samples are working reference applications, not isolated snippets. They show how a .NET host configures SqlOS, how browser and native clients complete OAuth flows, how protected APIs enforce tokens and fine-grained authorization, and how the pieces run together under .NET Aspire.

## Choose the shortest path to your goal

| You want to… | Start here | Why |
| --- | --- | --- |
| Evaluate SqlOS in one focused application | [Todo API](SqlOS.Todo.Api/README.md) + `SqlOS.Todo.AppHost` | One .NET API, hosted sign-in, a protected Todo resource, FGA, and Swagger |
| See the broadest feature set | [Full example AppHost](SqlOS.Example.AppHost/README.md) | Runs the example API, Todo API, SQL Server, and three web clients together |
| Integrate a server-rendered .NET app | [ASP.NET Core client](SqlOS.Example.AspNetCoreWeb/README.md) | Razor Pages, ASP.NET Core OAuth middleware, PKCE, encrypted cookies, and a protected API call |
| Integrate a JavaScript browser app | [Next.js client](SqlOS.Example.Web/README.md) or [Angular client](SqlOS.Example.AngularWeb/README.md) | Hosted and headless auth, browser PKCE, token refresh, and FGA-filtered retail screens |
| Integrate a native mobile app | [Expo client](SqlOS.Example.ExpoApp/README.md) | Custom-scheme callback, PKCE, SecureStore, refresh tokens, and protected APIs |
| Build a terminal sign-in flow | [Todo CLI](SqlOS.Todo.Cli/README.md) | OAuth device authorization, browser handoff, polling, token refresh, and CLI API calls |

If you are new to the repository, start with the Todo sample. Use the full example when you want to compare client patterns or explore SSO, MFA, headless auth, and richer FGA models.

## Run the full example

Prerequisites:

- .NET 9 SDK
- Docker Desktop or another Docker-compatible runtime
- Node.js and npm
- available local ports listed below

From the repository root:

```bash
npm ci --prefix examples/SqlOS.Example.Web
npm ci --prefix examples/SqlOS.Example.AngularWeb
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

Wait for the Aspire resource table to show the applications as running, then open:

| URL | What is there |
| --- | --- |
| `http://localhost:5062/swagger` | Application API reference |
| `http://localhost:5062/sqlos` | SqlOS administration dashboard |
| `http://localhost:5090` | ASP.NET Core Razor Pages client |
| `http://localhost:3010` | Next.js client |
| `http://localhost:4200` | Angular client |
| `http://localhost:5080` | Todo sample |

The checked-in dashboard password is `your-strong-password`. It exists only to make the local sample runnable; replace it for any shared or deployed environment.

The full AppHost launches exactly these resources:

```mermaid
flowchart LR
    AppHost["SqlOS.Example.AppHost"] --> SQL["SQL Server :1434"]
    SQL --> ExampleDb["sqlos-example"]
    SQL --> TodoDb["sqlos-todo"]
    ExampleDb --> API["Example API :5062"]
    TodoDb --> Todo["Todo API :5080"]
    Todo --> DotNet["ASP.NET Core :5090"]
    API --> Next["Next.js :3010"]
    API --> Angular["Angular :4200"]
    API -. "start separately" .-> Expo["Expo"]
    Todo -. "start separately" .-> CLI["Todo CLI"]
```

It does **not** start the Expo app or Todo CLI. Those are separate clients that connect to an already-running backend.

## Example catalog

| Project | Started by | Default address | What it demonstrates |
| --- | --- | --- | --- |
| [`SqlOS.Example.AppHost`](SqlOS.Example.AppHost/README.md) | You | Aspire dashboard on HTTPS port `18888` | Full local orchestration, SQL resources, configuration forwarding |
| [`SqlOS.Example.Api`](SqlOS.Example.Api/README.md) | Full AppHost | `http://localhost:5062` | Embedding SqlOS in ASP.NET Core, AuthServer, dashboard, MFA, SSO helpers, FGA, protected APIs |
| [`SqlOS.Example.AspNetCoreWeb`](SqlOS.Example.AspNetCoreWeb/README.md) | Both AppHosts | `http://localhost:5090` | Built-in ASP.NET Core OAuth handler, PKCE, cookie session, and a Todo-resource API call |
| [`SqlOS.Example.Web`](SqlOS.Example.Web/README.md) | Full AppHost | `http://localhost:3010` under Aspire; `3000` standalone | Next.js, hosted and headless auth, NextAuth, MFA, SSO portal, retail FGA UI |
| [`SqlOS.Example.AngularWeb`](SqlOS.Example.AngularWeb/README.md) | Full AppHost | `http://localhost:4200` | Angular, hosted and headless auth, browser PKCE, retail FGA UI |
| [`SqlOS.Example.ExpoApp`](SqlOS.Example.ExpoApp/README.md) | You, separately | Simulator/device | Expo Router, native OAuth callback, SecureStore, protected retail UI |
| [`SqlOS.Example.Tests`](SqlOS.Example.Tests/SqlOS.Example.Tests.csproj) | Test runner | n/a | ASP.NET Core access-token refresh, ticket renewal, and logout fallback |
| [`SqlOS.Example.IntegrationTests`](SqlOS.Example.IntegrationTests/SqlOS.Example.IntegrationTests.csproj) | Test runner | n/a | Real-SQL tests for example auth, OIDC, email OTP, dashboard, workspaces, and retail FGA |
| [`SqlOS.Todo.AppHost`](SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj) | You | Aspire dashboard on HTTPS port `18890` | Focused SQL + Todo API + Razor client stack |
| [`SqlOS.Todo.Api`](SqlOS.Todo.Api/README.md) | Either AppHost | `http://localhost:5080` | Hosted auth, resource metadata, audience validation, Todo FGA, CIMD and optional DCR |
| [`SqlOS.Todo.Cli`](SqlOS.Todo.Cli/README.md) | You, separately | Terminal | Device authorization grant and Todo API calls |
| [`SqlOS.Todo.IntegrationTests`](SqlOS.Todo.IntegrationTests/SqlOS.Todo.IntegrationTests.csproj) | Test runner | n/a | Real-SQL tests for Todo auth, FGA, device flow, CIMD, and DCR |

`SqlOS.Example.Tests` provides fast application-session coverage; the two integration-test projects provide executable real-SQL protocol and authorization coverage.

## Ports and local state

| Port | Owner |
| --- | --- |
| `1434` | Full example SQL Server container |
| `1435` | Todo-only SQL Server container |
| `3010` | Next.js under the full AppHost |
| `4200` | Angular |
| `5062` | Example API and SqlOS host |
| `5080` | Todo API |
| `5090` | ASP.NET Core client |
| `18888` / `18889` | Full AppHost dashboard / OTLP endpoint |
| `18890` / `18891` | Todo AppHost dashboard / OTLP endpoint |

Both AppHosts use a persistent SQL container and data volume. Stopping the AppHost does not erase users, clients, grants, or sample data. To start fresh, stop the AppHost and deliberately remove its SQL container and associated data volume, or drop the disposable sample database. Do not do that if the volume contains data you care about.

The full stack uses separate `sqlos-example` and `sqlos-todo` databases in one SQL container. The Todo-only AppHost uses its own SQL container and `sqlos-todo` database.

## Optional provider configuration

Password auth works without external services. These provider-backed features work only after their settings are supplied to the full AppHost. The broad example can render its email-code option before delivery is configured; sending still requires ACS.

| Feature | Required configuration |
| --- | --- |
| Email delivery and email OTP | `SqlOS:Email:AzureCommunicationServicesConnectionString` and `SqlOS:Email:FromAddress`, or `AZURE_EMAIL_CONNECTION_STRING` and `AZURE_EMAIL_SENDER_ADDRESS`; Todo also requires `TodoSample__EnableEmailOtp=true` |
| Phone OTP | `SqlOS:PhoneOtp:Enabled=true` plus `TWILIO_ACCOUNT_SID`, `TWILIO_AUTH_TOKEN`, and `TWILIO_VERIFY_SERVICE_SID` |
| Microsoft social login | `AZURE_OIDC_MICROSOFT_CLIENT_ID` and `AZURE_OIDC_MICROSOFT_CLIENT_SECRET`; tenant is optional |

Use environment variables or AppHost user-secrets. Never commit provider secrets.

## Verify the examples

Build the clients:

```bash
dotnet build examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
dotnet build examples/SqlOS.Todo.Cli/SqlOS.Todo.Cli.csproj
dotnet test examples/SqlOS.Example.Tests/SqlOS.Example.Tests.csproj
npm run build --prefix examples/SqlOS.Example.Web
npm run build --prefix examples/SqlOS.Example.AngularWeb
npm ci --prefix examples/SqlOS.Example.ExpoApp
npm exec --prefix examples/SqlOS.Example.ExpoApp -- tsc --noEmit -p examples/SqlOS.Example.ExpoApp/tsconfig.json
```

Run the real-SQL integration suites with Docker available:

```bash
dotnet test examples/SqlOS.Example.IntegrationTests/SqlOS.Example.IntegrationTests.csproj
dotnet test examples/SqlOS.Todo.IntegrationTests/SqlOS.Todo.IntegrationTests.csproj
```

The frontend samples do not currently have checked-in browser automation. Their build commands catch compilation and bundling failures; the backend protocol and authorization behavior is covered by the integration suites.

## What to copy into your application

- Start with [the API composition root](SqlOS.Example.Api/Program.cs) and [application DbContext](SqlOS.Example.Api/Data/ExampleAppDbContext.cs) to see the .NET integration boundary.
- Use the [ASP.NET Core client](SqlOS.Example.AspNetCoreWeb/Program.cs) for a server-rendered .NET OAuth integration.
- Use the [Next.js PKCE helper](SqlOS.Example.Web/lib/sqlos-auth.ts), [Angular auth service](SqlOS.Example.AngularWeb/src/app/services/sqlos-auth.service.ts), or [Expo auth helper](SqlOS.Example.ExpoApp/services/sqlos-auth.ts) for client-specific reference flows.
- Read the relevant sample README before copying security or storage choices. Several conveniences are intentionally local-demo defaults, and each guide calls them out.
