using System.Data.Common;
using FluentAssertions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.IntegrationTests.Fga;

[TestClass]
public class SqlOSFgaFunctionInitializerIntegrationTests : FgaIntegrationTestBase
{
    [TestMethod]
    public async Task EnsureFunctionsExist_Idempotent_CanRunMultipleTimes()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlOSFgaFunctionInitializer(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaFunctionInitializer>());

        // Should not throw when run multiple times
        await initializer.EnsureFunctionsExistAsync();
        await initializer.EnsureFunctionsExistAsync();

        var definition = await GetFunctionDefinitionAsync();
        definition.Should().Contain("CycleDetected");
    }

    [TestMethod]
    public async Task EnsureFunctionsExist_AddsConfiguredDepthGuard()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlOSFgaFunctionInitializer(
            Context,
            Options.Create(new SqlOSFgaOptions { MaxResourceHierarchyDepth = 3 }),
            loggerFactory.CreateLogger<SqlOSFgaFunctionInitializer>());

        try
        {
            await initializer.EnsureFunctionsExistAsync();

            var definition = await GetFunctionDefinitionAsync();
            (definition.Contains("Depth < 3", StringComparison.Ordinal)
                || definition.Contains("\"Depth\" < 3", StringComparison.Ordinal)).Should().BeTrue();
            (definition.Contains("Depth = 3", StringComparison.Ordinal)
                || definition.Contains("\"Depth\" = 3", StringComparison.Ordinal)).Should().BeTrue();
        }
        finally
        {
            var defaultInitializer = new SqlOSFgaFunctionInitializer(
                Context,
                Options.Create(new SqlOSFgaOptions()),
                loggerFactory.CreateLogger<SqlOSFgaFunctionInitializer>());
            await defaultInitializer.EnsureFunctionsExistAsync();
        }
    }

    [TestMethod]
    public async Task ConfiguredDepth_AllowsGrantVisibilityAtAcceptedBoundary()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var options = new SqlOSFgaOptions { MaxResourceHierarchyDepth = 2 };
        var initializer = new SqlOSFgaFunctionInitializer(
            Context,
            Options.Create(options),
            loggerFactory.CreateLogger<SqlOSFgaFunctionInitializer>());
        var suffix = Guid.NewGuid().ToString("N");
        var level1 = new SqlOSFgaResource
        {
            Id = $"depth_level_1_{suffix}",
            ParentId = "root",
            Name = "Depth level 1",
            ResourceTypeId = "agency"
        };
        var level2 = new SqlOSFgaResource
        {
            Id = $"depth_level_2_{suffix}",
            ParentId = level1.Id,
            Name = "Depth level 2",
            ResourceTypeId = "project"
        };

        try
        {
            Context.Set<SqlOSFgaResource>().AddRange(level1, level2);
            await Context.SaveChangesAsync();
            await initializer.EnsureFunctionsExistAsync();
            var authService = new SqlOSFgaAuthService(
                Context,
                Options.Create(options),
                loggerFactory.CreateLogger<SqlOSFgaAuthService>());

            var pointCheck = await authService.CheckAccessAsync(
                FgaTestDataSeeder.SystemAdminSubjectId,
                "TEST_VIEW",
                level2.Id);
            var sqlFilterVisible = await Context.IsResourceAccessible(
                    level2.Id,
                    JsonSerializer.Serialize(new[] { FgaTestDataSeeder.SystemAdminSubjectId }),
                    FgaTestDataSeeder.ViewPermissionId)
                .AnyAsync();

            pointCheck.Allowed.Should().BeTrue();
            sqlFilterVisible.Should().BeTrue();
        }
        finally
        {
            Context.Set<SqlOSFgaResource>().RemoveRange(level2, level1);
            await Context.SaveChangesAsync();
            var defaultInitializer = new SqlOSFgaFunctionInitializer(
                Context,
                Options.Create(new SqlOSFgaOptions()),
                loggerFactory.CreateLogger<SqlOSFgaFunctionInitializer>());
            await defaultInitializer.EnsureFunctionsExistAsync();
        }
    }

    [TestMethod]
    public async Task EnsureFunctionsExist_EnforcesPrincipalAndResourceLifecycle()
    {
        // The grant conditions live in fn_AccessRoots; fn_IsResourceAccessible walks the tree and joins them.
        var definition = await GetFunctionDefinitionAsync()
            + Environment.NewLine
            + await TestCatalog.GetFunctionDefinitionAsync(Context, "fn_AccessRoots");

        if (TestDatabase.IsPostgreSql)
        {
            definition.Should().Contain("\"IsActive\" = TRUE");
            definition.Should().Contain("u.\"IsActive\" = TRUE");
            definition.Should().Contain("sa.\"ExpiresAt\" > (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')");
            definition.Should().Contain("ug.\"IsActive\" = TRUE");
            definition.Should().Contain("jsonb_array_elements_text");
            definition.Should().Contain("p_subject_ids::jsonb ->> 0");
            definition.Should().Contain("permission.\"ResourceTypeId\" IS NULL OR permission.\"ResourceTypeId\" = target.\"ResourceTypeId\"");
        }
        else
        {
            definition.Should().Contain("IsActive = 1");
            definition.Should().Contain("u.IsActive = 1");
            definition.Should().Contain("sa.ExpiresAt > GETUTCDATE()");
            definition.Should().Contain("ug.IsActive = 1");
            definition.Should().Contain("OPENJSON(@SubjectIds)");
            definition.Should().Contain("JSON_VALUE(@SubjectIds, '$[0]')");
            definition.Should().Contain("permission.ResourceTypeId IS NULL OR permission.ResourceTypeId = target.ResourceTypeId");
        }
    }

    [TestMethod]
    public async Task CyclicHierarchy_IsRejectedOnWrite_AndStillFailsClosedInTheFunction()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var initializer = new SqlOSFgaFunctionInitializer(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaFunctionInitializer>());
        var suffix = Guid.NewGuid().ToString("N");
        var first = new SqlOSFgaResource
        {
            Id = $"cycle_a_{suffix}",
            Name = "Cycle A",
            ResourceTypeId = "agency"
        };
        var second = new SqlOSFgaResource
        {
            Id = $"cycle_b_{suffix}",
            ParentId = first.Id,
            Name = "Cycle B",
            ResourceTypeId = "agency"
        };
        var grant = new SqlOSFgaGrant
        {
            Id = $"cycle_grant_{suffix}",
            SubjectId = FgaTestDataSeeder.SystemAdminSubjectId,
            ResourceId = first.Id,
            RoleId = FgaTestDataSeeder.SystemAdminRoleId
        };
        var closeTheCycle = TestDatabase.Rewrite("UPDATE [dbo].[SqlOSFgaResources] SET [ParentId] = {0} WHERE [Id] = {1}");
        var updateTrigger = TestDatabase.IsPostgreSql
            ? "\"dbo\".\"SqlOSFgaResources\" {0} TRIGGER \"TR_SqlOSFgaResourcesClosure_Update\""
            : "[dbo].[SqlOSFgaResources] {0} TRIGGER [TR_SqlOSFgaResourcesClosure_Update]";
        var triggerDisabled = false;

        try
        {
            Context.Set<SqlOSFgaResource>().AddRange(first, second);
            Context.Set<SqlOSFgaGrant>().Add(grant);
            await Context.SaveChangesAsync();
            await initializer.EnsureFunctionsExistAsync();

            // The closure trigger refuses the edge that would close the cycle.
            var closing = () => Context.Database.ExecuteSqlRawAsync(closeTheCycle, second.Id, first.Id);
            (await closing.Should().ThrowAsync<DbException>()).Which.Message.Should().Contain("cycle");

            // A cycle that reaches the data anyway (here: with the trigger disabled) still fails closed.
            await Context.Database.ExecuteSqlRawAsync("ALTER TABLE " + string.Format(updateTrigger, "DISABLE"));
            triggerDisabled = true;
            await Context.Database.ExecuteSqlRawAsync(closeTheCycle, second.Id, first.Id);
            Context.ChangeTracker.Clear();

            var visible = await Context.IsResourceAccessible(
                    first.Id,
                    JsonSerializer.Serialize(new[] { FgaTestDataSeeder.SystemAdminSubjectId }),
                    FgaTestDataSeeder.ViewPermissionId)
                .AnyAsync();

            visible.Should().BeFalse();
        }
        finally
        {
            await Context.Database.ExecuteSqlRawAsync(
                TestDatabase.Rewrite("UPDATE [dbo].[SqlOSFgaResources] SET [ParentId] = NULL WHERE [Id] = {0}"),
                first.Id);
            if (triggerDisabled)
            {
                await Context.Database.ExecuteSqlRawAsync("ALTER TABLE " + string.Format(updateTrigger, "ENABLE"));
            }

            Context.ChangeTracker.Clear();
            Context.Set<SqlOSFgaGrant>().Remove(grant);
            Context.Set<SqlOSFgaResource>().RemoveRange(second, first);
            await Context.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task CreateOrAlter_KeepsFunctionCallableDuringRepeatedInitialization()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var connectionString = Context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The integration database has no connection string.");
        await using var firstUpdater = CreateContext(connectionString);
        await using var secondUpdater = CreateContext(connectionString);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunInitializerAsync(TestSqlOSDbContext updater)
        {
            var initializer = new SqlOSFgaFunctionInitializer(
                updater,
                Options.Create(new SqlOSFgaOptions()),
                loggerFactory.CreateLogger<SqlOSFgaFunctionInitializer>());

            await startGate.Task;
            for (var i = 0; i < 5; i++)
            {
                // Forget the stored definition hash, so every iteration re-creates the routines for real
                // while the readers run; an unchanged definition is otherwise skipped.
                await updater.Database.ExecuteSqlRawAsync(
                    TestDatabase.Rewrite("UPDATE [dbo].[SqlOSFgaSchema] SET [RoutinesHash] = NULL"));
                await initializer.EnsureFunctionsExistAsync();
            }
        }

        var updates = new[]
        {
            RunInitializerAsync(firstUpdater),
            RunInitializerAsync(secondUpdater)
        };
        var reads = Enumerable.Range(0, 10).Select(async _ =>
        {
            await startGate.Task;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await using var connection = TestDatabase.CreateConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = TestDatabase.IsPostgreSql
                    ? """
                      SELECT COUNT(*)
                      FROM "dbo"."fn_IsResourceAccessible"(
                          @resourceId,
                          @subjectIds,
                          @permissionId)
                      """
                    : """
                      SELECT COUNT(*)
                      FROM [dbo].fn_IsResourceAccessible(
                          @resourceId,
                          @subjectIds,
                          @permissionId)
                      """;
                TestDatabase.AddParameter(command, "@resourceId", FgaTestDataSeeder.TestAgencyResourceId);
                TestDatabase.AddParameter(
                    command,
                    "@subjectIds",
                    JsonSerializer.Serialize(new[] { FgaTestDataSeeder.SystemAdminSubjectId }));
                TestDatabase.AddParameter(command, "@permissionId", FgaTestDataSeeder.ViewPermissionId);
                Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(1);
            }
        }).ToArray();

        startGate.SetResult();
        await Task.WhenAll(updates.Concat(reads));
    }

    private static TestSqlOSDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseTestProvider(connectionString)
            .Options;
        return new TestSqlOSDbContext(options);
    }

    private static Task<string> GetFunctionDefinitionAsync()
        => TestCatalog.GetFunctionDefinitionAsync(Context, "fn_IsResourceAccessible");
}
