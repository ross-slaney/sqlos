using Aspire.Hosting;

// "Sign in with X" federation demo:
//   - App X (SqlOS host)  http://localhost:5100 — the OpenID Provider
//   - App Y (Next.js)     http://localhost:3020 — Auth.js relying party
// Run `npm install` in ../SqlOS.SignInWithX.AppY once before the first start.
//
// Ports are configurable so the browser e2e tests
// (examples/SqlOS.SignInWithX.E2eTests) can boot this same app host on
// alternate ports while a manually started demo keeps running on the
// defaults. Everything derived (origins, issuer, redirect URIs, the seeded
// app-y client) flows from these values.
var builder = DistributedApplication.CreateBuilder(args);

var appXPort = GetPort(builder.Configuration["SignInWithX:AppXPort"], 5100);
var appYPort = GetPort(builder.Configuration["SignInWithX:AppYPort"], 3020);
var sqlPort = GetPort(builder.Configuration["SignInWithX:SqlPort"], 1436);
// The demo keeps a persistent SQL container with a data volume so accounts
// survive restarts; tests opt out to avoid sharing state (and the volume)
// with a concurrently running demo.
var ephemeralSql = string.Equals(
    builder.Configuration["SignInWithX:EphemeralSql"], "true", StringComparison.OrdinalIgnoreCase);

var appXOrigin = $"http://localhost:{appXPort}";
var appYOrigin = $"http://localhost:{appYPort}";
var appXIssuer = $"{appXOrigin}/sqlos/auth";

var sqlPassword = builder.AddParameter("sql-password", value: "LocalDevPassword123!");

var sql = builder.AddSqlServer("sql", password: sqlPassword, port: sqlPort)
    .WithContainerRuntimeArgs("--platform", "linux/amd64");

if (!ephemeralSql)
{
    sql = sql.WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume();
}

var database = sql.AddDatabase("sqlos-appx");

// launchProfileName: null keeps the launchSettings port (5100) from being
// claimed by the Aspire proxy, so a configured port is the only one bound.
var appX = builder.AddProject<Projects.SqlOS_SignInWithX_AppX>("app-x", launchProfileName: null)
    .WithHttpEndpoint(port: appXPort, isProxied: false)
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ASPNETCORE_URLS", appXOrigin)
    .WithEnvironment("ConnectionStrings__DefaultConnection", database.Resource.ConnectionStringExpression)
    .WithEnvironment("AppX__PublicOrigin", appXOrigin)
    .WithEnvironment("AppX__AppYOrigin", appYOrigin);

builder.AddNpmApp("app-y", "../SqlOS.SignInWithX.AppY", "dev")
    .WithHttpEndpoint(port: appYPort, env: "PORT", isProxied: false)
    .WithEnvironment("NODE_ENV", "development")
    .WithEnvironment("NEXTAUTH_URL", appYOrigin)
    .WithEnvironment("NEXTAUTH_SECRET", "sqlos-signinwithx-local-secret")
    .WithEnvironment("SQLOS_ISSUER", appXIssuer)
    .WithEnvironment("NEXT_PUBLIC_SQLOS_ORIGIN", appXOrigin)
    // Each stack compiles into its own Next.js dist directory; two dev servers
    // sharing one .next (demo + e2e tests) corrupt each other's builds.
    .WithEnvironment("NEXT_DIST_DIR", appYPort == 3020 ? ".next" : $".next-{appYPort}")
    .WithEnvironment("SQLOS_CLIENT_ID", "app-y")
    .WaitFor(appX);

builder.Build().Run();

static int GetPort(string? configured, int fallback) =>
    int.TryParse(configured, out var port) ? port : fallback;
