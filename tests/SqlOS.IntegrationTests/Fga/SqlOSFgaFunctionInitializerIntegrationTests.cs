using FluentAssertions;
using System.Text.Json;
using Microsoft.Data.SqlClient;
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
            definition.Should().Contain("a.Depth < 3");
            definition.Should().Contain("truncated.Depth = 3");
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
        var definition = await GetFunctionDefinitionAsync();

        definition.Should().Contain("IsActive = 1");
        definition.Should().Contain("u.IsActive = 1");
        definition.Should().Contain("sa.ExpiresAt > GETUTCDATE()");
        definition.Should().Contain("ug.IsActive = 1");
        definition.Should().Contain("OPENJSON(@SubjectIds)");
        definition.Should().Contain("JSON_VALUE(@SubjectIds, '$[0]')");
        definition.Should().Contain("permission.ResourceTypeId IS NULL OR permission.ResourceTypeId = target.ResourceTypeId");
    }

    [TestMethod]
    public async Task CyclicHierarchy_FailsClosedWithoutSqlRecursionFailure()
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

        try
        {
            Context.Set<SqlOSFgaResource>().AddRange(first, second);
            Context.Set<SqlOSFgaGrant>().Add(grant);
            await Context.SaveChangesAsync();
            await Context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [dbo].[SqlOSFgaResources] SET ParentId = {second.Id} WHERE Id = {first.Id}");
            Context.ChangeTracker.Clear();
            await initializer.EnsureFunctionsExistAsync();

            var visible = await Context.IsResourceAccessible(
                    first.Id,
                    JsonSerializer.Serialize(new[] { FgaTestDataSeeder.SystemAdminSubjectId }),
                    FgaTestDataSeeder.ViewPermissionId)
                .AnyAsync();

            visible.Should().BeFalse();
        }
        finally
        {
            await Context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [dbo].[SqlOSFgaResources] SET ParentId = NULL WHERE Id = {first.Id}");
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
        var updater = CreateContext(connectionString);
        await using (updater)
        {
            var initializer = new SqlOSFgaFunctionInitializer(
                updater,
                Options.Create(new SqlOSFgaOptions()),
                loggerFactory.CreateLogger<SqlOSFgaFunctionInitializer>());

            var updates = Task.Run(async () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await initializer.EnsureFunctionsExistAsync();
                }
            });

            var reads = Enumerable.Range(0, 20).Select(async _ =>
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*)
                    FROM [dbo].fn_IsResourceAccessible(
                        @resourceId,
                        @subjectIds,
                        @permissionId)
                    """;
                command.Parameters.AddWithValue("@resourceId", FgaTestDataSeeder.TestAgencyResourceId);
                command.Parameters.AddWithValue(
                    "@subjectIds",
                    JsonSerializer.Serialize(new[] { FgaTestDataSeeder.SystemAdminSubjectId }));
                command.Parameters.AddWithValue("@permissionId", FgaTestDataSeeder.ViewPermissionId);
                return Convert.ToInt32(await command.ExecuteScalarAsync());
            });

            var results = await Task.WhenAll(reads.Append(updates.ContinueWith(_ => 1)));
            await updates;
            results.Should().OnlyContain(count => count == 1);
        }
    }

    private static TestSqlOSDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new TestSqlOSDbContext(options);
    }

    private static async Task<string> GetFunctionDefinitionAsync()
    {
        var connection = Context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID('[dbo].[fn_IsResourceAccessible]'))";
            return (await cmd.ExecuteScalarAsync())?.ToString() ?? string.Empty;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
