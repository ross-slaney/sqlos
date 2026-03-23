using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

const int todoPort = 5080;
var todoOrigin = $"http://localhost:{todoPort}";
var todoResource = $"{todoOrigin}/api/todos";
var todoIssuer = $"{todoOrigin}/sqlos/auth";
var sqlPassword = builder.AddParameter("sql-password", value: "LocalDevPassword123!");

var sql = builder.AddSqlServer("sql", password: sqlPassword, port: 1434)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume()
    .WithContainerRuntimeArgs("--platform", "linux/amd64");

var exampleDatabase = sql.AddDatabase("sqlos-example");
var todoDatabase = sql.AddDatabase("sqlos-todo");

var api = builder.AddProject<Projects.SqlOS_Example_Api>("api")
    .WithReference(exampleDatabase)
    .WaitFor(exampleDatabase)
    .WithEnvironment("ConnectionStrings__DefaultConnection", exampleDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("SqlOS__Issuer", "http://localhost:5062/sqlos/auth")
    .WithEnvironment("SqlOS__HeadlessFrontendUrl", "http://localhost:3010")
    .WithEnvironment("ExampleFrontend__Origin", "http://localhost:3010")
    .WithEnvironment("ExampleFrontend__CallbackUrl", "http://localhost:3010/auth/callback")
    .WithEnvironment("ExampleFrontend__ClientId", "example-web");

builder.AddProject<Projects.SqlOS_Todo_Api>("todo-api")
    .WithReference(todoDatabase)
    .WaitFor(todoDatabase)
    .WithEnvironment("ConnectionStrings__DefaultConnection", todoDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("SqlOS__Issuer", todoIssuer)
    .WithEnvironment("TodoSample__PublicOrigin", todoOrigin)
    .WithEnvironment("TodoSample__Resource", todoResource)
    .WithEnvironment("TodoSample__EnableHeadless", "false")
    .WithEnvironment("TodoSample__EnableDcr", "false");

builder.AddNpmApp("web", "../SqlOS.Example.Web", "dev")
    .WithHttpEndpoint(port: 3010, env: "PORT", isProxied: false)
    .WithEnvironment("NODE_ENV", "development")
    .WithEnvironment("NEXT_PUBLIC_API_URL", api.GetEndpoint("http"))
    .WithEnvironment("NEXTAUTH_URL", "http://localhost:3010")
    .WithEnvironment("NEXTAUTH_SECRET", "sqlos-example-local-secret")
    .WaitFor(api);

builder.AddNpmApp("angular-web", "../SqlOS.Example.AngularWeb", "dev")
    .WithHttpEndpoint(port: 4200, env: "PORT", isProxied: false)
    .WithEnvironment("NODE_ENV", "development")
    .WaitFor(api);

builder.Build().Run();
