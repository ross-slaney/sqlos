using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var sqlPassword = builder.AddParameter("sql-password", value: "LocalDevPassword123!");

var sql = builder.AddSqlServer("sql", password: sqlPassword, port: 1434)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume()
    .WithContainerRuntimeArgs("--platform", "linux/amd64");

var database = sql.AddDatabase("sqlos-example");

var api = builder.AddProject<Projects.SqlOS_Example_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("ConnectionStrings__DefaultConnection", database.Resource.ConnectionStringExpression)
    .WithEnvironment("SqlOS__Issuer", "http://localhost:5062/sqlos")
    .WithEnvironment("ExampleFrontend__Origin", "http://localhost:3010")
    .WithEnvironment("ExampleFrontend__CallbackUrl", "http://localhost:3010/auth/callback")
    .WithEnvironment("ExampleFrontend__ClientId", "example-web");

builder.AddNpmApp("web", "../SqlOS.Example.Web", "dev")
    .WithHttpEndpoint(port: 3010, env: "PORT", isProxied: false)
    .WithEnvironment("NODE_ENV", "development")
    .WithEnvironment("NEXT_PUBLIC_API_URL", api.GetEndpoint("http"))
    .WithEnvironment("NEXTAUTH_URL", "http://localhost:3010")
    .WithEnvironment("NEXTAUTH_SECRET", "sqlos-example-local-secret")
    .WaitFor(api);

builder.Build().Run();
