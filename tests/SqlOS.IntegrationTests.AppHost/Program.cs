using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var usePostgreSql = Environment.GetEnvironmentVariable("SQLOS_TEST_PROVIDER")?.Trim().ToLowerInvariant()
    is "postgresql" or "postgres" or "npgsql";

if (usePostgreSql)
{
    builder.AddPostgres("sql")
        .AddDatabase("sqlos-test");
}
else
{
    var sqlPassword = builder.AddParameter("sql-password", value: "TestPassword123!");
    builder.AddSqlServer("sql", password: sqlPassword)
        .WithContainerRuntimeArgs("--platform", "linux/amd64")
        .AddDatabase("sqlos-test");
}

builder.Build().Run();

// Test projects reference this AppHost alongside an example API's public Program. Declaring it
// internal stops ASP.NET Core 10's generator from making it public and ambiguous there.
internal partial class Program { }
