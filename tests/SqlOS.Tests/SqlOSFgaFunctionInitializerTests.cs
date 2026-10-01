using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Tests;

[TestClass]
public class SqlOSFgaFunctionInitializerTests
{
    [TestMethod]
    public void BuildFunctionSql_UsesAtomicDefinitionUpdateAndCycleGuard()
    {
        var sql = SqlOSFgaFunctionInitializer.BuildIsResourceAccessibleFunctionSql(
            new SqlOSFgaOptions { MaxResourceHierarchyDepth = 7 });

        sql.Should().Contain("CREATE OR ALTER FUNCTION");
        sql.Should().NotContain("DROP FUNCTION");
        sql.Should().Contain("CycleDetected");
        sql.Should().Contain("CHARINDEX");
        sql.Should().Contain("malformed.CycleDetected = 1");
        sql.Should().Contain("truncated.Depth = 7");
        sql.Should().Contain("a.Depth < 7");
    }

    [TestMethod]
    public void BuildFunctionSql_EscapesConfiguredIdentifiers()
    {
        var options = new SqlOSFgaOptions { Schema = "tenant]one" };
        options.TableNames.Resources = "resources]current";

        var sql = SqlOSFgaFunctionInitializer.BuildIsResourceAccessibleFunctionSql(options);

        sql.Should().Contain("[tenant]]one].fn_IsResourceAccessible");
        sql.Should().Contain("[tenant]]one].[resources]]current]");
        sql.Should().Contain("[tenant]]one].fn_AccessRoots(@SubjectIds, @PermissionId)");
    }

    [TestMethod]
    public void BuildAccessRootsSql_CarriesEveryGrantCondition()
    {
        var options = new SqlOSFgaOptions { Schema = "tenant]one" };
        options.TableNames.Grants = "grants]current";

        var sql = SqlOSFgaFunctionInitializer.BuildAccessRootsFunctionSql(options);

        sql.Should().Contain("CREATE OR ALTER FUNCTION [tenant]]one].fn_AccessRoots");
        sql.Should().Contain("[tenant]]one].[grants]]current]");
        sql.Should().Contain("OPENJSON(@SubjectIds)");
        sql.Should().Contain("JSON_VALUE(@SubjectIds, '$[0]')");
        sql.Should().Contain("rp.PermissionId = @PermissionId");
        sql.Should().Contain("r.IsActive = 1");
        sql.Should().Contain("g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE()");
        sql.Should().Contain("g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE()");
        sql.Should().Contain("sa.ExpiresAt > GETUTCDATE()");
        sql.Should().Contain("parent.Seq AS ParentSeq, parent.IsActive AS ParentIsActive");
        sql.Should().Contain("LEFT JOIN [tenant]]one].[SqlOSFgaResources] parent ON parent.Id = r.ParentId");
    }

    [TestMethod]
    public void BuildClosureMaintenanceSql_CreatesRoutinesTriggersAndDepthGuards()
    {
        var options = new SqlOSFgaOptions { Schema = "tenant]one", MaxResourceHierarchyDepth = 4 };
        options.TableNames.Resources = "resources]current";

        var batches = SqlOS.Database.SqlServerDatabaseProvider.Instance.BuildResourceClosureMaintenanceSql(options);
        var all = string.Join("\n", batches);

        batches.Should().HaveCount(5);
        all.Should().Contain("CREATE OR ALTER PROCEDURE [tenant]]one].[sp_resources]]currentClosure_Apply]");
        all.Should().Contain("CREATE OR ALTER PROCEDURE [tenant]]one].[sp_resources]]currentClosure_Rebuild]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [tenant]]one].[TR_resources]]currentClosure_Insert]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [tenant]]one].[TR_resources]]currentClosure_Update]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [tenant]]one].[TR_resources]]currentClosure_Delete]");
        all.Should().Contain("[tenant]]one].[resources]]currentClosure]");
        all.Should().Contain("c.Depth <= 4");
        all.Should().Contain("WHERE Depth > 4");
        all.Should().Contain("would create a cycle");
        all.Should().Contain("@RejectMalformed BIT = 1");
        all.Should().Contain("EXEC [tenant]]one].[sp_resources]]currentClosure_Apply] @RejectMalformed = 0;");
        all.Should().NotContain("DROP TRIGGER");
        // The recursive member of a SQL Server CTE may not contain an outer join.
        all.Should().NotContain("LEFT JOIN deleted");
        all.Should().Contain("INTO #SqlOSClosureOld FROM deleted");
        all.Should().Contain("CREATE UNIQUE CLUSTERED INDEX IX_SqlOSClosureOld ON #SqlOSClosureOld (Id)");
        // The walk mirrors fn_IsResourceAccessible: from an active descendant, through active ancestors only.
        all.Should().Contain("WHERE x.ParentId IS NOT NULL AND x.IsActive = 1");
        all.Should().Contain("WHERE p.ParentId IS NOT NULL AND p.IsActive = 1 AND c.Depth <= 4");
    }

    [TestMethod]
    public void RoutinesHash_ChangesWithAnyDefinitionOrOption()
    {
        static string HashFor(SqlOSFgaOptions options)
        {
            var provider = SqlOS.Database.SqlServerDatabaseProvider.Instance;
            var batches = new List<string>
            {
                provider.BuildAccessRootsFunctionSql(options),
                provider.BuildIsResourceAccessibleFunctionSql(options),
            };
            batches.AddRange(provider.BuildResourceClosureMaintenanceSql(options));
            return SqlOSFgaFunctionInitializer.Hash(batches);
        }

        var defaults = HashFor(new SqlOSFgaOptions());
        defaults.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]+$");
        HashFor(new SqlOSFgaOptions()).Should().Be(defaults, "the same definitions hash the same");
        HashFor(new SqlOSFgaOptions { MaxResourceHierarchyDepth = 7 }).Should().NotBe(defaults);
        HashFor(new SqlOSFgaOptions { Schema = "fga" }).Should().NotBe(defaults);
    }

    [TestMethod]
    public void ResourcesTable_DeclaresTheClosureTriggers_ThatBothProvidersCreate()
    {
        // EF Core's SQL Server update pipeline emits OUTPUT without INTO unless the model says the table has
        // triggers, and SQL Server rejects that on a table with triggers. The declared names must be the ones
        // the providers create.
        var options = new SqlOSFgaOptions();
        options.TableNames.Resources = "Nodes";
        var modelBuilder = new ModelBuilder();
        SqlOSFgaModelConfiguration.Configure(modelBuilder, options);

        var declared = modelBuilder.Model.FindEntityType(typeof(SqlOSFgaResource))!
            .GetDeclaredTriggers()
            .Select(t => t.ModelName)
            .ToList();

        declared.Should().BeEquivalentTo(["TR_NodesClosure_Insert", "TR_NodesClosure_Update", "TR_NodesClosure_Delete"]);
        var sqlServer = string.Join("\n", SqlOS.Database.SqlServerDatabaseProvider.Instance.BuildResourceClosureMaintenanceSql(options));
        var postgres = string.Join("\n", SqlOS.Database.PostgreSqlDatabaseProvider.Instance.BuildResourceClosureMaintenanceSql(options));
        foreach (var trigger in declared)
        {
            sqlServer.Should().Contain($"CREATE OR ALTER TRIGGER [dbo].[{trigger}]");
            postgres.Should().Contain($"CREATE OR REPLACE TRIGGER \"{trigger}\"");
        }
    }
}
