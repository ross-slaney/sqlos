using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Tests;

[TestClass]
public class SqlOSFgaFunctionInitializerTests
{
    [TestMethod]
    public void PointCheckSql_ProbesTheGrantsOfEveryAncestorTheReachCovers()
    {
        var sql = SqlOSFgaFunctionInitializer.BuildIsResourceAccessibleFunctionSql(
            new SqlOSFgaOptions { MaxResourceHierarchyDepth = 7 });

        sql.Should().Contain("CREATE OR ALTER FUNCTION");
        sql.Should().NotContain("DROP FUNCTION");
        sql.Should().NotContain("WITH ancestors");

        // Levels 0..7: the configured depth plus the root level.
        sql.Should().Contain("CROSS APPLY (VALUES (0, x.Ancestor0), (1, x.Ancestor1)");
        sql.Should().Contain("(7, x.Ancestor7)) AS lv([Level], [Seq])");
        sql.Should().NotContain("x.Ancestor8");
        sql.Should().Contain("lv.[Level] >= x.Reach");
        sql.Should().Contain("x.Reach IS NOT NULL");

        // The grant is matched by the ancestor, never by the caller's whole grant list.
        sql.Should().Contain("INNER JOIN [dbo].[SqlOSFgaGrants] g ON g.ResourceId = a.Id");
        sql.Should().Contain("g.SubjectId IN (SELECT live.SubjectId FROM [dbo].fn_ActiveSubjects(@SubjectIds) live)");
        sql.Should().Contain("rp.PermissionId = @PermissionId");
        sql.Should().Contain("permission.ResourceTypeId IS NULL OR permission.ResourceTypeId = x.ResourceTypeId");
        sql.Should().Contain("g.EffectiveFrom <= GETUTCDATE()");
        sql.Should().Contain("g.EffectiveTo >= GETUTCDATE()");
    }

    [TestMethod]
    public void PointCheckSql_EscapesConfiguredIdentifiers()
    {
        var options = new SqlOSFgaOptions { Schema = "tenant]one" };
        options.TableNames.Resources = "resources]current";

        var sql = SqlOSFgaFunctionInitializer.BuildIsResourceAccessibleFunctionSql(options);

        sql.Should().Contain("[tenant]]one].fn_IsResourceAccessible");
        sql.Should().Contain("[tenant]]one].[resources]]current]");
    }

    [TestMethod]
    public void ActiveSubjectsSql_CarriesEverySubjectCondition()
    {
        var sql = SqlOSFgaFunctionInitializer.BuildActiveSubjectsFunctionSql(new SqlOSFgaOptions());

        sql.Should().Contain("CREATE OR ALTER FUNCTION [dbo].fn_ActiveSubjects");
        sql.Should().Contain("OPENJSON(@SubjectIds)");
        sql.Should().Contain("u.IsActive = 1");
        sql.Should().Contain("sa.ExpiresAt > GETUTCDATE()");
        sql.Should().Contain("ug.IsActive = 1");
        sql.Should().Contain("ag.SubjectId IS NOT NULL");
        sql.Should().Contain("caller.Id = JSON_VALUE(@SubjectIds, '$[0]')");
    }

    [TestMethod]
    public void AccessRootsSql_ReturnsTheGrantedResourcesWithTheirLevels()
    {
        var sql = SqlOSFgaFunctionInitializer.BuildAccessRootsFunctionSql(new SqlOSFgaOptions());

        sql.Should().Contain("CREATE OR ALTER FUNCTION [dbo].fn_AccessRoots");
        sql.Should().Contain("SELECT DISTINCT r.Seq AS ResourceSeq, r.Depth");
        sql.Should().Contain("rp.PermissionId = @PermissionId");
        sql.Should().Contain("g.SubjectId IN (SELECT live.SubjectId FROM [dbo].fn_ActiveSubjects(@SubjectIds) live)");
        sql.Should().Contain("r.IsActive = 1");
        sql.Should().Contain("r.Depth IS NOT NULL");
        sql.Should().Contain("g.EffectiveFrom <= GETUTCDATE()");
        sql.Should().Contain("g.EffectiveTo >= GETUTCDATE()");

        SqlServerDatabaseProvider.Instance.BuildAccessRootsQuerySql(new SqlOSFgaOptions())
            .Should().Be("SELECT a.ResourceSeq, a.Depth FROM [dbo].fn_AccessRoots({0}, {1}) AS a");
    }

