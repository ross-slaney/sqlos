using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;

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
        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaResources", "IX_SqlOSFgaResources_ParentId_Id"));
        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaRolePermissions", "IX_SqlOSFgaRolePermissions_PermissionId_RoleId"));
        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaGrants", "IX_SqlOSFgaGrants_ResourceId_SubjectId"));
        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaGrants", "IX_SqlOSFgaGrants_SubjectId"));
        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaGrants", "IX_SqlOSFgaGrants_CreatedAt_Id"));
        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaGrants", "IX_SqlOSFgaGrants_SubjectId_CreatedAt_Id"));
        Assert.IsTrue(await IndexExistsAsync("SqlOSFgaGrants", "IX_SqlOSFgaGrants_ResourceId_CreatedAt_Id"));
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
    public async Task EnsureSchema_V8CursorIndexes_StayUnderSqlServerKeyLimit_AndAcceptMaxLengthIds()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlOSFgaSchemaInitializer(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        await AssertIndexFitsProviderLimitsAsync("SqlOSFgaResources", "IX_SqlOSFgaResources_ParentId_Id");
        await AssertIndexFitsProviderLimitsAsync("SqlOSFgaGrants", "IX_SqlOSFgaGrants_SubjectId_CreatedAt_Id");
        await AssertIndexFitsProviderLimitsAsync("SqlOSFgaGrants", "IX_SqlOSFgaGrants_ResourceId_CreatedAt_Id");
        await AssertIndexFitsProviderLimitsAsync("SqlOSFgaGrants", "IX_SqlOSFgaGrants_ResourceId_SubjectId");
        await AssertIndexFitsProviderLimitsAsync("SqlOSFgaRolePermissions", "IX_SqlOSFgaRolePermissions_PermissionId_RoleId");

        var token = Guid.NewGuid().ToString("N");
        string MaxId(string prefix) => (prefix + token).PadRight(450, 'x');

        var resourceTypeId = $"rt_{token}";
        var subjectTypeId = $"st_{token}";
        var roleId = $"role_{token}";
        var parentId = MaxId("res_p_");
        var childId = MaxId("res_c_");
        var subjectId = MaxId("sub_");
        var grantId = MaxId("gr_");

        try
        {
            Context.Set<SqlOS.Fga.Models.SqlOSFgaResourceType>().Add(new() { Id = resourceTypeId, Name = "Bound index type" });
            Context.Set<SqlOS.Fga.Models.SqlOSFgaSubjectType>().Add(new() { Id = subjectTypeId, Name = "Bound index subject" });
            Context.Set<SqlOS.Fga.Models.SqlOSFgaRole>().Add(new() { Id = roleId, Key = $"bound_{token}", Name = "Bound index role" });
            await Context.SaveChangesAsync();

            Context.Set<SqlOS.Fga.Models.SqlOSFgaResource>().Add(new()
            {
                Id = parentId,
                Name = "Bound parent",
                ResourceTypeId = resourceTypeId
            });
            await Context.SaveChangesAsync();

            Context.Set<SqlOS.Fga.Models.SqlOSFgaResource>().Add(new()
            {
                Id = childId,
                ParentId = parentId,
                Name = "Bound child",
                ResourceTypeId = resourceTypeId
            });
            Context.Set<SqlOS.Fga.Models.SqlOSFgaSubject>().Add(new()
            {
                Id = subjectId,
                SubjectTypeId = subjectTypeId,
                DisplayName = "Bound subject"
            });
            await Context.SaveChangesAsync();

            Context.Set<SqlOS.Fga.Models.SqlOSFgaGrant>().Add(new()
            {
                Id = grantId,
                SubjectId = subjectId,
                ResourceId = childId,
                RoleId = roleId
            });
            await Context.SaveChangesAsync();
        }
        finally
        {
            Context.ChangeTracker.Clear();
        }
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

    private Task<bool> TableExistsAsync(string tableName)
        => TestCatalog.TableExistsAsync(Context, tableName);

    private Task<bool> ColumnExistsAsync(string tableName, string columnName)
        => TestCatalog.ColumnExistsAsync(Context, tableName, columnName);

    private async Task AssertIndexFitsProviderLimitsAsync(string tableName, string indexName)
    {
        Assert.IsTrue(await TestCatalog.IndexExistsAsync(Context, tableName, indexName), indexName);
        if (TestDatabase.IsSqlServer)
        {
            Assert.IsTrue(await IndexKeyWidthAsync(tableName, indexName) <= 1700, indexName);
        }
    }

    private async Task<int> IndexKeyWidthAsync(string tableName, string indexName)
    {
        var connection = Context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT COALESCE(SUM(c.max_length), 0)
                FROM sys.indexes i
                INNER JOIN sys.tables t ON i.object_id = t.object_id
                INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                WHERE t.name = @tableName
                  AND i.name = @indexName
                  AND t.schema_id = SCHEMA_ID('dbo')
                  AND ic.is_included_column = 0";
            cmd.Parameters.Add(new SqlParameter("@tableName", tableName));
            cmd.Parameters.Add(new SqlParameter("@indexName", indexName));
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private Task<bool> IndexExistsAsync(string tableName, string indexName)
        => TestCatalog.IndexExistsAsync(Context, tableName, indexName);

    private async Task<int> GetSchemaVersionAsync()
    {
        var connection = Context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = TestDatabase.Rewrite("SELECT TOP 1 [Version] FROM [dbo].[SqlOSFgaSchema]");
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
        var prefix = TestDatabase.IsPostgreSql
            ? "SqlOS.Fga.Schema.PostgreSql."
            : "SqlOS.Fga.Schema.";
        return SqlOS.Database.SqlOSMigrationManifest
            .Discover(typeof(SqlOSFgaSchemaInitializer).Assembly, prefix)
            .Max(script => script.Version);
    }
}
