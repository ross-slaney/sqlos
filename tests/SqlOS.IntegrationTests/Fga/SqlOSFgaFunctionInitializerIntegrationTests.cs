using FluentAssertions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.IntegrationTests.Fga.Infrastructure;
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
            definition.Should().Contain("WHERE a.Depth < 3");
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