    [TestMethod]
    public void LineageColumnsSql_AddsOneFilteredIndexPerLevel()
    {
        var batches = SqlServerDatabaseProvider.Instance.BuildEnsureLineageColumnsSql(new SqlOSFgaOptions { MaxResourceHierarchyDepth = 3 });

        batches.Should().HaveCount(2, "columns first, then the indexes that reference them");
        batches[0].Should().Contain("ALTER TABLE [dbo].[SqlOSFgaResources] ADD [Ancestor0] BIGINT NULL");
        batches[0].Should().Contain("ADD [Ancestor3] BIGINT NULL");
        batches[0].Should().NotContain("Ancestor4");
        batches[1].Should().Contain("CREATE NONCLUSTERED INDEX [IX_SqlOSFgaResources_Ancestor2] ON [dbo].[SqlOSFgaResources]([Ancestor2]) INCLUDE ([Reach]) WHERE [Ancestor2] IS NOT NULL");
    }

    [TestMethod]
    public void LineageMaintenanceSql_CreatesTheRoutinesTriggersAndGuards()
    {
        var options = new SqlOSFgaOptions { MaxResourceHierarchyDepth = 4 };
        var scope = new SqlOSFgaScopeTable("app", "Items", "ResourceId", ["Id"]);
        var batches = SqlServerDatabaseProvider.Instance.BuildLineageMaintenanceSql(options, [scope]);
        var all = string.Join("\n", batches);

        all.Should().Contain("CREATE OR ALTER PROCEDURE [dbo].[sp_SqlOSFgaResources_LineageRefresh]");
        all.Should().Contain("CREATE OR ALTER PROCEDURE [dbo].[sp_SqlOSFgaResources_LineageRebuild]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [dbo].[TR_SqlOSFgaResources_Lineage_Insert] ON [dbo].[SqlOSFgaResources]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [dbo].[TR_SqlOSFgaResources_Lineage_Update] ON [dbo].[SqlOSFgaResources]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [dbo].[TR_SqlOSFgaResources_Lineage_Delete] ON [dbo].[SqlOSFgaResources]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [app].[TR_Items_SqlOSFgaScope_Insert] ON [app].[Items]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [app].[TR_Items_SqlOSFgaScope_Update] ON [app].[Items]");

        // The depth guards: a chain climbing past the limit, or a child of a row at the deepest level.
        all.Should().Contain("WHERE Steps > 4");
        all.Should().Contain("p.Depth = 4)");
        all.Should().Contain("maximum hierarchy depth of 4");
        all.Should().Contain("THROW 51012");

        // The lineage: depth, reach, and the ancestor at every level 0..4.
        all.Should().Contain("Reach = CASE WHEN r.IsActive = 0 OR nd.Depth IS NULL THEN NULL WHEN r.ParentId IS NULL THEN 0 WHEN p.Reach IS NULL THEN nd.Depth ELSE p.Reach END");
        all.Should().Contain("Ancestor4 = CASE WHEN nd.Depth = 4 THEN r.Seq WHEN nd.Depth > 4 THEN p.Ancestor4 ELSE NULL END");
        all.Should().NotContain("Ancestor5");

        // The scope columns of the affected rows follow, and are cleared when the resource goes.
        all.Should().Contain("[SqlOSFgaAncestor4] = r.Ancestor4, [SqlOSFgaReach] = r.Reach, [SqlOSFgaTypeSeq] = rt.Seq");
        all.Should().Contain("INNER JOIN #SqlOSLineageAffected s ON s.Id = t.[ResourceId]");
        all.Should().Contain("[SqlOSFgaReach] = NULL, [SqlOSFgaTypeSeq] = NULL");
        all.Should().Contain("IF NOT UPDATE([ResourceId]) RETURN;");
    }

