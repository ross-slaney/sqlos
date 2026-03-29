using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

const int todoPort = 5080;
var todoOrigin = $"http://localhost:{todoPort}";
var todoResource = $"{todoOrigin}/api/todos";
var todoIssuer = $"{todoOrigin}/sqlos/auth";
var sqlPassword = builder.AddParameter("sql-password", value: "LocalDevPassword123!");

var sql = builder.AddSqlServer("sql", password: sqlPassword, port: 1435)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume()
    .WithContainerRuntimeArgs("--platform", "linux/amd64");

var database = sql.AddDatabase("sqlos-todo");

builder.AddProject<Projects.SqlOS_Todo_Api>("todo-api")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("ConnectionStrings__DefaultConnection", database.Resource.ConnectionStringExpression)
    .WithEnvironment("SqlOS__Issuer", todoIssuer)
    .WithEnvironment("TodoSample__PublicOrigin", todoOrigin)
    .WithEnvironment("TodoSample__Resource", todoResource)
    .WithEnvironment("TodoSample__EnableHeadless", "false")
    .WithEnvironment("TodoSample__EnableDcr", "false");

builder.Build().Run();
