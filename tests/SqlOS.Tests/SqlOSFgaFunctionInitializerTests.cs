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
        sql.Should().Contain("SELECT r.Seq AS ResourceSeq, r.Depth");
        sql.Should().NotContain("DISTINCT", "a count up to a cap reads only the first roots it needs");
        sql.Should().Contain("rp.PermissionId = @PermissionId");
        sql.Should().Contain("g.SubjectId IN (SELECT live.SubjectId FROM [dbo].fn_ActiveSubjects(@SubjectIds) live)");
        sql.Should().Contain("CROSS APPLY (\n        SELECT TOP (1) x.Seq, x.Depth\n        FROM [dbo].[SqlOSFgaResources] x\n        WHERE x.Id = g.ResourceId AND x.IsActive = 1 AND x.Depth IS NOT NULL\n    ) r",
            "each grant's resource is a lookup by key, never a pass over the resources");
        sql.Should().Contain("g.EffectiveFrom <= GETUTCDATE()");
        sql.Should().Contain("g.EffectiveTo >= GETUTCDATE()");
    }

    [TestMethod]
    public void ListVisibleSql_ListsEachRootsRowsFromTheIndexOfItsLevel()
    {
        var sql = SqlOSFgaFunctionInitializer.BuildListVisibleFunctionSql(new SqlOSFgaOptions { MaxResourceHierarchyDepth = 3 });

        sql.Should().Contain("CREATE OR ALTER FUNCTION [dbo].fn_ListVisible(");
        sql.Should().Contain("@TypeId NVARCHAR(450)");
        sql.Should().Contain("SELECT v.Id AS ResourceId");
        sql.Should().Contain("FROM [dbo].fn_AccessRoots(@SubjectIds, @PermissionId) a\n    CROSS APPLY (");

        // One branch per level 0..3, run only for the roots at that level (a startup filter on a.Depth), reading the
        // rows under the root from the level's filtered index: the predicate states the index's filter.
        for (var level = 0; level <= 3; level++)
        {
            sql.Should().Contain($"SELECT r.Id FROM [dbo].[SqlOSFgaResources] r WHERE a.Depth = {level} AND r.Ancestor{level} = a.ResourceSeq AND r.Ancestor{level} IS NOT NULL AND r.Reach <= {level} AND (@TypeId IS NULL OR r.ResourceTypeId = @TypeId)");
        }

        sql.Should().NotContain("Ancestor4");
        sql.Should().NotContain("SqlOSFgaGrants", "the grants are the roots' business");
    }

    [TestMethod]
    public void VisibleSet_IsTheListMaterializedOnce_AndListFirst_CountsUpToTheTablesCap()
    {
        var provider = SqlServerDatabaseProvider.Instance;
        var options = new SqlOSFgaOptions();

        // A multi-statement function: the statement that reads it starts from its rows.
        var set = provider.BuildVisibleSetFunctionSql(options);
        set.Should().Contain("CREATE OR ALTER FUNCTION [dbo].fn_VisibleSet(");
        set.Should().Contain("RETURNS @visible TABLE (ResourceId NVARCHAR(450) NOT NULL PRIMARY KEY WITH (IGNORE_DUP_KEY = ON))");
        set.Should().Contain("SELECT l.ResourceId FROM [dbo].fn_ListVisible(@SubjectIds, @PermissionId, @TypeId) l;");

        // The cap is 8·√(the table's rows), at least 1,000; the count reads fn_ListVisible up to it and stops.
        var listFirst = SqlOSFgaFunctionInitializer.BuildListFirstFunctionSql(options);
        listFirst.Should().Contain("CREATE OR ALTER FUNCTION [dbo].fn_ListFirst(");
        listFirst.Should().Contain("@Table NVARCHAR(776)");
        listFirst.Should().Contain("CASE WHEN 8 * SQRT(CAST(ISNULL(SUM(p.rows), 0) AS FLOAT)) > 1000 THEN 8 * SQRT(CAST(ISNULL(SUM(p.rows), 0) AS FLOAT)) ELSE 1000 END");
        listFirst.Should().Contain("WHERE p.object_id = OBJECT_ID(@Table) AND p.index_id IN (0, 1)");
        listFirst.Should().Contain("SELECT TOP (c.Cap) 1 AS One FROM [dbo].fn_ListVisible(@SubjectIds, @PermissionId, @TypeId)");
        listFirst.Should().Contain("CAST(CASE WHEN v.Visible < c.Cap THEN 1 ELSE 0 END AS BIT) AS ListFirst");

        provider.BuildListFirstQuerySql(options).Should().Be("SELECT f.ListFirst AS [Value] FROM [dbo].fn_ListFirst({0}, {1}, {2}, {3}) AS f");

        // The row check is the point check under SqlOS's own name, so an application's own mapping of
        // fn_IsResourceAccessible never meets SqlOS's in one EF model.
        var checkRow = provider.BuildCheckRowFunctionSql(options);
        checkRow.Should().Contain("CREATE OR ALTER FUNCTION [dbo].fn_CheckRow(");
        checkRow.Should().Contain("SELECT CAST(1 AS BIT) AS Allowed\n    FROM [dbo].fn_IsResourceAccessible(@ResourceId, @SubjectIds, @PermissionId)");
    }

    [TestMethod]
    public void LineageColumnsSql_AddsOneColumnAndOneIndexPerLevel()
    {
        var batches = SqlServerDatabaseProvider.Instance.BuildEnsureLineageColumnsSql(new SqlOSFgaOptions { MaxResourceHierarchyDepth = 3 });

        batches.Should().HaveCount(2, "the columns, then the indexes over them");
        batches[0].Should().Contain("ALTER TABLE [dbo].[SqlOSFgaResources] ADD [Ancestor0] BIGINT NULL");
        batches[0].Should().Contain("ADD [Ancestor3] BIGINT NULL");
        batches[0].Should().NotContain("Ancestor4");
        batches[0].Should().NotContain("INDEX");

        // Per level: the ancestor and the type, covering what fn_ListVisible reads of a row beneath a root, over the
        // rows that have an ancestor at the level.
        for (var level = 0; level <= 3; level++)
        {
            batches[1].Should().Contain($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSFgaResources_Ancestor{level}' AND object_id = OBJECT_ID(N'[dbo].[SqlOSFgaResources]'))");
            batches[1].Should().Contain($"CREATE NONCLUSTERED INDEX [IX_SqlOSFgaResources_Ancestor{level}] ON [dbo].[SqlOSFgaResources] ([Ancestor{level}], [ResourceTypeId]) INCLUDE ([Reach], [Id]) WHERE [Ancestor{level}] IS NOT NULL;");
        }

        batches[1].Should().NotContain("Ancestor4");
    }

    [TestMethod]
    public void LineageMaintenanceSql_CreatesTheRoutinesTriggersAndGuards()
    {
        var options = new SqlOSFgaOptions { MaxResourceHierarchyDepth = 4 };
        var batches = SqlServerDatabaseProvider.Instance.BuildLineageMaintenanceSql(options);
        var all = string.Join("\n", batches);

        all.Should().Contain("CREATE OR ALTER PROCEDURE [dbo].[sp_SqlOSFgaResources_LineageRefresh]");
        all.Should().Contain("CREATE OR ALTER PROCEDURE [dbo].[sp_SqlOSFgaResources_LineageRebuild]");

        // The rebuild never runs as one transaction: the nodes are computed in a temp table, then every range
        // of the key commits on its own.
        var rebuild = batches.Single(b => b.Contains("CREATE OR ALTER PROCEDURE [dbo].[sp_SqlOSFgaResources_LineageRebuild]", StringComparison.Ordinal));
        rebuild.Should().Contain("CREATE TABLE #SqlOSLineageNodes");
        rebuild.Should().Contain("CREATE TABLE #SqlOSLineageRanges");
        rebuild.Should().Contain("@RangeRows INT = 500000");
        rebuild.Should().Contain("/ @RangeRows AS Range");
        rebuild.Should().Contain("WHERE r.Id >= @from AND r.Id <= @to");
        rebuild.Should().Contain("BEGIN TRANSACTION;");
        rebuild.Should().Contain("COMMIT TRANSACTION;");
        rebuild.IndexOf("BEGIN TRANSACTION;", StringComparison.Ordinal).Should().BeGreaterThan(rebuild.IndexOf("WHILE @n <= @ranges", StringComparison.Ordinal));
        rebuild.Should().Contain("SET [LineageBuilt] = 0");
        rebuild.Should().Contain("SET [LineageBuilt] = 1");

        // Two triggers, on the resources table, and none anywhere else: a deleted resource is a leaf (a parent
        // cannot be deleted) and in no other row's lineage.
        all.Should().Contain("CREATE OR ALTER TRIGGER [dbo].[TR_SqlOSFgaResources_Lineage_Insert] ON [dbo].[SqlOSFgaResources]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [dbo].[TR_SqlOSFgaResources_Lineage_Update] ON [dbo].[SqlOSFgaResources]");
        all.Should().NotContain("AFTER DELETE");
        System.Text.RegularExpressions.Regex.Matches(all, "CREATE OR ALTER TRIGGER").Count.Should().Be(2);

        // The depth guards: a chain climbing past the limit, or a child of a row at the deepest level.
        all.Should().Contain("WHERE Steps > 4");
        all.Should().Contain("p.Depth = 4)");
        all.Should().Contain("maximum hierarchy depth of 4");
        all.Should().Contain("THROW 51012");

        // The lineage: depth, reach, and the ancestor at every level 0..4; nothing else is maintained.
        all.Should().Contain("Reach = CASE WHEN r.IsActive = 0 OR nd.Depth IS NULL THEN NULL WHEN r.ParentId IS NULL THEN 0 WHEN p.Reach IS NULL THEN nd.Depth ELSE p.Reach END");
        all.Should().Contain("Ancestor4 = CASE WHEN nd.Depth = 4 THEN r.Seq WHEN nd.Depth > 4 THEN p.Ancestor4 ELSE NULL END");
        all.Should().NotContain("Ancestor5");
        all.Should().NotContain("FgaScope", "nothing of SqlOS's is on an application table");
        all.Should().NotContain("ResourceTypeId", "a retype changes no lineage");

        // Every maintaining trigger takes the lineage lock for its transaction before reading anything: inserts
        // shared, tree changes exclusive; the rebuild holds it for the whole session.
        all.Should().Contain("sys.sp_getapplock @Resource = N'SqlOS:FgaLineage:dbo.SqlOSFgaResources', @LockMode = 'Shared', @LockOwner = 'Transaction'");
        all.Should().Contain("sys.sp_getapplock @Resource = N'SqlOS:FgaLineage:dbo.SqlOSFgaResources', @LockMode = 'Exclusive', @LockOwner = 'Transaction'");
        all.Should().Contain("sys.sp_getapplock @Resource = N'SqlOS:FgaLineage:dbo.SqlOSFgaResources', @LockMode = 'Exclusive', @LockOwner = 'Session'");
        var update = batches.Single(b => b.Contains("AFTER UPDATE", StringComparison.Ordinal));
        update.Should().Contain("IF NOT (UPDATE(ParentId) OR UPDATE(IsActive)) RETURN;");
        update.IndexOf("@LockMode = 'Exclusive'", StringComparison.Ordinal).Should().BeGreaterThan(update.IndexOf("UPDATE(ParentId)", StringComparison.Ordinal), "an update that changes no parent or activity takes no lock");
        all.Should().Contain("SELECT Id FROM (SELECT Id, ParentId, IsActive FROM inserted EXCEPT SELECT Id, ParentId, IsActive FROM deleted) changed");
        all.Should().NotContain("INNER JOIN deleted d ON d.Id = i.Id", "a join of inserted and deleted has nothing to plan by");
        System.Text.RegularExpressions.Regex.IsMatch(all, @"BEGIN\s+END").Should().BeFalse("T-SQL rejects an empty block");
    }

    [TestMethod]
    public void RoutinesHash_ChangesWithAnyDefinitionOrOption()
    {
        var provider = SqlServerDatabaseProvider.Instance;
        string HashFor(SqlOSFgaOptions options)
            => SqlOSFgaFunctionInitializer.Hash(
                provider.BuildEnsureLineageColumnsSql(options)
                    .Concat([
                        provider.BuildActiveSubjectsFunctionSql(options), provider.BuildAccessRootsFunctionSql(options), provider.BuildListVisibleFunctionSql(options),
                        provider.BuildVisibleSetFunctionSql(options), provider.BuildListFirstFunctionSql(options), provider.BuildIsResourceAccessibleFunctionSql(options),
                        provider.BuildCheckRowFunctionSql(options)])
                    .Concat(provider.BuildLineageMaintenanceSql(options)));

        var baseline = HashFor(new SqlOSFgaOptions());
        HashFor(new SqlOSFgaOptions()).Should().Be(baseline, "the same definitions hash the same");
        HashFor(new SqlOSFgaOptions { MaxResourceHierarchyDepth = 11 }).Should().NotBe(baseline, "the depth changes the columns, the indexes, the guards, and the list");
        HashFor(new SqlOSFgaOptions { Schema = "other" }).Should().NotBe(baseline);
        baseline.Should().HaveLength(64);
    }

    [TestMethod]
    public void RoutinesHashQuery_RequiresEveryRoutineTriggerColumnAndIndex()
    {
        var options = new SqlOSFgaOptions { Schema = "ten'ant", MaxResourceHierarchyDepth = 3 };
        options.TableNames.Resources = "res]ources";

        var hash = SqlServerDatabaseProvider.Instance.BuildSelectRoutinesHashSql(options);

        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_ActiveSubjects]', N'IF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_AccessRoots]', N'IF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_ListVisible]', N'IF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_VisibleSet]', N'TF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_ListFirst]', N'IF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_CheckRow]', N'IF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_IsResourceAccessible]', N'IF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[sp_res]]ources_LineageRefresh]', N'P') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[sp_res]]ources_LineageRebuild]', N'P') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[TR_res]]ources_Lineage_Insert]', N'TR') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[TR_res]]ources_Lineage_Update]', N'TR') IS NOT NULL");
        hash.Should().NotContain("Lineage_Delete");
        hash.Should().Contain("COL_LENGTH('[ten''ant].[res]]ources]', 'Ancestor3') IS NOT NULL");
        hash.Should().Contain("(SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'[ten''ant].[res]]ources]') AND name IN (N'IX_res]ources_Ancestor0', N'IX_res]ources_Ancestor1', N'IX_res]ources_Ancestor2', N'IX_res]ources_Ancestor3')) = 4");
        hash.Should().NotContain("FgaScope");
    }

    [TestMethod]
    public void ResourcesTable_DeclaresItsTriggers_AndLeavesTheLineageToTheDatabase()
    {
        var options = new DbContextOptionsBuilder<LineageModelDbContext>()
            .UseSqlServer("Server=.;Database=SqlOS_Lineage;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var context = new LineageModelDbContext(options);
        var resources = context.Model.FindEntityType(typeof(SqlOSFgaResource))!;

        resources.GetDeclaredTriggers().Select(t => t.GetDatabaseName())
            .Should().BeEquivalentTo(SqlOSFgaLineage.TriggerNames("SqlOSFgaResources"));
        context.Model.FindEntityType(typeof(SqlOSFgaGrant))!.GetDeclaredTriggers().Should().BeEmpty("a grant changes no stored structure; the filter reads grants when it runs");

        // Only SqlOS's SQL routines read the lineage, so the model leaves it out and does not depend on the
        // configured depth: nothing in an application's model or migrations changes when the depth does.
        foreach (var name in new[] { "Seq", "Depth", "Reach", "Ancestor0", "Ancestor3" })
        {
            resources.FindProperty(name).Should().BeNull(name);
        }

        context.Model.FindEntityType(typeof(SqlOSFgaVisibleResource))!.FindPrimaryKey().Should().BeNull("the visible resources are a query result");
        context.Model.FindEntityType(typeof(SqlOSFgaRowCheck))!.FindPrimaryKey().Should().BeNull("a row check is a query result");
        context.Model.GetDbFunctions().Select(f => f.Name).Should().BeEquivalentTo(["fn_ActiveSubjects", "fn_VisibleSet", "fn_CheckRow"], "fn_IsResourceAccessible stays free for applications to map");
        context.Model.FindEntityType(typeof(SqlOSFgaAccessMatch))!.FindPrimaryKey().Should().BeNull("the point check's grant is a query result");
    }
}

file sealed class LineageModelDbContext(DbContextOptions<LineageModelDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.UseSqlOS(SqlOSDatabase.SqlServerProviderName, new SqlOSFgaOptions { MaxResourceHierarchyDepth = 3 });
}
