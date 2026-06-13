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
                     "SqlOSUserPhoneNumbers",
                     "SqlOSCredentials",
                     "SqlOSPasswordLoginBuckets",
                     "SqlOSMemberships",
                     "SqlOSSsoConnections",
                     "SqlOSExternalIdentities",
                     "SqlOSClientApplications",
                     "SqlOSApplicationAssignments",
                     "SqlOSSessions",
                     "SqlOSRefreshTokens",
                     "SqlOSSigningKeys",
                     "SqlOSTemporaryTokens",
                     "SqlOSAuditEvents",
                     "SqlOSPhoneOtpChallenges",
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
                     "DisabledReason",
                     "AccessMode"
                 })
        {
            Assert.IsTrue(await ColumnExistsAsync("SqlOSClientApplications", column), $"Column SqlOSClientApplications.{column} should exist.");
        }

        Assert.IsTrue(await ColumnExistsAsync("SqlOSSessions", "Resource"), "Column SqlOSSessions.Resource should exist.");
        Assert.IsTrue(await ColumnExistsAsync("SqlOSSessions", "EffectiveAudience"), "Column SqlOSSessions.EffectiveAudience should exist.");
        Assert.IsTrue(await ColumnExistsAsync("SqlOSSessions", "OrganizationId"), "Column SqlOSSessions.OrganizationId should exist.");
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
        Assert.IsTrue(await ColumnExistsAsync("SqlOSClientApplications", "AccessMode"));
        Assert.IsTrue(await ColumnExistsAsync("SqlOSSessions", "EffectiveAudience"));
    }

    [TestMethod]
    public async Task EmailSchemaInitializer_CreatesTemplatesAndDeliveries()
    {
        var initializer = new SqlOSSchemaInitializer(
            AspireFixture.SharedContext,
            Options.Create(AspireFixture.Options),
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());

        await initializer.EnsureSchemaAsync();

        Assert.IsTrue(await TableExistsAsync("SqlOSEmailTemplates"), "Table SqlOSEmailTemplates should exist.");
        Assert.IsTrue(await TableExistsAsync("SqlOSEmailDeliveries"), "Table SqlOSEmailDeliveries should exist.");
        Assert.IsTrue(await ColumnExistsAsync("SqlOSEmailTemplates", "VariablesJson"));
        Assert.IsTrue(await ColumnExistsAsync("SqlOSEmailDeliveries", "RenderedTextPreview"));
        Assert.IsTrue(await ColumnExistsAsync("SqlOSEmailDeliveries", "IdempotencyKey"));
    }

    [TestMethod]
    public async Task EnsureSchema_UpgradesVersion22AuditEventsSchema()
    {
        var databaseName = $"SqlOSUpgrade_{Guid.NewGuid():N}"[..30];
        var databaseConnectionString = BuildDatabaseConnectionString(databaseName);
        await CreateDatabaseAsync(databaseName);

        try
        {
            var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseSqlServer(databaseConnectionString)
                .Options;

            await using var context = new TestSqlOSDbContext(dbOptions);
            await SeedVersion22AuditEventsSchemaAsync(context);

            var initializer = new SqlOSSchemaInitializer(
                context,
                Options.Create(AspireFixture.Options),
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());

            await initializer.EnsureSchemaAsync();

            Assert.IsTrue(await ColumnExistsAsync(context, "SqlOSAuditEvents", "Action"));
            Assert.IsTrue(await ColumnExistsAsync(context, "SqlOSAuditEvents", "IdempotencyKeyHash"));
            Assert.IsTrue(await IndexExistsAsync(context, "SqlOSAuditEvents", "IX_SqlOSAuditEvents_Action_OccurredAt"));
            Assert.IsTrue(await IndexExistsAsync(context, "SqlOSAuditEvents", "UX_SqlOSAuditEvents_IdempotencyKeyHash"));
            Assert.AreEqual("user.login", await ScalarStringAsync(context, "SELECT TOP 1 [Action] FROM [dbo].[SqlOSAuditEvents]"));
            Assert.AreEqual(24, await ScalarIntAsync(context, "SELECT TOP 1 [Version] FROM [dbo].[SqlOSSchema]"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [TestMethod]
    public async Task EnsureSchema_RepairsVersion23MissingApplicationAssignmentsSchema()
    {
        var databaseName = $"SqlOSRepair_{Guid.NewGuid():N}"[..30];
        var databaseConnectionString = BuildDatabaseConnectionString(databaseName);
        await CreateDatabaseAsync(databaseName);

        try
        {
            var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseSqlServer(databaseConnectionString)
                .Options;

            await using var context = new TestSqlOSDbContext(dbOptions);
            await SeedVersion23WithoutApplicationAssignmentsSchemaAsync(context);

            var initializer = new SqlOSSchemaInitializer(
                context,
                Options.Create(AspireFixture.Options),
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());

            await initializer.EnsureSchemaAsync();

            Assert.IsTrue(await ColumnExistsAsync(context, "SqlOSClientApplications", "AccessMode"));
            Assert.IsTrue(await ColumnExistsAsync(context, "SqlOSSessions", "OrganizationId"));
            Assert.IsTrue(await TableExistsAsync(context, "SqlOSApplicationAssignments"));
            Assert.IsTrue(await ForeignKeyExistsAsync(context, "SqlOSSessions", "FK_SqlOSSessions_Organizations"));
            Assert.IsTrue(await IndexExistsAsync(context, "SqlOSClientApplications", "IX_SqlOSClientApplications_AccessMode"));
            Assert.IsTrue(await IndexExistsAsync(context, "SqlOSApplicationAssignments", "IX_SqlOSApplicationAssignments_Target"));
            Assert.IsTrue(await IndexExistsAsync(context, "SqlOSApplicationAssignments", "IX_SqlOSApplicationAssignments_ClientApplicationId_RevokedAt"));
            Assert.IsTrue(await IndexExistsAsync(context, "SqlOSApplicationAssignments", "IX_SqlOSApplicationAssignments_OrganizationId_RevokedAt"));
            Assert.AreEqual(24, await ScalarIntAsync(context, "SELECT TOP 1 [Version] FROM [dbo].[SqlOSSchema]"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    private static async Task<bool> TableExistsAsync(string tableName)
        => await TableExistsAsync(AspireFixture.SharedContext, tableName);

    private static async Task<bool> TableExistsAsync(DbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
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

    private static async Task<bool> ForeignKeyExistsAsync(DbContext context, string tableName, string foreignKeyName)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*)
                FROM sys.foreign_keys fk
                INNER JOIN sys.tables t ON fk.parent_object_id = t.object_id
                WHERE t.name = @tableName
                  AND fk.name = @foreignKeyName
                  AND t.schema_id = SCHEMA_ID('dbo')
                """;
            cmd.Parameters.Add(new SqlParameter("@tableName", tableName));
            cmd.Parameters.Add(new SqlParameter("@foreignKeyName", foreignKeyName));
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result) > 0;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task<bool> ColumnExistsAsync(string tableName, string columnName)
        => await ColumnExistsAsync(AspireFixture.SharedContext, tableName, columnName);

    private static async Task<bool> ColumnExistsAsync(DbContext context, string tableName, string columnName)
    {
        var connection = context.Database.GetDbConnection();
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

    private static async Task<bool> IndexExistsAsync(DbContext context, string tableName, string indexName)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*)
                FROM sys.indexes i
                INNER JOIN sys.tables t ON i.object_id = t.object_id
                WHERE t.name = @tableName
                  AND i.name = @indexName
                  AND t.schema_id = SCHEMA_ID('dbo')
                """;
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

    private static async Task SeedVersion22AuditEventsSchemaAsync(DbContext context)
    {
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE [dbo].[SqlOSSchema] ([Version] INT NOT NULL);
            INSERT INTO [dbo].[SqlOSSchema] ([Version]) VALUES (22);

            CREATE TABLE [dbo].[SqlOSAuditEvents] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [OrganizationId] NVARCHAR(64) NULL,
                [UserId] NVARCHAR(64) NULL,
                [SessionId] NVARCHAR(64) NULL,
                [EventType] NVARCHAR(120) NOT NULL,
                [ActorType] NVARCHAR(80) NOT NULL,
                [ActorId] NVARCHAR(64) NULL,
                [OccurredAt] DATETIME2 NOT NULL,
                [IpAddress] NVARCHAR(128) NULL,
                [DataJson] NVARCHAR(MAX) NULL
            );

            INSERT INTO [dbo].[SqlOSAuditEvents] (
                [Id],
                [EventType],
                [ActorType],
                [OccurredAt]
            )
            VALUES (
                'evt_upgrade_audit',
                'user.login',
                'user',
                SYSUTCDATETIME()
            );
            """);
    }

    private static async Task SeedVersion23WithoutApplicationAssignmentsSchemaAsync(DbContext context)
    {
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE [dbo].[SqlOSSchema] ([Version] INT NOT NULL);
            INSERT INTO [dbo].[SqlOSSchema] ([Version]) VALUES (23);

            CREATE TABLE [dbo].[SqlOSOrganizations] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY
            );

            CREATE TABLE [dbo].[SqlOSClientApplications] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY
            );

            CREATE TABLE [dbo].[SqlOSSessions] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY
            );
            """);
    }

    private static async Task<string?> ScalarStringAsync(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            var result = await cmd.ExecuteScalarAsync();
            return result == DBNull.Value ? null : Convert.ToString(result);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task<int> ScalarIntAsync(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static string BuildDatabaseConnectionString(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(AspireFixture.SqlConnectionString)
        {
            InitialCatalog = databaseName
        };
        return builder.ConnectionString;
    }

    private static string BuildMasterConnectionString()
    {
        var builder = new SqlConnectionStringBuilder(AspireFixture.SqlConnectionString)
        {
            InitialCatalog = "master"
        };
        return builder.ConnectionString;
    }

    private static async Task CreateDatabaseAsync(string databaseName)
    {
        await using var connection = new SqlConnection(BuildMasterConnectionString());
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{databaseName}]";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string databaseName)
    {
        await using var connection = new SqlConnection(BuildMasterConnectionString());
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}
