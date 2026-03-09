using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class SchemaInitializerIntegrationTests
{
    [TestMethod]
    public async Task EnsureSchema_CreatesCoreTables()
    {
        var initializer = new SqlOSSchemaInitializer(
            AspireFixture.SharedContext,
            Options.Create(AspireFixture.Options),
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        foreach (var table in new[]
                 {
                     "SqlOSOrganizations",
                     "SqlOSUsers",
                     "SqlOSUserEmails",
                     "SqlOSCredentials",
                     "SqlOSMemberships",
                     "SqlOSSsoConnections",
                     "SqlOSExternalIdentities",
                     "SqlOSClientApplications",
                     "SqlOSSessions",
                     "SqlOSRefreshTokens",
                     "SqlOSSigningKeys",
                     "SqlOSTemporaryTokens",
                     "SqlOSAuditEvents",
                     "SqlOSSchema"
                 })
        {
            Assert.IsTrue(await TableExistsAsync(table), $"Table {table} should exist.");
        }
    }

    private static async Task<bool> TableExistsAsync(string tableName)
    {
        var connection = AspireFixture.SharedContext.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = @name AND schema_id = SCHEMA_ID('dbo')";
            cmd.Parameters.Add(new SqlParameter("@name", tableName));
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result) > 0;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
