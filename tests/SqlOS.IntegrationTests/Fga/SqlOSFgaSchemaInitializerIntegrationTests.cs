using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Fga.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

[TestClass]
public class SqlOSFgaSchemaInitializerIntegrationTests : FgaIntegrationTestBase
{
    [TestMethod]
    public async Task EnsureSchema_Idempotent_CanRunMultipleTimes()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlOSFgaSchemaInitializer(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaSchemaInitializer>());

        await initializer.EnsureSchemaAsync();
        await initializer.EnsureSchemaAsync();
    }

    [TestMethod]
    public async Task EnsureSchema_CreatesAllTables()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlOSFgaSchemaInitializer(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        var expectedTables = new[]
        {
            "SqlOSFgaSubjectTypes",
            "SqlOSFgaSubjects",
            "SqlOSFgaResourceTypes",
            "SqlOSFgaResources",
            "SqlOSFgaRoles",
            "SqlOSFgaPermissions",
            "SqlOSFgaRolePermissions",
            "SqlOSFgaGrants",
            "SqlOSFgaUserGroups",
            "SqlOSFgaUserGroupMemberships",
            "SqlOSFgaServiceAccounts",
            "SqlOSFgaUsers",
            "SqlOSFgaAgents",
            "SqlOSFgaSchema"
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
        var initializer = new SqlOSFgaSchemaInitializer(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        var hasColumn = await ColumnExistsAsync("SqlOSFgaGrants", "Description");
        Assert.IsTrue(hasColumn, "SqlOSFgaGrants.Description column should exist after v3 migration");
    }

    [TestMethod]
    public async Task EnsureSchema_V4Migration_AddsAuthorizationIndexes()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlOSFgaSchemaInitializer(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaResources", "IX_SqlOSFgaResources_ParentId"));
        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaRolePermissions", "IX_SqlOSFgaRolePermissions_PermissionId_RoleId"));
        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaGrants", "IX_SqlOSFgaGrants_ResourceId_SubjectId"));
        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaGrants", "IX_SqlOSFgaGrants_SubjectId"));
    }

    [TestMethod]
    public async Task EnsureSchema_V5Migration_AddsGroupLifecycleColumn()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlOSFgaSchemaInitializer(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        Assert.IsTrue(await ColumnExistsAsync("SqlOSFgaUserGroups", "IsActive"));
    }

    [TestMethod]
    public async Task EnsureSchema_V6Migration_EnforcesUniquePermissionKeys()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlOSFgaSchemaInitializer(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaPermissions", "UX_SqlOSFgaPermissions_Key"));

        Context.Set<SqlOS.Fga.Models.SqlOSFgaPermission>().Add(new()
        {
            Id = $"perm_duplicate_{Guid.NewGuid():N}",
            Key = "TEST_VIEW",
            Name = "Duplicate View"
        });
        await Assert.ThrowsExceptionAsync<DbUpdateException>(() => Context.SaveChangesAsync());
        Context.ChangeTracker.Clear();
    }

    [TestMethod]
    public async Task EnsureSchema_EachEmbeddedMigrationPersistsItsOwnVersion()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlOSFgaSchemaInitializer(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        var version = await GetSchemaVersionAsync();
        Assert.AreEqual(GetLatestMigrationVersion(), version);
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

    private async Task<bool> IndexExistsAsync(string tableName, string indexName)
    {
        var connection = Context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM sys.indexes i
                INNER JOIN sys.tables t ON i.object_id = t.object_id
                WHERE t.name = @tableName
                  AND i.name = @indexName
                  AND t.schema_id = SCHEMA_ID('dbo')";
            cmd.Parameters.Add(new SqlParameter("@tableName", tableName));
            cmd.Parameters.Add(new SqlParameter("@indexName", indexName));
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
            cmd.CommandText = "SELECT TOP 1 [Version] FROM [dbo].[SqlOSFgaSchema]";
            var result = await cmd.ExecuteScalarAsync();
            return result != null ? Convert.ToInt32(result) : 0;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static int GetLatestMigrationVersion()
    {
        const string resourcePrefix = "SqlOS.Fga.Schema.";

        return typeof(SqlOSFgaSchemaInitializer).Assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name => name[resourcePrefix.Length..].Split('_', 2)[0])
            .Select(int.Parse)
            .Max();
    }
}
