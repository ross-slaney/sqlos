using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class SchemaInitializerIntegrationTests
{
    private const int CurrentSchemaVersion = 41;

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
                     "SqlOSPasswordLoginReservations",
                     "SqlOSPasswordLoginReservationBuckets",
                     "SqlOSMfaAttemptBuckets",
                     "SqlOSMfaAttemptReservations",
                     "SqlOSMfaAttemptReservationBuckets",
                     "SqlOSMemberships",
                     "SqlOSSsoConnections",
                     "SqlOSExternalIdentities",
                     "SqlOSClientApplications",
                     "SqlOSClientCredentials",
                     "SqlOSApplicationAssignments",
                     "SqlOSSessions",
                     "SqlOSRefreshTokens",
                     "SqlOSSigningKeys",
                     "SqlOSTemporaryTokens",
                     "SqlOSAuditEvents",
                     "SqlOSScimConnections",
                     "SqlOSScimExternalIds",
                     "SqlOSScimGroupMappings",
                     "SqlOSScimManagedGrants",
                     "SqlOSScimSyncEvents",
                     "SqlOSScimOperationCommits",
                     "SqlOSRateLimitBuckets",
                     "SqlOSPhoneOtpChallenges",
                     "SqlOSCalendarConnections",
                     "SqlOSCalendarSyncStates",
                     "SqlOSCalendarEvents",
                     "SqlOSSchema",
                     "SqlOSAppliedMigrations"
                 })
        {
            Assert.IsTrue(await TableExistsAsync(table), $"Table {table} should exist.");
        }

        Assert.IsTrue(await ColumnExistsAsync(
            "SqlOSSsoConnections",
            "AcceptedAuthnContextClassRefsJson"));
        Assert.IsTrue(await ColumnExistsAsync(
            "SqlOSAuthOidcConnections",
            "AcceptedAmrValuesJson"));
        Assert.IsTrue(await ColumnExistsAsync(
            "SqlOSAuthOidcConnections",
            "AcceptedAcrValuesJson"));
        foreach (var table in new[] { "SqlOSClientApplications", "SqlOSAuthOidcConnections", "SqlOSScimConnections", "SqlOSMfaSettings" })
        {
            foreach (var column in new[] { "ConfigurationOwner", "ConfigurationSourceKey", "ConfigurationFingerprint", "LastReconciledAt", "ConfigurationOrphanedAt" })
            {
                Assert.IsTrue(await ColumnExistsAsync(table, column), $"Column {table}.{column} should exist.");
            }
        }

        Assert.IsTrue(
            await IndexExistsAsync(AspireFixture.SharedContext, "SqlOSUsers", "IX_SqlOSUsers_DisplayName_Id"),
            "User lists need a DisplayName+Id keyset index.");
        Assert.IsTrue(
            await IndexExistsAsync(AspireFixture.SharedContext, "SqlOSOrganizations", "IX_SqlOSOrganizations_Name_Id"),
            "Organization lists need a Name+Id keyset index.");
        Assert.IsTrue(
            await IndexExistsAsync(AspireFixture.SharedContext, "SqlOSClientApplications", "IX_SqlOSClientApplications_Name_Id"),
            "Client lists need a Name+Id keyset index.");
        Assert.IsTrue(
            await IndexExistsAsync(AspireFixture.SharedContext, "SqlOSAuditEvents", "IX_SqlOSAuditEvents_OccurredAt_IngestedAt_Id"),
            "Audit lists need an OccurredAt+IngestedAt+Id keyset index.");
    }

    [TestMethod]
    public async Task EnsureSchema_ResumesMissingSameVersionScripts_AndRecordsDeterministicOrder()
    {
        var databaseName = $"SqlOSLedger_{Guid.NewGuid():N}"[..30];
        var databaseConnectionString = BuildDatabaseConnectionString(databaseName);
        await CreateDatabaseAsync(databaseName);

        try
        {
            var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseSqlServer(databaseConnectionString, sql => sql.EnableRetryOnFailure())
                .Options;
            await using var context = new TestSqlOSDbContext(dbOptions);
            var initializer = new SqlOSSchemaInitializer(
                context,
                Options.Create(AspireFixture.Options),
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());

            await initializer.EnsureSchemaAsync();

            var initialOrder = await ReadAppliedMigrationOrderAsync(context);
            var expectedOrder = initialOrder
                .OrderBy(ParseMigrationVersion)
                .ThenBy(x => x, StringComparer.Ordinal)
                .ToList();
            CollectionAssert.AreEqual(expectedOrder, initialOrder,
                "Migration order must be version-first and script-name deterministic.");

            await context.Database.ExecuteSqlRawAsync("""
                DROP TABLE [dbo].[SqlOSEmailDeliveries];
                DROP TABLE [dbo].[SqlOSEmailTemplates];
                DROP TABLE [dbo].[SqlOSMfaAttemptReservationBuckets];
                DROP TABLE [dbo].[SqlOSMfaAttemptReservations];
                DROP TABLE [dbo].[SqlOSMfaAttemptBuckets];
                DROP TABLE [dbo].[SqlOSPasswordLoginReservationBuckets];
                DROP TABLE [dbo].[SqlOSPasswordLoginReservations];
                DROP TABLE [dbo].[SqlOSPasswordLoginBuckets];
                DELETE FROM [dbo].[SqlOSAppliedMigrations]
                WHERE [ScriptName] IN (
                    'SqlOS.AuthServer.Schema.017_PasswordLoginAbuse.sql',
                    'SqlOS.AuthServer.Schema.017_TransactionalEmail.sql',
                    'SqlOS.AuthServer.Schema.039_AtomicPasswordLoginAdmission.sql',
                    'SqlOS.AuthServer.Schema.040_MfaAttemptAdmission.sql');
                UPDATE [dbo].[SqlOSSchema] SET [Version] = 17;
                """);

            await initializer.EnsureSchemaAsync();

            Assert.AreEqual(3, await ScalarIntAsync(context,
                "SELECT COUNT(*) FROM [dbo].[SqlOSAppliedMigrations] WHERE [Version] = 17"));
            Assert.AreEqual(1, await ScalarIntAsync(context,
                "SELECT COUNT(*) FROM sys.tables WHERE [name] = 'SqlOSPasswordLoginBuckets'"));
            Assert.AreEqual(1, await ScalarIntAsync(context,
                "SELECT COUNT(*) FROM sys.tables WHERE [name] = 'SqlOSPasswordLoginReservations'"));
            Assert.AreEqual(1, await ScalarIntAsync(context,
                "SELECT COUNT(*) FROM sys.tables WHERE [name] = 'SqlOSPasswordLoginReservationBuckets'"));
            Assert.AreEqual(1, await ScalarIntAsync(context,
                "SELECT COUNT(*) FROM sys.tables WHERE [name] = 'SqlOSMfaAttemptBuckets'"));
            Assert.AreEqual(1, await ScalarIntAsync(context,
                "SELECT COUNT(*) FROM sys.tables WHERE [name] = 'SqlOSMfaAttemptReservations'"));
            Assert.AreEqual(1, await ScalarIntAsync(context,
                "SELECT COUNT(*) FROM sys.tables WHERE [name] = 'SqlOSMfaAttemptReservationBuckets'"));
            Assert.AreEqual(0, await ScalarIntAsync(context, """
                SELECT COUNT(*) FROM sys.indexes
                WHERE [name] = 'IX_SqlOSPasswordLoginBuckets_ClientKey_UpdatedAt'
                  AND [object_id] = OBJECT_ID('[dbo].[SqlOSPasswordLoginBuckets]')
                """));
            Assert.AreEqual(1, await ScalarIntAsync(context,
                "SELECT COUNT(*) FROM sys.tables WHERE [name] = 'SqlOSEmailTemplates'"));
            Assert.AreEqual(1, await ScalarIntAsync(context,
                "SELECT COUNT(*) FROM sys.tables WHERE [name] = 'SqlOSEmailDeliveries'"));
            Assert.AreEqual(CurrentSchemaVersion, await ScalarIntAsync(context, "SELECT TOP 1 [Version] FROM [dbo].[SqlOSSchema]"));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await DropDatabaseAsync(databaseName);
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
        Assert.IsTrue(
            await IndexExistsAsync(AspireFixture.SharedContext, "SqlOSAuthorizationCodes", "IX_SqlOSAuthorizationCodes_AuthorizationRequestId"),
            "SqlOSAuthorizationCodes.AuthorizationRequestId should be uniquely indexed.");
        Assert.IsTrue(
            await ColumnExistsAsync("SqlOSSigningKeys", "CustodyProvider"),
            "SqlOSSigningKeys.CustodyProvider should identify the private-key custody backend.");
        Assert.IsTrue(
            await ColumnExistsAsync("SqlOSSigningKeys", "KeyReference"),
            "SqlOSSigningKeys.KeyReference should contain only an opaque custody reference.");
        Assert.IsFalse(
            await ColumnExistsAsync("SqlOSSigningKeys", "PrivateKeyPem"),
            "SqlOSSigningKeys must not retain a private-key PEM column.");
        Assert.IsTrue(
            await IndexExistsAsync(AspireFixture.SharedContext, "SqlOSSigningKeys", "UX_SqlOSSigningKeys_OneActive"),
            "SqlOSSigningKeys should enforce a single active key at the database boundary.");
        Assert.AreEqual(
            4096,
            await ScalarIntAsync(
                AspireFixture.SharedContext,
                "SELECT COL_LENGTH('dbo.SqlOSAuthorizationRequests', 'State')"),
            "OAuth state must accommodate ASP.NET Core's protected correlation state.");
        Assert.AreEqual(
            4096,
            await ScalarIntAsync(
                AspireFixture.SharedContext,
                "SELECT COL_LENGTH('dbo.SqlOSAuthorizationCodes', 'State')"),
            "Authorization codes must preserve the complete OAuth state value.");
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
            await SeedScimMigrationPrerequisitesAsync(context);

            var initializer = new SqlOSSchemaInitializer(
                context,
                Options.Create(AspireFixture.Options),
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());

            await initializer.EnsureSchemaAsync();

            Assert.IsTrue(await ColumnExistsAsync(context, "SqlOSAuditEvents", "Action"));
            Assert.IsTrue(await ColumnExistsAsync(context, "SqlOSAuditEvents", "IdempotencyKeyHash"));
            Assert.IsTrue(await ColumnExistsAsync(context, "SqlOSAuditEvents", "IdempotencyScopeHash"));
            Assert.IsTrue(await IndexExistsAsync(context, "SqlOSAuditEvents", "IX_SqlOSAuditEvents_Action_OccurredAt"));
            Assert.IsTrue(await IndexExistsAsync(context, "SqlOSAuditEvents", "UX_SqlOSAuditEvents_IdempotencyKeyHash"));
            Assert.IsTrue(await IndexExistsAsync(context, "SqlOSAuditEvents", "UX_SqlOSAuditEvents_IdempotencyScopeHash"));
            Assert.AreEqual("user.login", await ScalarStringAsync(context, "SELECT TOP 1 [Action] FROM [dbo].[SqlOSAuditEvents]"));
            Assert.AreEqual(CurrentSchemaVersion, await ScalarIntAsync(context, "SELECT TOP 1 [Version] FROM [dbo].[SqlOSSchema]"));
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
            await SeedScimMigrationPrerequisitesAsync(context);

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
            Assert.AreEqual(CurrentSchemaVersion, await ScalarIntAsync(context, "SELECT TOP 1 [Version] FROM [dbo].[SqlOSSchema]"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [TestMethod]
    public async Task EnsureSchema_UpgradesVersion29ScimSchemaInPlace()
    {
        var databaseName = $"SqlOSScimUpgrade_{Guid.NewGuid():N}"[..30];
        var databaseConnectionString = BuildDatabaseConnectionString(databaseName);
        await CreateDatabaseAsync(databaseName);

        try
        {
            var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseSqlServer(databaseConnectionString)
                .Options;

            await using var context = new TestSqlOSDbContext(dbOptions);
            await SeedVersion29ScimSchemaAsync(context);

            var initializer = new SqlOSSchemaInitializer(
                context,
                Options.Create(AspireFixture.Options),
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());

            await initializer.EnsureSchemaAsync();
            await initializer.EnsureSchemaAsync();

            Assert.AreEqual(CurrentSchemaVersion, await ScalarIntAsync(context, "SELECT TOP 1 [Version] FROM [dbo].[SqlOSSchema]"));
            foreach (var column in new[] { "UserName", "PrimaryEmail", "FormattedName", "GivenName", "FamilyName", "DeletedAt", "OwnsUserLifecycle" })
            {
                Assert.IsTrue(await ColumnExistsAsync(context, "SqlOSScimExternalIds", column));
            }

            Assert.AreEqual(
                1,
                await ScalarIntAsync(
                    context,
                    "SELECT CAST([is_nullable] AS INT) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.SqlOSScimExternalIds') AND [name] = 'ExternalId'"));
            Assert.AreEqual(
                1,
                await ScalarIntAsync(
                    context,
                    "SELECT CAST([is_unique] AS INT) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.SqlOSScimExternalIds') AND [name] = 'IX_SqlOSScimExternalIds_Connection_Resource_External'"));
            Assert.AreEqual(
                1,
                await ScalarIntAsync(
                    context,
                    "SELECT CAST([has_filter] AS INT) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.SqlOSScimExternalIds') AND [name] = 'IX_SqlOSScimExternalIds_Connection_Resource_External'"));
            Assert.AreEqual(
                1,
                await ScalarIntAsync(
                    context,
                    "SELECT CAST([is_unique] AS INT) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.SqlOSScimExternalIds') AND [name] = 'IX_SqlOSScimExternalIds_Connection_Resource_Entity'"));
            Assert.AreEqual(
                1,
                await ScalarIntAsync(
                    context,
                    "SELECT CAST([is_unique] AS INT) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.SqlOSScimExternalIds') AND [name] = 'IX_SqlOSScimExternalIds_Connection_Resource_UserName'"));
            Assert.AreEqual(
                1,
                await ScalarIntAsync(
                    context,
                    "SELECT CAST([is_unique] AS INT) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.SqlOSScimConnections') AND [name] = 'UX_SqlOSScimConnections_OneEnabledPerOrganization'"));

            Assert.AreEqual(
                1,
                await ScalarIntAsync(context, "SELECT COUNT(*) FROM [dbo].[SqlOSScimExternalIds] WHERE [EntityId] = 'user_alice'"));
            Assert.AreEqual(
                "alice-new",
                await ScalarStringAsync(context, "SELECT [ExternalId] FROM [dbo].[SqlOSScimExternalIds] WHERE [EntityId] = 'user_alice'"));
            Assert.AreEqual(
                "alice@example.test",
                await ScalarStringAsync(context, "SELECT [UserName] FROM [dbo].[SqlOSScimExternalIds] WHERE [EntityId] = 'user_alice'"));
            Assert.AreEqual(
                "alice@example.test",
                await ScalarStringAsync(context, "SELECT [PrimaryEmail] FROM [dbo].[SqlOSScimExternalIds] WHERE [EntityId] = 'user_alice'"));
            Assert.AreEqual(
                "Alice Example",
                await ScalarStringAsync(context, "SELECT [DisplayName] FROM [dbo].[SqlOSScimExternalIds] WHERE [EntityId] = 'user_alice'"));
            Assert.AreEqual(
                "Alice Example",
                await ScalarStringAsync(context, "SELECT [FormattedName] FROM [dbo].[SqlOSScimExternalIds] WHERE [EntityId] = 'user_alice'"));
            Assert.AreEqual(
                "sqlos-migrated-link_bob",
                await ScalarStringAsync(context, "SELECT [UserName] FROM [dbo].[SqlOSScimExternalIds] WHERE [EntityId] = 'user_bob'"));
            Assert.AreEqual(
                0,
                await ScalarIntAsync(context, "SELECT CAST([OwnsUserLifecycle] AS INT) FROM [dbo].[SqlOSScimExternalIds] WHERE [EntityId] = 'user_bob'"));
            Assert.AreEqual(
                "shared@example.test",
                await ScalarStringAsync(context, "SELECT [UserName] FROM [dbo].[SqlOSScimExternalIds] WHERE [EntityId] = 'user_carol'"));
            Assert.AreEqual(
                1,
                await ScalarIntAsync(context, "SELECT COUNT(*) FROM [dbo].[SqlOSScimConnections] WHERE [OrganizationId] = 'org_upgrade' AND [IsEnabled] = 1"));
            Assert.AreEqual(
                "conn_new",
                await ScalarStringAsync(context, "SELECT [Id] FROM [dbo].[SqlOSScimConnections] WHERE [OrganizationId] = 'org_upgrade' AND [IsEnabled] = 1"));
            Assert.AreEqual(
                1,
                await ScalarIntAsync(context, "SELECT COUNT(*) FROM [dbo].[SqlOSFgaGrants] WHERE [Id] = 'grant_disabled'"));
            Assert.AreEqual(
                1,
                await ScalarIntAsync(context, "SELECT COUNT(*) FROM [dbo].[SqlOSScimManagedGrants] WHERE [GrantId] = 'grant_disabled' AND [RevokedAt] IS NULL"));
            Assert.AreEqual(
                1,
                await ScalarIntAsync(context, "SELECT COUNT(*) FROM [dbo].[SqlOSFgaGrants] WHERE [Id] = 'grant_enabled'"));
            Assert.AreEqual(
                1,
                await ScalarIntAsync(context, "SELECT COUNT(*) FROM [dbo].[SqlOSScimManagedGrants] WHERE [GrantId] = 'grant_enabled' AND [RevokedAt] IS NULL"));

            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO [dbo].[SqlOSScimExternalIds] (
                    [Id], [ConnectionId], [ResourceType], [ExternalId], [EntityId],
                    [FgaSubjectId], [DisplayName], [IsActive], [CreatedAt], [UpdatedAt], [LastSyncedAt])
                VALUES
                    ('ext_null_1', 'conn_new', 'User', NULL, 'user_null_1', NULL, NULL, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME()),
                    ('ext_null_2', 'conn_new', 'User', NULL, 'user_null_2', NULL, NULL, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            Assert.AreEqual(
                2,
                await ScalarIntAsync(context, "SELECT COUNT(*) FROM [dbo].[SqlOSScimExternalIds] WHERE [ExternalId] IS NULL"));

            await Assert.ThrowsExceptionAsync<SqlException>(async () =>
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE [dbo].[SqlOSScimConnections] SET [IsEnabled] = 1 WHERE [Id] = 'conn_old'"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [TestMethod]
    public async Task EnsureSchema_UpgradesVersion26OAuthStateColumns()
    {
        var databaseName = $"SqlOSState_{Guid.NewGuid():N}"[..30];
        var databaseConnectionString = BuildDatabaseConnectionString(databaseName);
        await CreateDatabaseAsync(databaseName);

        try
        {
            var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseSqlServer(databaseConnectionString)
                .Options;

            await using var context = new TestSqlOSDbContext(dbOptions);
            await SeedVersion26OAuthStateSchemaAsync(context);
            await SeedScimMigrationPrerequisitesAsync(context);

            var initializer = new SqlOSSchemaInitializer(
                context,
                Options.Create(AspireFixture.Options),
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());

            await initializer.EnsureSchemaAsync();

            Assert.AreEqual(
                4096,
                await ScalarIntAsync(
                    context,
                    "SELECT COL_LENGTH('dbo.SqlOSAuthorizationRequests', 'State')"));
            Assert.AreEqual(
                4096,
                await ScalarIntAsync(
                    context,
                    "SELECT COL_LENGTH('dbo.SqlOSAuthorizationCodes', 'State')"));
            Assert.AreEqual(
                new string('s', 256),
                await ScalarStringAsync(
                    context,
                    "SELECT [State] FROM [dbo].[SqlOSAuthorizationRequests] WHERE [Id] = 'req_state_upgrade'"));
            Assert.AreEqual(
                CurrentSchemaVersion,
                await ScalarIntAsync(context, "SELECT TOP 1 [Version] FROM [dbo].[SqlOSSchema]"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [TestMethod]
    public async Task EnsureSchema_DoesNotNarrowHotfixedOAuthStateColumns()
    {
        var databaseName = $"SqlOSStateWide_{Guid.NewGuid():N}"[..30];
        var databaseConnectionString = BuildDatabaseConnectionString(databaseName);
        await CreateDatabaseAsync(databaseName);

        try
        {
            var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseSqlServer(databaseConnectionString)
                .Options;

            await using var context = new TestSqlOSDbContext(dbOptions);
            await SeedVersion26WideOAuthStateSchemaAsync(context);
            await SeedScimMigrationPrerequisitesAsync(context);

            var initializer = new SqlOSSchemaInitializer(
                context,
                Options.Create(AspireFixture.Options),
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());

            await initializer.EnsureSchemaAsync();

            Assert.AreEqual(
                8000,
                await ScalarIntAsync(
                    context,
                    "SELECT COL_LENGTH('dbo.SqlOSAuthorizationRequests', 'State')"));
            Assert.AreEqual(
                -1,
                await ScalarIntAsync(
                    context,
                    "SELECT COL_LENGTH('dbo.SqlOSAuthorizationCodes', 'State')"));
            Assert.AreEqual(
                3000,
                await ScalarIntAsync(
                    context,
                    "SELECT LEN([State]) FROM [dbo].[SqlOSAuthorizationRequests] WHERE [Id] = 'req_state_wide'"));
            Assert.AreEqual(
                CurrentSchemaVersion,
                await ScalarIntAsync(context, "SELECT TOP 1 [Version] FROM [dbo].[SqlOSSchema]"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [TestMethod]
    public async Task EnsureSchema_Version26PlaintextSigningKey_RenamesReferenceAndStartupRejectsIt()
    {
        var databaseName = $"SqlOSKeyUpgrade_{Guid.NewGuid():N}"[..30];
        var databaseConnectionString = BuildDatabaseConnectionString(databaseName);
        await CreateDatabaseAsync(databaseName);

        try
        {
            var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseSqlServer(databaseConnectionString)
                .Options;
            await using var context = new TestSqlOSDbContext(dbOptions);
            using var rsa = RSA.Create(2048);
            var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
            var publicKeyPem = rsa.ExportRSAPublicKeyPem();
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE [dbo].[SqlOSSchema] ([Version] INT NOT NULL);
                INSERT INTO [dbo].[SqlOSSchema] ([Version]) VALUES (26);

                CREATE TABLE [dbo].[SqlOSSigningKeys] (
                    [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                    [Kid] NVARCHAR(120) NOT NULL UNIQUE,
                    [Algorithm] NVARCHAR(20) NOT NULL,
                    [PublicKeyPem] NVARCHAR(MAX) NOT NULL,
                    [PrivateKeyPem] NVARCHAR(MAX) NOT NULL,
                    [IsActive] BIT NOT NULL,
                    [ActivatedAt] DATETIME2 NOT NULL,
                    [RetiredAt] DATETIME2 NULL
                );
                """);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [dbo].[SqlOSSigningKeys] (
                    [Id], [Kid], [Algorithm], [PublicKeyPem], [PrivateKeyPem],
                    [IsActive], [ActivatedAt], [RetiredAt])
                VALUES (
                    {"key_legacy_plaintext"}, {"legacy-plaintext-kid"},
                    {SecurityAlgorithms.RsaSha256}, {publicKeyPem}, {privateKeyPem},
                    {true}, {DateTime.UtcNow}, NULL);
                """);
            await SeedScimMigrationPrerequisitesAsync(context);

            var initializer = new SqlOSSchemaInitializer(
                context,
                Options.Create(AspireFixture.Options),
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSSchemaInitializer>());
            await initializer.EnsureSchemaAsync();

            Assert.IsTrue(await ColumnExistsAsync(context, "SqlOSSigningKeys", "KeyReference"));
            Assert.IsFalse(await ColumnExistsAsync(context, "SqlOSSigningKeys", "PrivateKeyPem"));
            Assert.AreEqual(
                "legacy-unprotected",
                await ScalarStringAsync(context, "SELECT TOP 1 [CustodyProvider] FROM [dbo].[SqlOSSigningKeys]"));
            Assert.AreEqual(
                privateKeyPem,
                await ScalarStringAsync(context, "SELECT TOP 1 [KeyReference] FROM [dbo].[SqlOSSigningKeys]"));

            var crypto = new SqlOSCryptoService(
                context,
                Options.Create(AspireFixture.Options),
                AspireFixture.DataProtectionProvider);
            var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => crypto.EnsureActiveSigningKeyAsync());
            StringAssert.Contains(exception.Message, "contains plaintext private key material");
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

            CREATE TABLE [dbo].[SqlOSOrganizations] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [Slug] NVARCHAR(120) NOT NULL,
                [Name] NVARCHAR(200) NOT NULL,
                [PrimaryDomain] NVARCHAR(255) NULL,
                [IsActive] BIT NOT NULL,
                [CreatedAt] DATETIME2 NOT NULL
            );

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

    private static async Task SeedScimMigrationPrerequisitesAsync(DbContext context)
    {
        await context.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID('dbo.SqlOSOrganizations', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[SqlOSOrganizations] (
                    [Id] NVARCHAR(64) NOT NULL PRIMARY KEY
                );
            END

            IF OBJECT_ID('dbo.SqlOSUsers', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[SqlOSUsers] (
                    [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                    [DisplayName] NVARCHAR(200) NOT NULL,
                    [DefaultEmail] NVARCHAR(320) NULL,
                    [IsActive] BIT NOT NULL,
                    [CreatedAt] DATETIME2 NOT NULL,
                    [UpdatedAt] DATETIME2 NOT NULL
                );
            END
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

    private static async Task SeedVersion29ScimSchemaAsync(DbContext context)
    {
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE [dbo].[SqlOSSchema] ([Version] INT NOT NULL);
            INSERT INTO [dbo].[SqlOSSchema] ([Version]) VALUES (29);

            CREATE TABLE [dbo].[SqlOSOrganizations] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [Slug] NVARCHAR(120) NOT NULL,
                [Name] NVARCHAR(200) NOT NULL,
                [PrimaryDomain] NVARCHAR(255) NULL,
                [IsActive] BIT NOT NULL,
                [CreatedAt] DATETIME2 NOT NULL
            );

            CREATE TABLE [dbo].[SqlOSUsers] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [DisplayName] NVARCHAR(200) NOT NULL,
                [DefaultEmail] NVARCHAR(320) NULL,
                [IsActive] BIT NOT NULL,
                [CreatedAt] DATETIME2 NOT NULL,
                [UpdatedAt] DATETIME2 NOT NULL
            );

            CREATE TABLE [dbo].[SqlOSScimConnections] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [OrganizationId] NVARCHAR(64) NOT NULL,
                [SeedKey] NVARCHAR(160) NULL,
                [DisplayName] NVARCHAR(200) NOT NULL,
                [IsEnabled] BIT NOT NULL,
                [TokenHash] NVARCHAR(128) NULL,
                [TokenPrefix] NVARCHAR(24) NULL,
                [TokenRotatedAt] DATETIME2 NULL,
                [TokenLastUsedAt] DATETIME2 NULL,
                [LastSyncAt] DATETIME2 NULL,
                [Source] NVARCHAR(40) NOT NULL,
                [CreatedAt] DATETIME2 NOT NULL,
                [UpdatedAt] DATETIME2 NOT NULL
            );

            CREATE UNIQUE INDEX [IX_SqlOSScimConnections_OrganizationId_SeedKey]
                ON [dbo].[SqlOSScimConnections]([OrganizationId], [SeedKey])
                WHERE [SeedKey] IS NOT NULL;
            CREATE UNIQUE INDEX [IX_SqlOSScimConnections_TokenHash]
                ON [dbo].[SqlOSScimConnections]([TokenHash])
                WHERE [TokenHash] IS NOT NULL;
            CREATE INDEX [IX_SqlOSScimConnections_OrganizationId_IsEnabled]
                ON [dbo].[SqlOSScimConnections]([OrganizationId], [IsEnabled]);
            ALTER TABLE [dbo].[SqlOSScimConnections]
                ADD CONSTRAINT [FK_SqlOSScimConnections_Organizations_OrganizationId]
                    FOREIGN KEY ([OrganizationId]) REFERENCES [dbo].[SqlOSOrganizations]([Id]);

            CREATE TABLE [dbo].[SqlOSScimExternalIds] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [ConnectionId] NVARCHAR(64) NOT NULL,
                [ResourceType] NVARCHAR(20) NOT NULL,
                [ExternalId] NVARCHAR(450) NOT NULL,
                [EntityId] NVARCHAR(128) NOT NULL,
                [FgaSubjectId] NVARCHAR(128) NULL,
                [DisplayName] NVARCHAR(300) NULL,
                [IsActive] BIT NOT NULL,
                [CreatedAt] DATETIME2 NOT NULL,
                [UpdatedAt] DATETIME2 NOT NULL,
                [LastSyncedAt] DATETIME2 NOT NULL
            );

            CREATE UNIQUE INDEX [IX_SqlOSScimExternalIds_Connection_Resource_External]
                ON [dbo].[SqlOSScimExternalIds]([ConnectionId], [ResourceType], [ExternalId]);
            CREATE INDEX [IX_SqlOSScimExternalIds_Connection_Resource_Entity]
                ON [dbo].[SqlOSScimExternalIds]([ConnectionId], [ResourceType], [EntityId]);
            ALTER TABLE [dbo].[SqlOSScimExternalIds]
                ADD CONSTRAINT [FK_SqlOSScimExternalIds_Connections_ConnectionId]
                    FOREIGN KEY ([ConnectionId]) REFERENCES [dbo].[SqlOSScimConnections]([Id]);

            CREATE TABLE [dbo].[SqlOSScimGroupMappings] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [ConnectionId] NVARCHAR(64) NOT NULL,
                [SourceKey] NVARCHAR(300) NULL,
                [Source] NVARCHAR(40) NOT NULL,
                [MatchType] NVARCHAR(40) NOT NULL,
                [GroupDisplayName] NVARCHAR(300) NULL,
                [GroupExternalId] NVARCHAR(450) NULL,
                [GroupPattern] NVARCHAR(500) NULL,
                [RoleKey] NVARCHAR(120) NOT NULL,
                [ResourceId] NVARCHAR(256) NULL,
                [ResourceIdTemplate] NVARCHAR(500) NULL,
                [Description] NVARCHAR(500) NULL,
                [IsEnabled] BIT NOT NULL,
                [CreatedAt] DATETIME2 NOT NULL,
                [UpdatedAt] DATETIME2 NOT NULL
            );
            ALTER TABLE [dbo].[SqlOSScimGroupMappings]
                ADD CONSTRAINT [FK_SqlOSScimGroupMappings_Connections_ConnectionId]
                    FOREIGN KEY ([ConnectionId]) REFERENCES [dbo].[SqlOSScimConnections]([Id]);

            CREATE TABLE [dbo].[SqlOSScimManagedGrants] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [ConnectionId] NVARCHAR(64) NOT NULL,
                [MappingId] NVARCHAR(64) NOT NULL,
                [GroupExternalId] NVARCHAR(450) NOT NULL,
                [FgaGroupId] NVARCHAR(128) NOT NULL,
                [FgaGroupSubjectId] NVARCHAR(128) NOT NULL,
                [GrantId] NVARCHAR(128) NOT NULL,
                [RoleId] NVARCHAR(128) NOT NULL,
                [ResourceId] NVARCHAR(256) NOT NULL,
                [CreatedAt] DATETIME2 NOT NULL,
                [RevokedAt] DATETIME2 NULL
            );
            ALTER TABLE [dbo].[SqlOSScimManagedGrants]
                ADD CONSTRAINT [FK_SqlOSScimManagedGrants_Connections_ConnectionId]
                    FOREIGN KEY ([ConnectionId]) REFERENCES [dbo].[SqlOSScimConnections]([Id]);
            ALTER TABLE [dbo].[SqlOSScimManagedGrants]
                ADD CONSTRAINT [FK_SqlOSScimManagedGrants_Mappings_MappingId]
                    FOREIGN KEY ([MappingId]) REFERENCES [dbo].[SqlOSScimGroupMappings]([Id]);

            CREATE TABLE [dbo].[SqlOSFgaGrants] (
                [Id] NVARCHAR(450) NOT NULL PRIMARY KEY,
                [SubjectId] NVARCHAR(450) NOT NULL,
                [ResourceId] NVARCHAR(450) NOT NULL,
                [RoleId] NVARCHAR(450) NOT NULL,
                [EffectiveFrom] DATETIME2 NULL,
                [EffectiveTo] DATETIME2 NULL,
                [CreatedAt] DATETIME2 NOT NULL,
                [UpdatedAt] DATETIME2 NOT NULL
            );

            INSERT INTO [dbo].[SqlOSOrganizations] (
                [Id], [Slug], [Name], [IsActive], [CreatedAt])
            VALUES (
                'org_upgrade', 'upgrade', 'Upgrade organization', 1, '2026-01-01');

            INSERT INTO [dbo].[SqlOSUsers] (
                [Id], [DefaultEmail], [DisplayName], [IsActive], [CreatedAt], [UpdatedAt])
            VALUES
                ('user_alice', ' alice@example.test ', 'Alice Example', 1, '2026-01-01', '2026-01-01'),
                ('user_bob', 'shared@example.test', 'Bob Example', 1, '2026-01-01', '2026-01-01'),
                ('user_carol', 'shared@example.test', 'Carol Example', 1, '2026-01-01', '2026-01-01');

            INSERT INTO [dbo].[SqlOSScimConnections] (
                [Id], [OrganizationId], [DisplayName], [IsEnabled], [Source], [CreatedAt], [UpdatedAt])
            VALUES
                ('conn_old', 'org_upgrade', 'Older connection', 1, 'api', '2026-01-01', '2026-01-02'),
                ('conn_new', 'org_upgrade', 'Newer connection', 1, 'api', '2026-01-03', '2026-01-04');

            INSERT INTO [dbo].[SqlOSScimGroupMappings] (
                [Id], [ConnectionId], [Source], [MatchType], [RoleKey], [IsEnabled], [CreatedAt], [UpdatedAt])
            VALUES
                ('mapping_old', 'conn_old', 'api', 'externalId', 'member', 1, '2026-01-01', '2026-01-01'),
                ('mapping_new', 'conn_new', 'api', 'externalId', 'member', 1, '2026-01-01', '2026-01-01');

            INSERT INTO [dbo].[SqlOSFgaGrants] (
                [Id], [SubjectId], [ResourceId], [RoleId], [CreatedAt], [UpdatedAt])
            VALUES
                ('grant_disabled', 'group_subject_old', 'root', 'role_member', '2026-01-01', '2026-01-01'),
                ('grant_enabled', 'group_subject_new', 'root', 'role_member', '2026-01-01', '2026-01-01');

            INSERT INTO [dbo].[SqlOSScimManagedGrants] (
                [Id], [ConnectionId], [MappingId], [GroupExternalId], [FgaGroupId],
                [FgaGroupSubjectId], [GrantId], [RoleId], [ResourceId], [CreatedAt])
            VALUES
                ('managed_disabled', 'conn_old', 'mapping_old', 'group-old', 'group_old', 'group_subject_old', 'grant_disabled', 'role_member', 'root', '2026-01-01'),
                ('managed_enabled', 'conn_new', 'mapping_new', 'group-new', 'group_new', 'group_subject_new', 'grant_enabled', 'role_member', 'root', '2026-01-01');

            INSERT INTO [dbo].[SqlOSScimExternalIds] (
                [Id], [ConnectionId], [ResourceType], [ExternalId], [EntityId],
                [FgaSubjectId], [DisplayName], [IsActive], [CreatedAt], [UpdatedAt], [LastSyncedAt])
            VALUES
                ('link_alice_old', 'conn_new', 'User', 'alice-old', 'user_alice', NULL, NULL, 1, '2026-01-01', '2026-01-01', '2026-01-01'),
                ('link_alice_new', 'conn_new', 'User', 'alice-new', 'user_alice', NULL, NULL, 1, '2026-01-02', '2026-01-02', '2026-01-02'),
                ('link_bob', 'conn_new', 'User', 'bob-external', 'user_bob', NULL, NULL, 1, '2026-01-03', '2026-01-03', '2026-01-03'),
                ('link_carol', 'conn_new', 'User', 'carol-external', 'user_carol', NULL, NULL, 1, '2026-01-04', '2026-01-04', '2026-01-04');
            """);
    }

    private static async Task SeedVersion26OAuthStateSchemaAsync(DbContext context)
    {
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE [dbo].[SqlOSSchema] ([Version] INT NOT NULL);
            INSERT INTO [dbo].[SqlOSSchema] ([Version]) VALUES (26);

            CREATE TABLE [dbo].[SqlOSAuthorizationRequests] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [State] NVARCHAR(256) NOT NULL
            );

            CREATE TABLE [dbo].[SqlOSAuthorizationCodes] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [State] NVARCHAR(256) NOT NULL
            );
            """);

        var requestId = "req_state_upgrade";
        var state = new string('s', 256);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [dbo].[SqlOSAuthorizationRequests] ([Id], [State]) VALUES ({requestId}, {state})");
    }

    private static async Task SeedVersion26WideOAuthStateSchemaAsync(DbContext context)
    {
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE [dbo].[SqlOSSchema] ([Version] INT NOT NULL);
            INSERT INTO [dbo].[SqlOSSchema] ([Version]) VALUES (26);

            CREATE TABLE [dbo].[SqlOSAuthorizationRequests] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [State] NVARCHAR(4000) NOT NULL
            );

            CREATE TABLE [dbo].[SqlOSAuthorizationCodes] (
                [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
                [State] NVARCHAR(MAX) NOT NULL
            );
            """);

        var requestId = "req_state_wide";
        var state = new string('s', 3000);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [dbo].[SqlOSAuthorizationRequests] ([Id], [State]) VALUES ({requestId}, {state})");
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

    private static async Task<List<string>> ReadAppliedMigrationOrderAsync(DbContext context)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT [ScriptName] FROM [dbo].[SqlOSAppliedMigrations] ORDER BY [Sequence]";
            await using var reader = await cmd.ExecuteReaderAsync();
            var result = new List<string>();
            while (await reader.ReadAsync())
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static int ParseMigrationVersion(string scriptName)
    {
        var match = Regex.Match(scriptName, @"\.Schema\.(\d+)_", RegexOptions.CultureInvariant);
        Assert.IsTrue(match.Success, $"Unexpected migration resource name: {scriptName}");
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
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
