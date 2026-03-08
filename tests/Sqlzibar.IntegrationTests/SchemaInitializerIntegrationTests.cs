using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sqlzibar.Configuration;
using Sqlzibar.IntegrationTests.Infrastructure;
using Sqlzibar.Services;

namespace Sqlzibar.IntegrationTests;

[TestClass]
public class SchemaInitializerIntegrationTests : IntegrationTestBase
{
    [TestMethod]
    public async Task EnsureSchema_Idempotent_CanRunMultipleTimes()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlzibarSchemaInitializer(
            Context,
            Options.Create(new SqlzibarOptions()),
            loggerFactory.CreateLogger<SqlzibarSchemaInitializer>());

        await initializer.EnsureSchemaAsync();
        await initializer.EnsureSchemaAsync();
    }

    [TestMethod]
    public async Task EnsureSchema_CreatesAllTables()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlzibarSchemaInitializer(
            Context,
            Options.Create(new SqlzibarOptions()),
            loggerFactory.CreateLogger<SqlzibarSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        var expectedTables = new[]
        {
            "SqlzibarSubjectTypes",
            "SqlzibarSubjects",
            "SqlzibarResourceTypes",
            "SqlzibarResources",
            "SqlzibarRoles",
            "SqlzibarPermissions",
            "SqlzibarRolePermissions",
            "SqlzibarGrants",
            "SqlzibarUserGroups",
            "SqlzibarUserGroupMemberships",
            "SqlzibarServiceAccounts",
            "SqlzibarUsers",
            "SqlzibarAgents",
            "SqlzibarSchema"
        };

        foreach (var tableName in expectedTables)
        {
            var exists = await TableExistsAsync(tableName);
            Assert.IsTrue(exists, $"Table {tableName} should exist");
        }
    }

    [TestMethod]
    public async Task EnsureSchema_V3Migration_AddsDescriptionColumnToGrants()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlzibarSchemaInitializer(
            Context,
            Options.Create(new SqlzibarOptions()),
            loggerFactory.CreateLogger<SqlzibarSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        var hasColumn = await ColumnExistsAsync("SqlzibarGrants", "Description");
        Assert.IsTrue(hasColumn, "SqlzibarGrants.Description column should exist after v3 migration");
    }

    [TestMethod]
    public async Task EnsureSchema_SetsCorrectVersion()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlzibarSchemaInitializer(
            Context,
            Options.Create(new SqlzibarOptions()),
            loggerFactory.CreateLogger<SqlzibarSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        var version = await GetSchemaVersionAsync();
        Assert.IsTrue(version >= 3, $"Schema version should be at least 3, was {version}");
    }

    private async Task<bool> TableExistsAsync(string tableName)
    {
        var connection = Context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.tables WHERE name = @name AND schema_id = SCHEMA_ID('dbo')";
            cmd.Parameters.Add(new SqlParameter("@name", tableName));
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result) > 0;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private async Task<bool> ColumnExistsAsync(string tableName, string columnName)
    {
        var connection = Context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*) FROM sys.columns c
                INNER JOIN sys.tables t ON c.object_id = t.object_id
                WHERE t.name = @tableName AND c.name = @columnName AND t.schema_id = SCHEMA_ID('dbo')";
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

    private async Task<int> GetSchemaVersionAsync()
    {
        var connection = Context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT TOP 1 [Version] FROM [dbo].[SqlzibarSchema]";
            var result = await cmd.ExecuteScalarAsync();
            return result != null ? Convert.ToInt32(result) : 0;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
