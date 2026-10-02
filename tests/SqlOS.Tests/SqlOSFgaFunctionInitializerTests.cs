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
    public void LineageColumnsSql_AddsOneColumnPerLevel_AndNoIndexes()
    {
        var sql = SqlServerDatabaseProvider.Instance.BuildEnsureLineageColumnsSql(new SqlOSFgaOptions { MaxResourceHierarchyDepth = 3 }).Single();

        sql.Should().Contain("ALTER TABLE [dbo].[SqlOSFgaResources] ADD [Ancestor0] BIGINT NULL");
        sql.Should().Contain("ADD [Ancestor3] BIGINT NULL");
        sql.Should().NotContain("Ancestor4");
        sql.Should().NotContain("INDEX", "the ancestor columns are read by resource id or by Seq, never searched by value");
    }

    [TestMethod]
    public void ScopeIndexesSql_AddsComputedColumnsAndFilteredIndexesPerLevel_AndDropsStaleOnes()
    {
        var options = new SqlOSFgaOptions { MaxResourceHierarchyDepth = 2 };
        var scope = new SqlOSFgaScopeTable("app", "Items", "ResourceId", ["Id"], [new SqlOSFgaScopeOrder("Price", ["Price", "Id"])]);
        var sql = SqlServerDatabaseProvider.Instance.BuildEnsureScopeIndexesSql(options, [scope]).Single();

        // Guarded: the column comes from the application's migration.
        sql.Should().Contain("IF COL_LENGTH('[app].[Items]', 'SqlOSFgaScope') IS NULL");
        sql.Should().Contain("THROW 51013");

        // Per level 0..2: a computed column over the level's eight bytes, the key index and the Price mirror on
        // it, filtered on the depth byte so a level no row reaches costs nothing.
        for (var level = 0; level <= 2; level++)
        {
            var offset = SqlOSFgaLineage.ScopeAncestorOffset(level);
            sql.Should().Contain($"ALTER TABLE [app].[Items] ADD [SqlOSFgaScope{level}] AS SUBSTRING([SqlOSFgaScope], {offset}, 8);");
            sql.Should().Contain($"CREATE NONCLUSTERED INDEX [IX_Items_SqlOSFgaScope{level}] ON [app].[Items] ([SqlOSFgaScope{level}], [Id]) WHERE [SqlOSFgaScope] >= 0x0{level};");
            sql.Should().Contain($"CREATE NONCLUSTERED INDEX [IX_Items_SqlOSFgaScope{level}_Price] ON [app].[Items] ([SqlOSFgaScope{level}], [Price], [Id]) WHERE [SqlOSFgaScope] >= 0x0{level};");
        }

        sql.Should().NotContain("SqlOSFgaScope3");

        // The type, as a computed column with statistics and no index: the optimizer estimates the type test
        // from data instead of guessing it is selective.
        sql.Should().Contain("ALTER TABLE [app].[Items] ADD [SqlOSFgaScopeType] AS SUBSTRING([SqlOSFgaScope], 2, 4);");
        sql.Should().Contain("CREATE STATISTICS [ST_Items_SqlOSFgaScopeType] ON [app].[Items] ([SqlOSFgaScopeType]);");
        sql.Should().NotContain("INDEX [IX_Items_SqlOSFgaScopeType");
        sql.Should().Contain("name LIKE 'IX[_]Items[_]SqlOSFgaScope%'");
        sql.Should().Contain("'IX_Items_SqlOSFgaScope2_Price')");
        sql.Should().Contain("EXEC (@stale);");
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

        // The rebuild never runs as one transaction: the nodes are computed in a temp table, then every range
        // of the key commits on its own.
        var rebuild = batches.Single(b => b.Contains("sp_SqlOSFgaResources_LineageRebuild]\nAS", StringComparison.Ordinal));
        rebuild.Should().Contain("CREATE TABLE #SqlOSLineageNodes");
        rebuild.Should().Contain("CREATE TABLE #SqlOSLineageRanges");
        rebuild.Should().Contain("/ 500000 AS Range");
        rebuild.Should().Contain("WHERE r.Id >= @from AND r.Id <= @to");
        rebuild.Should().Contain("BEGIN TRANSACTION;");
        rebuild.Should().Contain("COMMIT TRANSACTION;");
        rebuild.IndexOf("BEGIN TRANSACTION;", StringComparison.Ordinal).Should().BeGreaterThan(rebuild.IndexOf("WHILE @n <= @ranges", StringComparison.Ordinal));
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

        // The scope value of the affected rows follows (depth, type, then each level's ancestor where access
        // flows down from it), and is cleared when the resource goes.
        all.Should().Contain("[SqlOSFgaScope] = CASE WHEN r.Id IS NULL THEN NULL ELSE CAST(ISNULL(r.Depth, 0) AS BINARY(1)) + CAST(rt.Seq AS BINARY(4)) + CAST(ISNULL(CASE WHEN r.Reach <= 0 THEN r.Ancestor0 END, 0) AS BINARY(8))");
        all.Should().Contain("CAST(ISNULL(CASE WHEN r.Reach <= 4 THEN r.Ancestor4 END, 0) AS BINARY(8)) END");
        all.Should().Contain("INNER JOIN #SqlOSLineageAffected s ON s.Id = t.[ResourceId]");
        all.Should().Contain("[SqlOSFgaScope] = NULL");
        all.Should().Contain("IF NOT UPDATE([ResourceId]) RETURN;");
        all.Should().Contain("SELECT Id FROM (SELECT Id, ParentId, IsActive FROM inserted EXCEPT SELECT Id, ParentId, IsActive FROM deleted) changed");
        all.Should().NotContain("INNER JOIN deleted d ON d.Id = i.Id", "a join of inserted and deleted has nothing to plan by");
    }

    [TestMethod]
    public void LineageMaintenanceSql_WithoutScopeTables_HasNoEmptyBlocks()
    {
        var all = string.Join("\n", SqlServerDatabaseProvider.Instance.BuildLineageMaintenanceSql(new SqlOSFgaOptions(), []));

        System.Text.RegularExpressions.Regex.IsMatch(all, @"BEGIN\s+END").Should().BeFalse("T-SQL rejects an empty block");
        all.Should().NotContain("IF UPDATE(ResourceTypeId)", "there is nothing to propagate a type change to");
        all.Should().NotContain("SqlOSFgaScope");

        var pg = string.Join("\n", PostgreSqlDatabaseProvider.Instance.BuildLineageMaintenanceSql(new SqlOSFgaOptions(), []));
        System.Text.RegularExpressions.Regex.IsMatch(pg, @"IF v_types THEN\s+NULL;\s+END IF;").Should().BeTrue();
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
