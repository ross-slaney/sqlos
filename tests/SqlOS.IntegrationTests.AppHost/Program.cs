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
