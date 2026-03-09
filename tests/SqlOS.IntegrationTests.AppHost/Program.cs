using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var sqlPassword = builder.AddParameter("sql-password", value: "TestPassword123!");

var sql = builder.AddSqlServer("sql", password: sqlPassword)
    .WithContainerRuntimeArgs("--platform", "linux/amd64");

sql.AddDatabase("sqlos-test");

builder.Build().Run();
