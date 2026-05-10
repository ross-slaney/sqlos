using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

const int todoPort = 5080;
var todoOrigin = $"http://localhost:{todoPort}";
var todoResource = $"{todoOrigin}/api/todos";
var todoIssuer = $"{todoOrigin}/sqlos/auth";
var todoEnableEmailOtp = builder.Configuration["TodoSample:EnableEmailOtp"];
var emailOtpConnectionString = builder.Configuration["SqlOS:EmailOtp:AzureCommunicationServicesConnectionString"];
var emailOtpFromAddress = builder.Configuration["SqlOS:EmailOtp:FromAddress"];
var sqlPassword = builder.AddParameter("sql-password", value: "LocalDevPassword123!");

var sql = builder.AddSqlServer("sql", password: sqlPassword, port: 1435)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume()
    .WithContainerRuntimeArgs("--platform", "linux/amd64");

var database = sql.AddDatabase("sqlos-todo");

var todoApi = builder.AddProject<Projects.SqlOS_Todo_Api>("todo-api")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("ConnectionStrings__DefaultConnection", database.Resource.ConnectionStringExpression)
    .WithEnvironment("SqlOS__Issuer", todoIssuer)
    .WithEnvironment("TodoSample__PublicOrigin", todoOrigin)
    .WithEnvironment("TodoSample__Resource", todoResource)
    .WithEnvironment("TodoSample__EnableHeadless", "false")
    .WithEnvironment("TodoSample__EnableDcr", "false");

if (!string.IsNullOrWhiteSpace(todoEnableEmailOtp))
{
    todoApi.WithEnvironment("TodoSample__EnableEmailOtp", todoEnableEmailOtp);
}

if (!string.IsNullOrWhiteSpace(emailOtpConnectionString) && !string.IsNullOrWhiteSpace(emailOtpFromAddress))
{
    todoApi
        .WithEnvironment("SqlOS__EmailOtp__AzureCommunicationServicesConnectionString", emailOtpConnectionString)
        .WithEnvironment("SqlOS__EmailOtp__FromAddress", emailOtpFromAddress);
}

builder.Build().Run();
