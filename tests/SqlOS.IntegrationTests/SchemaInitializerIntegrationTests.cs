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

    [TestMethod]
    public async Task EnsureSchema_AddsClientRegistrationAndResourceBindingColumns()
    {
        var initializer = new SqlOSSchemaInitializer(
            AspireFixture.SharedContext,
            Options.Create(AspireFixture.Options),
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        foreach (var column in new[]
                 {
                     "RegistrationSource",
                     "TokenEndpointAuthMethod",
                     "GrantTypesJson",
                     "ResponseTypesJson",
                     "MetadataDocumentUrl",
                     "ClientUri",
                     "LogoUri",
                     "SoftwareId",
                     "SoftwareVersion",
                     "MetadataJson",
                     "MetadataFetchedAt",
                     "MetadataExpiresAt",
                     "MetadataEtag",
                     "MetadataLastModifiedAt",
                     "LastSeenAt",
                     "DisabledAt",
                     "DisabledReason"
                 })
        {
            Assert.IsTrue(await ColumnExistsAsync("SqlOSClientApplications", column), $"Column SqlOSClientApplications.{column} should exist.");
        }

        Assert.IsTrue(await ColumnExistsAsync("SqlOSSessions", "Resource"), "Column SqlOSSessions.Resource should exist.");
        Assert.IsTrue(await ColumnExistsAsync("SqlOSSessions", "EffectiveAudience"), "Column SqlOSSessions.EffectiveAudience should exist.");
    }

    [TestMethod]
    public async Task EnsureSchema_IsIdempotent_ForClientRegistrationMigration()
    {
        var initializer = new SqlOSSchemaInitializer(
            AspireFixture.SharedContext,
            Options.Create(AspireFixture.Options),
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());

        await initializer.EnsureSchemaAsync();
        await initializer.EnsureSchemaAsync();

        Assert.IsTrue(await ColumnExistsAsync("SqlOSClientApplications", "RegistrationSource"));
        Assert.IsTrue(await ColumnExistsAsync("SqlOSSessions", "EffectiveAudience"));
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

    private static async Task<bool> ColumnExistsAsync(string tableName, string columnName)
    {
        var connection = AspireFixture.SharedContext.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*)
                FROM sys.columns c
                INNER JOIN sys.tables t ON c.object_id = t.object_id
                WHERE t.name = @tableName
                  AND c.name = @columnName
                  AND t.schema_id = SCHEMA_ID('dbo')
                """;
            cmd.Parameters.Add(new SqlParameter("@tableName", tableName));
            cmd.Parameters.Add(new SqlParameter("@columnName", columnName));
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result) > 0;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