    [TestMethod]
    public void RoutinesHash_ChangesWithAnyDefinitionOrOption()
    {
        var provider = SqlServerDatabaseProvider.Instance;
        string HashFor(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scope)
            => SqlOSFgaFunctionInitializer.Hash(
                provider.BuildEnsureLineageColumnsSql(options)
                    .Concat([provider.BuildActiveSubjectsFunctionSql(options), provider.BuildAccessRootsFunctionSql(options), provider.BuildIsResourceAccessibleFunctionSql(options)])
                    .Concat(provider.BuildLineageMaintenanceSql(options, scope)));

        var baseline = HashFor(new SqlOSFgaOptions(), []);
        HashFor(new SqlOSFgaOptions(), []).Should().Be(baseline, "the same definitions hash the same");
        HashFor(new SqlOSFgaOptions { MaxResourceHierarchyDepth = 11 }, []).Should().NotBe(baseline, "the depth changes the columns and the guards");
        HashFor(new SqlOSFgaOptions { Schema = "other" }, []).Should().NotBe(baseline);
        HashFor(new SqlOSFgaOptions(), [new SqlOSFgaScopeTable(null, "Items", "ResourceId", ["Id"])]).Should().NotBe(baseline, "a newly registered application table adds triggers");
        baseline.Should().HaveLength(64);
    }

    [TestMethod]
    public void RoutinesHashQuery_RequiresEveryRoutineTriggerAndColumn()
    {
        var options = new SqlOSFgaOptions { Schema = "ten'ant", MaxResourceHierarchyDepth = 3 };
        options.TableNames.Resources = "res]ources";
        var scope = new SqlOSFgaScopeTable("app", "Items", "ResourceId", ["Id"]);

        var hash = SqlServerDatabaseProvider.Instance.BuildSelectRoutinesHashSql(options, [scope]);

        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_ActiveSubjects]', N'IF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_AccessRoots]', N'IF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_IsResourceAccessible]', N'IF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[sp_res]]ources_LineageRefresh]', N'P') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[sp_res]]ources_LineageRebuild]', N'P') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[TR_res]]ources_Lineage_Insert]', N'TR') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[TR_res]]ources_Lineage_Delete]', N'TR') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[app].[TR_Items_SqlOSFgaScope_Update]', N'TR') IS NOT NULL");
        hash.Should().Contain("COL_LENGTH('[ten''ant].[res]]ources]', 'Ancestor3') IS NOT NULL");
    }

    [TestMethod]
    public void ResourcesTable_DeclaresTheLineageColumnsAndTriggers()
    {
        var options = new DbContextOptionsBuilder<LineageModelDbContext>()
            .UseSqlServer("Server=.;Database=SqlOS_Lineage;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var context = new LineageModelDbContext(options);
        var resources = context.Model.FindEntityType(typeof(SqlOSFgaResource))!;

        resources.GetDeclaredTriggers().Select(t => t.GetDatabaseName())
            .Should().BeEquivalentTo(SqlOSFgaLineage.TriggerNames("SqlOSFgaResources"));

        foreach (var name in new[] { "Seq", "Depth", "Reach", "Ancestor0", "Ancestor3" })
        {
            var property = resources.FindProperty(name);
            property.Should().NotBeNull(name);
            property!.IsShadowProperty().Should().BeTrue(name);
            property.GetBeforeSaveBehavior().Should().Be(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore, "{0} is maintained by the database", name);
            property.GetAfterSaveBehavior().Should().Be(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore, name);
        }

        resources.FindProperty("Ancestor4").Should().BeNull("the model declares depth 3, so levels 0..3");
        context.Model.FindEntityType(typeof(SqlOSFgaResourceType))!.FindProperty("Seq").Should().NotBeNull();
        context.Model.FindEntityType(typeof(SqlOSFgaAccessRoot))!.FindPrimaryKey().Should().BeNull("the roots are a query result");
    }
}

file sealed class LineageModelDbContext(DbContextOptions<LineageModelDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.UseSqlOS(GetType(), SqlOSDatabase.SqlServerProviderName, new SqlOSFgaOptions { MaxResourceHierarchyDepth = 3 });
}
