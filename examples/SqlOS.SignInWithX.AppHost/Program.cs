using Aspire.Hosting;

// "Sign in with X" federation demo:
//   - App X (SqlOS host)  http://localhost:5100 — the OpenID Provider
//   - App Y (Next.js)     http://localhost:3020 — Auth.js relying party
// Run `npm install` in ../SqlOS.SignInWithX.AppY once before the first start.
var builder = DistributedApplication.CreateBuilder(args);

const int appXPort = 5100;
const int appYPort = 3020;
var appXOrigin = $"http://localhost:{appXPort}";
var appYOrigin = $"http://localhost:{appYPort}";
var appXIssuer = $"{appXOrigin}/sqlos/auth";

var sqlPassword = builder.AddParameter("sql-password", value: "LocalDevPassword123!");

var sql = builder.AddSqlServer("sql", password: sqlPassword, port: 1436)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume()
    .WithContainerRuntimeArgs("--platform", "linux/amd64");

var database = sql.AddDatabase("sqlos-appx");

var appX = builder.AddProject<Projects.SqlOS_SignInWithX_AppX>("app-x")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("ConnectionStrings__DefaultConnection", database.Resource.ConnectionStringExpression)
    .WithEnvironment("AppX__PublicOrigin", appXOrigin)
    .WithEnvironment("AppX__AppYOrigin", appYOrigin);

builder.AddNpmApp("app-y", "../SqlOS.SignInWithX.AppY", "dev")
    .WithHttpEndpoint(port: appYPort, env: "PORT", isProxied: false)
    .WithEnvironment("NODE_ENV", "development")
    .WithEnvironment("NEXTAUTH_URL", appYOrigin)
    .WithEnvironment("NEXTAUTH_SECRET", "sqlos-signinwithx-local-secret")
    .WithEnvironment("SQLOS_ISSUER", appXIssuer)
    .WithEnvironment("SQLOS_CLIENT_ID", "app-y")
    .WaitFor(appX);

builder.Build().Run();
