using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// The lineage invariant: every resource holds its depth, its ancestor at every level, and its reach (the
/// highest level from which access flows down to it through active resources only). The triggers must keep it
/// after every kind of write the resources table receives, and reject a cycle or an over-deep row. Application
/// tables carry nothing of it: a row's resource id is all a filter reads of them.
/// </summary>
[TestClass]
public class SqlOSFgaLineageIntegrationTests : FgaIntegrationTestBase
{
    private const int MaxDepth = 10;
    private const int Levels = MaxDepth + 1;

    private sealed record ResourceRow(string Id, string? ParentId, bool IsActive, string ResourceTypeId, long Seq, short? Depth, short? Reach, long?[] Ancestors);

    private sealed record Lineage(short? Depth, short? Reach, long?[] Ancestors);

    [TestMethod]
    public async Task InsertedChain_HasTheLineageOfEveryAncestor()
    {
        var ids = await CreateChainAsync("chain", "agency", "team", "project", "project");
        try
        {
            await AssertLineageMatchesAsync(ids);

            var rows = await ReadResourcesAsync();
            var byId = rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
            var deepest = byId[ids[^1]];
            deepest.Depth.Should().Be(4, "root, then four created ancestors");
            deepest.Reach.Should().Be(0, "every resource on the path is active");
            deepest.Ancestors[0].Should().Be(byId["root"].Seq);
            deepest.Ancestors[1].Should().Be(byId[ids[0]].Seq);
            deepest.Ancestors[4].Should().Be(deepest.Seq, "a resource is its own ancestor at its level");
            deepest.Ancestors[5].Should().BeNull();
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [TestMethod]
    public async Task MultiRowInsert_ParentAndChildInOneStatement()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var parent = $"multi_parent_{suffix}";
        var child = $"multi_child_{suffix}";
        var sql = TestDatabase.Rewrite(
            "INSERT INTO [dbo].[SqlOSFgaResources] ([Id], [ParentId], [Name], [ResourceTypeId], [IsActive], [CreatedAt], [UpdatedAt]) VALUES "
            + "({1}, {0}, 'Child', 'team', 1, SYSUTCDATETIME(), SYSUTCDATETIME()), "
            + "({0}, 'root', 'Parent', 'agency', 1, SYSUTCDATETIME(), SYSUTCDATETIME())");
        await Context.Database.ExecuteSqlRawAsync(sql, parent, child);
        try
        {
            await AssertLineageMatchesAsync([parent, child]);
            var rows = await ReadResourcesAsync();
            rows.Single(r => r.Id == child).Depth.Should().Be(2);
        }
        finally
        {
            await DeleteAsync([parent, child]);
        }
    }

    [TestMethod]
    public async Task Reparent_MovesTheSubtreeUnderTheNewAncestors()
    {
        var ids = await CreateChainAsync("move", "agency", "team", "project");
        var other = await CreateChainAsync("moveto", "agency");
        try
        {
            var team = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == ids[1]);
            team.ParentId = other[0];
            await Context.SaveChangesAsync();

            await AssertLineageMatchesAsync(ids.Concat(other).ToArray());
            var rows = await ReadResourcesAsync();
            var project = rows.Single(r => r.Id == ids[2]);
            project.Ancestors[1].Should().Be(rows.Single(r => r.Id == other[0]).Seq, "the project now sits under the other agency");
            project.Depth.Should().Be(3);
        }
        finally
        {
            await DeleteAsync(ids);
            await DeleteAsync(other);
        }
    }

    [TestMethod]
    public async Task DeactivatingAMiddleNode_CutsTheReachBelowIt_AndReactivatingRestoresIt()
    {
        var ids = await CreateChainAsync("deact", "agency", "team", "project");
        try
        {
            var team = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == ids[1]);
            team.IsActive = false;
            await Context.SaveChangesAsync();
            await AssertLineageMatchesAsync(ids);

            var rows = await ReadResourcesAsync();
            rows.Single(r => r.Id == ids[1]).Reach.Should().BeNull("an inactive resource is reachable from nowhere");
            rows.Single(r => r.Id == ids[2]).Reach.Should().Be(3, "only a grant on the project itself reaches it now");
            rows.Single(r => r.Id == ids[2]).Ancestors[1].Should().NotBeNull("the ancestors are the tree's shape, whatever is active");

            team.IsActive = true;
            await Context.SaveChangesAsync();
            await AssertLineageMatchesAsync(ids);
            (await ReadResourcesAsync()).Single(r => r.Id == ids[2]).Reach.Should().Be(0);
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [TestMethod]
    public async Task ChangingTheResourceType_LeavesTheLineageAlone()
    {
        var ids = await CreateChainAsync("retype", "agency", "team");
        try
        {
            var before = await ReadResourcesAsync();
            var team = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == ids[1]);
            team.ResourceTypeId = "project";
            await Context.SaveChangesAsync();

            var after = await ReadResourcesAsync();
            after.Single(r => r.Id == ids[1]).ResourceTypeId.Should().Be("project");
            after.Select(r => r with { ResourceTypeId = "" }).Should().BeEquivalentTo(before.Select(r => r with { ResourceTypeId = "" }), InKeyOrder, "a type is not part of the lineage");
            await AssertLineageMatchesAsync(ids);
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [TestMethod]
    public async Task DeletingALeaf_LeavesTheRestOfTheLineageAlone()
    {
        var ids = await CreateChainAsync("del", "agency", "team", "project");
        try
        {
            var entity = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == ids[2]);
            Context.Set<SqlOSFgaResource>().Remove(entity);
            await Context.SaveChangesAsync();

            (await ReadResourcesAsync()).Should().NotContain(r => r.Id == ids[2]);
            await AssertLineageMatchesAsync(ids[..2]);
        }
        finally
        {
            await DeleteAsync(ids[..2]);
        }
    }

    [TestMethod]
    public async Task ARowDeeperThanTheConfiguredDepth_IsRejected()
    {
        // root has level 0; a node at level MaxDepth is allowed, one more is not.
        var ids = new List<string>();
        var suffix = Guid.NewGuid().ToString("N");
        try
        {
            var parent = "root";
            for (var depth = 1; depth <= MaxDepth; depth++)
            {
                var id = $"deep_{depth}_{suffix}";
                Context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource { Id = id, ParentId = parent, Name = id, ResourceTypeId = "project" });
                await Context.SaveChangesAsync();
                ids.Add(id);
                parent = id;
            }

            await AssertLineageMatchesAsync(ids.ToArray());
            (await ReadResourcesAsync()).Single(r => r.Id == ids[^1]).Depth.Should().Be(MaxDepth);

            var tooDeep = new SqlOSFgaResource { Id = $"deep_{MaxDepth + 1}_{suffix}", ParentId = parent, Name = "too deep", ResourceTypeId = "project" };
            Context.Set<SqlOSFgaResource>().Add(tooDeep);
            var act = () => Context.SaveChangesAsync();
            var failure = await act.Should().ThrowAsync<DbUpdateException>();
            failure.Which.InnerException.Should().BeAssignableTo<DbException>()
                .Which.Message.Should().Contain("maximum hierarchy depth");
            Context.Entry(tooDeep).State = EntityState.Detached;
        }
        finally
        {
            Context.ChangeTracker.Clear();
            await DeleteAsync(ids.ToArray());
        }
    }

    [TestMethod]
    public async Task MovingASubtree_TooDeep_IsRejected()
    {
        // A chain of MaxDepth - 1 nodes under root, and a two-node chain: moving the two-node chain under the
        // deep chain's end would place its leaf at level MaxDepth + 1.
        var deep = new List<string>();
        var suffix = Guid.NewGuid().ToString("N");
        var pair = await CreateChainAsync("pair", "agency", "team");
        try
        {
            var parent = "root";
            for (var depth = 1; depth <= MaxDepth - 1; depth++)
            {
                var id = $"deepmove_{depth}_{suffix}";
                Context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource { Id = id, ParentId = parent, Name = id, ResourceTypeId = "project" });
                await Context.SaveChangesAsync();
                deep.Add(id);
                parent = id;
            }

            var act = () => Context.Database.ExecuteSqlRawAsync(
                TestDatabase.Rewrite("UPDATE [dbo].[SqlOSFgaResources] SET [ParentId] = {0} WHERE [Id] = {1}"),
                deep[^1],
                pair[0]);
            (await act.Should().ThrowAsync<DbException>()).Which.Message.Should().Contain("maximum hierarchy depth");
            await AssertLineageMatchesAsync(pair.Concat(deep).ToArray());
            (await ReadResourcesAsync()).Single(r => r.Id == pair[0]).ParentId.Should().Be("root", "the statement was rolled back");
        }
        finally
        {
            Context.ChangeTracker.Clear();
            await DeleteAsync(pair);
            await DeleteAsync(deep.ToArray());
        }
    }

    [TestMethod]
    public async Task ACycle_IsRejected_AndTheLineageStands()
    {
        var ids = await CreateChainAsync("cycle", "agency", "team", "project");
        try
        {
            var act = () => Context.Database.ExecuteSqlRawAsync(
                TestDatabase.Rewrite("UPDATE [dbo].[SqlOSFgaResources] SET [ParentId] = {0} WHERE [Id] = {1}"),
                ids[2],
                ids[0]);
            (await act.Should().ThrowAsync<DbException>()).Which.Message.Should().Contain("cycle");
            await AssertLineageMatchesAsync(ids);
            (await ReadResourcesAsync()).Single(r => r.Id == ids[0]).ParentId.Should().Be("root");
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [TestMethod]
    public async Task Rebuild_ReproducesTheTriggerMaintainedLineage()
    {
        var ids = await CreateChainAsync("rebuild", "agency", "team", "project");
        var other = await CreateChainAsync("rebuild2", "agency", "team");
        try
        {
            var team = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == other[1]);
            team.IsActive = false;
            await Context.SaveChangesAsync();

            var maintained = await ReadResourcesAsync();
            await Context.Database.ExecuteSqlRawAsync(TestDatabase.IsPostgreSql
                ? "SELECT \"dbo\".\"fn_SqlOSFgaResources_LineageRebuild\"()"
                : "EXEC [dbo].[sp_SqlOSFgaResources_LineageRebuild]");

            (await ReadResourcesAsync()).Should().BeEquivalentTo(maintained, InKeyOrder);
            maintained.Should().Contain(r => r.Depth == 3);
        }
        finally
        {
            await DeleteAsync(ids);
            await DeleteAsync(other);
        }
    }

    [TestMethod]
    public async Task WholeTable_SatisfiesTheInvariant()
    {
        // Everything the other suites left behind, checked against the walk-up definition.
        var all = (await ReadResourcesAsync()).Select(r => r.Id).ToArray();
        await AssertLineageMatchesAsync(all);
    }

    [TestMethod]
    public async Task EveryLevel_HasItsIndex()
    {
        // fn_ListVisible seeks the index of a root's level for the rows beneath it; one per level, on the resources
        // table, and nothing on the application's table.
        for (var level = 0; level < Levels; level++)
        {
            (await TestCatalog.IndexExistsAsync(Context, "SqlOSFgaResources", SqlOSFgaLineage.AncestorIndexName("SqlOSFgaResources", level)))
                .Should().BeTrue("level {0} has its index", level);
        }

        (await TestCatalog.IndexExistsAsync(Context, "SqlOSFgaResources", SqlOSFgaLineage.AncestorIndexName("SqlOSFgaResources", Levels))).Should().BeFalse();
    }

    [TestMethod]
    public async Task BulkMove_SeveralSubtreesInOneStatement()
    {
        var from = await CreateChainAsync("bulk_from", "agency");
        var to = await CreateChainAsync("bulk_to", "agency");
        var teams = new List<string[]>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                teams.Add(await CreateUnderAsync(from[0], $"bulk_team{i}", "team", "project"));
            }

            // One UPDATE moves every team of one agency under another.
            await Context.Database.ExecuteSqlRawAsync(
                TestDatabase.Rewrite("UPDATE [dbo].[SqlOSFgaResources] SET [ParentId] = {0} WHERE [ParentId] = {1}"), to[0], from[0]);

            await AssertLineageMatchesAsync([.. from, .. to, .. teams.SelectMany(t => t)]);
            var rows = await ReadResourcesAsync();
            var toSeq = rows.Single(r => r.Id == to[0]).Seq;
            foreach (var team in teams)
            {
                rows.Single(r => r.Id == team[1]).Ancestors[1].Should().Be(toSeq, "every moved project now sits under the other agency");
            }
        }
        finally
        {
            foreach (var team in teams)
            {
                await DeleteAsync(team);
            }

            await DeleteAsync(from);
            await DeleteAsync(to);
        }
    }

    [TestMethod]
    public async Task ReactivatingUnderAnAncestorThatIsStillInactive_KeepsTheReachCut()
    {
        var ids = await CreateChainAsync("nested_deact", "agency", "team", "project");
        try
        {
            await SetActiveAsync(ids[0], false);
            await SetActiveAsync(ids[1], false);
            await SetActiveAsync(ids[1], true);

            await AssertLineageMatchesAsync(ids);
            (await ReadResourcesAsync()).Single(r => r.Id == ids[2]).Reach.Should().Be(2, "the agency is still inactive: access flows from the team down");

            await SetActiveAsync(ids[0], true);
            await AssertLineageMatchesAsync(ids);
            (await ReadResourcesAsync()).Single(r => r.Id == ids[2]).Reach.Should().Be(0);
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [TestMethod]
    public async Task Rebuild_AcrossManyRanges_ReproducesTheLineage()
    {
        var ids = await CreateChainAsync("ranges", "agency", "team", "project");
        try
        {
            var maintained = await ReadResourcesAsync();

            // SQL Server commits the rebuild in ranges of the key; three resources a range makes many ranges here.
            await Context.Database.ExecuteSqlRawAsync(
                TestDatabase.IsPostgreSql
                    ? "SELECT \"dbo\".\"fn_SqlOSFgaResources_LineageRebuild\"()"
                    : "EXEC [dbo].[sp_SqlOSFgaResources_LineageRebuild] @RangeRows = 3");

            (await ReadResourcesAsync()).Should().BeEquivalentTo(maintained, InKeyOrder);
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [TestMethod]
    public async Task Startup_OnADatabaseWithoutLineage_BuildsIt()
    {
        // The state a database is in before its first start: resources with no lineage and no stored routines hash.
        var ids = await CreateChainAsync("upgrade", "agency", "team", "project");
        try
        {
            var maintained = await ReadResourcesAsync();
            var ancestors = string.Join(", ", Enumerable.Range(0, Levels).Select(l => $"[Ancestor{l}] = NULL"));
            await Context.Database.ExecuteSqlRawAsync(TestDatabase.Rewrite($"UPDATE [dbo].[SqlOSFgaResources] SET [Depth] = NULL, [Reach] = NULL, {ancestors}"));
            await Context.Database.ExecuteSqlRawAsync(TestDatabase.Rewrite("UPDATE [dbo].[SqlOSFgaSchema] SET [RoutinesHash] = NULL"));
            await ClearLineageBuiltAsync();

            await new SqlOSFgaFunctionInitializer(Context, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaFunctionInitializer>.Instance)
                .EnsureFunctionsExistAsync();

            (await ReadResourcesAsync()).Should().BeEquivalentTo(maintained, InKeyOrder);
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [TestMethod]
    public async Task AnInterruptedRebuild_IsFinishedByTheNextStart()
    {
        // SQL Server commits the rebuild range by range. One that fails after the ranges holding the roots have
        // committed leaves the later rows without a lineage, and the next start must finish it rather than take
        // the built roots for a finished build. (PostgreSQL rebuilds in one transaction: a failure leaves the
        // previous lineage, and nothing half done.)
        if (TestDatabase.IsPostgreSql)
        {
            return;
        }

        // "zz" sorts after every other id, so this chain's last resource lies in the last range.
        var ids = await CreateChainAsync("zz_interrupted", "agency", "team", "project");
        try
        {
            var maintained = await ReadResourcesAsync();

            // A database before its first build, whose build then fails in its last range.
            var ancestors = string.Join(", ", Enumerable.Range(0, Levels).Select(l => $"[Ancestor{l}] = NULL"));
            var clearLineage = $"UPDATE [dbo].[SqlOSFgaResources] SET [Depth] = NULL, [Reach] = NULL, {ancestors}";
            var failInTheLastRange = $"ALTER TABLE [dbo].[SqlOSFgaResources] WITH NOCHECK ADD CONSTRAINT [CK_Test_InterruptRebuild] CHECK ([Id] <> N'{ids[2]}' OR [Depth] IS NULL)";
            await Context.Database.ExecuteSqlRawAsync(clearLineage);
            await Context.Database.ExecuteSqlRawAsync("UPDATE [dbo].[SqlOSFgaSchema] SET [RoutinesHash] = NULL");
            await Context.Database.ExecuteSqlRawAsync(failInTheLastRange);
            try
            {
                var rebuild = () => Context.Database.ExecuteSqlRawAsync("EXEC [dbo].[sp_SqlOSFgaResources_LineageRebuild] @RangeRows = 3");
                await rebuild.Should().ThrowAsync<Exception>();
            }
            finally
            {
                await Context.Database.ExecuteSqlRawAsync("ALTER TABLE [dbo].[SqlOSFgaResources] DROP CONSTRAINT [CK_Test_InterruptRebuild]");
            }

            (await ReadResourcesAsync()).Single(r => r.Id == "root").Depth.Should().Be(0, "the first ranges committed");
            (await ReadResourcesAsync()).Single(r => r.Id == ids[2]).Depth.Should().BeNull("the last range did not");

            await new SqlOSFgaFunctionInitializer(Context, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaFunctionInitializer>.Instance)
                .EnsureFunctionsExistAsync();

            (await ReadResourcesAsync()).Should().BeEquivalentTo(maintained, InKeyOrder);
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [TestMethod]
    public async Task Startup_AfterANewDefinition_ReappliesTheRoutines_AndKeepsTheLineage()
    {
        // A new SqlOS version or a changed option: the definitions are applied again; a lineage that is built
        // stays as it is.
        var ids = await CreateChainAsync("redefine", "agency", "team");
        try
        {
            var maintained = await ReadResourcesAsync();
            await Context.Database.ExecuteSqlRawAsync(TestDatabase.Rewrite("UPDATE [dbo].[SqlOSFgaSchema] SET [RoutinesHash] = NULL"));

            await new SqlOSFgaFunctionInitializer(Context, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaFunctionInitializer>.Instance)
                .EnsureFunctionsExistAsync();

            (await ReadResourcesAsync()).Should().BeEquivalentTo(maintained, InKeyOrder);
            await AssertLineageMatchesAsync(ids);
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    private static async Task<string[]> CreateUnderAsync(string parent, string prefix, params string[] types)
    {
        Context.ChangeTracker.Clear();
        var suffix = Guid.NewGuid().ToString("N");
        var ids = new string[types.Length];
        for (var i = 0; i < types.Length; i++)
        {
            ids[i] = $"{prefix}_{i}_{suffix}";
            Context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource { Id = ids[i], ParentId = parent, Name = ids[i], ResourceTypeId = types[i] });
            parent = ids[i];
        }

        await Context.SaveChangesAsync();
        return ids;
    }

    private static async Task SetActiveAsync(string id, bool active)
    {
        Context.ChangeTracker.Clear();
        var resource = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == id);
        resource.IsActive = active;
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();
    }

    // ---- Concurrent transactions: each test holds two transactions open on separate connections with a fixed
    // interleaving, commits both, and checks the committed tree against the walk-up definition. ----

    [TestMethod]
    public async Task Concurrent_InsertUnderAParent_ThenTheParentIsDeactivated()
    {
        var ids = await CreateChainAsync("race_ins", "agency", "team");
        var child = $"race_ins_child_{Guid.NewGuid():N}";
        try
        {
            await using var first = await Tx.BeginAsync();
            await first.InsertResourceAsync(child, ids[1], "project");

            await using var second = await Tx.BeginAsync();
            await RaceAsync(first, second, second.SetActiveAsync(ids[1], false));

            await AssertLineageMatchesAsync([.. ids, child]);
            (await ReadResourcesAsync()).Single(r => r.Id == child).Reach.Should().Be(3, "the team is inactive: only a grant on the project itself reaches it");
        }
        finally
        {
            await DeleteAsync([.. ids, child]);
        }
    }

    [TestMethod]
    public async Task Concurrent_TheParentIsDeactivated_ThenAChildIsInsertedUnderIt()
    {
        var ids = await CreateChainAsync("race_deact", "agency", "team");
        var child = $"race_deact_child_{Guid.NewGuid():N}";
        try
        {
            await using var first = await Tx.BeginAsync();
            await first.SetActiveAsync(ids[1], false);

            await using var second = await Tx.BeginAsync();
            await RaceAsync(first, second, Task.Run(() => second.InsertResourceAsync(child, ids[1], "project")));

            await AssertLineageMatchesAsync([.. ids, child]);
            (await ReadResourcesAsync()).Single(r => r.Id == child).Reach.Should().Be(3, "the team is inactive: only a grant on the project itself reaches it");
        }
        finally
        {
            await DeleteAsync([.. ids, child]);
        }
    }

    [TestMethod]
    public async Task Concurrent_ASubtreeMovesUnderAParent_WhileTheParentIsDeactivated()
    {
        var target = await CreateChainAsync("race_move_target", "agency", "team");
        var moving = await CreateChainAsync("race_move_src", "agency", "team", "project");
        try
        {
            await using var first = await Tx.BeginAsync();
            await first.SetParentAsync(moving[1], target[1]);

            await using var second = await Tx.BeginAsync();
            await RaceAsync(first, second, second.SetActiveAsync(target[1], false));

            await AssertLineageMatchesAsync([.. target, .. moving]);
            (await ReadResourcesAsync()).Single(r => r.Id == moving[2]).Reach.Should().Be(3, "the moved team now sits under an inactive team");
        }
        finally
        {
            await DeleteAsync(moving);
            await DeleteAsync(target);
        }
    }

    [TestMethod]
    public async Task Concurrent_TwoMovesThatTogetherFormACycle_DoNotBothCommit()
    {
        var a = await CreateChainAsync("race_cycle_a", "agency", "team");
        var b = await CreateChainAsync("race_cycle_b", "agency", "team");
        try
        {
            // Each move alone is fine; together they put a's team under b's team and b's team under a's team.
            await using var first = await Tx.BeginAsync();
            await first.SetParentAsync(a[1], b[1]);

            await using var second = await Tx.BeginAsync();
            await RaceAsync(first, second, second.SetParentAsync(b[1], a[1]));

            await AssertLineageMatchesAsync([.. a, .. b]);
            var rows = (await ReadResourcesAsync()).ToDictionary(r => r.Id, StringComparer.Ordinal);
            (rows[a[1]].ParentId == b[1] && rows[b[1]].ParentId == a[1]).Should().BeFalse("a cycle must never commit");
        }
        finally
        {
            await Context.Database.ExecuteSqlRawAsync(TestDatabase.Rewrite("UPDATE [dbo].[SqlOSFgaResources] SET [ParentId] = {0} WHERE [Id] = {1}"), a[0], a[1]);
            await Context.Database.ExecuteSqlRawAsync(TestDatabase.Rewrite("UPDATE [dbo].[SqlOSFgaResources] SET [ParentId] = {0} WHERE [Id] = {1}"), b[0], b[1]);
            await DeleteAsync(a);
            await DeleteAsync(b);
        }
    }

    [TestMethod]
    public async Task PostgreSql_ARepeatableReadTransaction_CannotChangeTheTree()
    {
        if (!TestDatabase.IsPostgreSql)
        {
            return;
        }

        var ids = await CreateChainAsync("repeatable", "agency", "team");
        try
        {
            await using var connection = TestDatabase.CreateConnection(Context.Database.GetConnectionString()!);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE \"dbo\".\"SqlOSFgaResources\" SET \"IsActive\" = false WHERE \"Id\" = @id";
            TestDatabase.AddParameter(command, "@id", ids[1]);

            var act = () => command.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<DbException>()).Which.Message.Should().Contain("REPEATABLE READ");
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    /// <summary>
    /// The second transaction's work starts while the first is open; it either finishes or waits on a lock. The
    /// first commits, then the second finishes and commits. A transaction the database rejects (a deadlock victim,
    /// or a malformed tree) rolls back, as it would in an application.
    /// </summary>
    private static async Task RaceAsync(Tx first, Tx second, Task secondWork)
    {
        await Task.WhenAny(secondWork, Task.Delay(TimeSpan.FromSeconds(2)));
        await first.TryCommitAsync();
        try
        {
            await secondWork;
            await second.TryCommitAsync();
        }
        catch (DbException)
        {
            await second.RollbackAsync();
        }
    }

    private sealed class Tx : IAsyncDisposable
    {
        private readonly DbConnection _connection;
        private readonly DbTransaction _transaction;
        private bool _done;

        private Tx(DbConnection connection, DbTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public static async Task<Tx> BeginAsync()
        {
            var connection = TestDatabase.CreateConnection(Context.Database.GetConnectionString()!);
            await connection.OpenAsync();
            return new Tx(connection, await connection.BeginTransactionAsync());
        }

        public Task InsertResourceAsync(string id, string parentId, string typeId)
            => ExecuteAsync(
                "INSERT INTO [dbo].[SqlOSFgaResources] ([Id], [ParentId], [Name], [ResourceTypeId], [IsActive], [CreatedAt], [UpdatedAt]) VALUES (@id, @parent, @id, @type, @active, @now, @now)",
                ("@id", id), ("@parent", parentId), ("@type", typeId), ("@active", true), ("@now", DateTime.UtcNow));

        public Task SetActiveAsync(string id, bool active)
            => ExecuteAsync("UPDATE [dbo].[SqlOSFgaResources] SET [IsActive] = @active WHERE [Id] = @id", ("@id", id), ("@active", active));

        public Task SetParentAsync(string id, string parentId)
            => ExecuteAsync("UPDATE [dbo].[SqlOSFgaResources] SET [ParentId] = @parent WHERE [Id] = @id", ("@id", id), ("@parent", parentId));

        public async Task TryCommitAsync()
        {
            if (_done)
            {
                return;
            }

            try
            {
                await _transaction.CommitAsync();
            }
            catch (DbException)
            {
                await RollbackAsync();
            }

            _done = true;
        }

        public async Task RollbackAsync()
        {
            if (_done)
            {
                return;
            }

            try
            {
                await _transaction.RollbackAsync();
            }
            catch (Exception)
            {
                // Already rolled back by the server (a deadlock victim).
            }

            _done = true;
        }

        private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandTimeout = 60;
            command.CommandText = TestDatabase.Rewrite(sql);
            foreach (var (name, value) in parameters)
            {
                TestDatabase.AddParameter(command, name, value);
            }

            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await RollbackAsync();
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private static async Task<string[]> CreateChainAsync(string prefix, params string[] types)
    {
        Context.ChangeTracker.Clear();
        var suffix = Guid.NewGuid().ToString("N");
        var ids = new string[types.Length];
        var parent = "root";
        for (var i = 0; i < types.Length; i++)
        {
            ids[i] = $"{prefix}_{i}_{suffix}";
            Context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource { Id = ids[i], ParentId = parent, Name = ids[i], ResourceTypeId = types[i] });
            parent = ids[i];
        }

        await Context.SaveChangesAsync();
        return ids;
    }

    /// <summary>Deletes children before parents: the ids are in creation order.</summary>
    private static async Task DeleteAsync(string[] ids)
    {
        Context.ChangeTracker.Clear();
        foreach (var id in Enumerable.Reverse(ids))
        {
            var entity = await Context.Set<SqlOSFgaResource>().SingleOrDefaultAsync(r => r.Id == id);
            if (entity != null)
            {
                Context.Set<SqlOSFgaResource>().Remove(entity);
                await Context.SaveChangesAsync();
            }
        }
    }

    /// <summary>The flag a database has before its lineage is first built.</summary>
    private static Task ClearLineageBuiltAsync()
        => Context.Database.ExecuteSqlRawAsync(TestDatabase.IsPostgreSql
            ? "UPDATE \"dbo\".\"SqlOSFgaSchema\" SET \"LineageBuilt\" = false"
            : "UPDATE [dbo].[SqlOSFgaSchema] SET [LineageBuilt] = 0");

    /// <summary>The lineage of the given resources matches the definition.</summary>
    private static async Task AssertLineageMatchesAsync(string[] ids)
    {
        var rows = await ReadResourcesAsync();
        var byId = rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var actual = byId[id];
            var expected = Expected(byId, actual);
            new Lineage(actual.Depth, actual.Reach, actual.Ancestors).Should().BeEquivalentTo(expected, "the lineage of {0} must match the walk-up definition", id);
        }
    }

    /// <summary>The definition: walk up from the resource; a chain that fails to reach a root within the depth limit is malformed.</summary>
    private static Lineage Expected(Dictionary<string, ResourceRow> byId, ResourceRow resource)
    {
        var chain = new List<ResourceRow> { resource };
        var current = resource.ParentId;
        while (current != null)
        {
            if (!byId.TryGetValue(current, out var ancestor) || chain.Count > MaxDepth)
            {
                return new Lineage(null, null, new long?[Levels]);
            }

            chain.Add(ancestor);
            current = ancestor.ParentId;
        }

        var depth = chain.Count - 1;
        var ancestors = new long?[Levels];
        for (var level = 0; level <= depth; level++)
        {
            ancestors[level] = chain[depth - level].Seq;
        }

        short? reach = null;
        if (resource.IsActive)
        {
            var r = depth;
            for (var level = depth - 1; level >= 0 && chain[depth - level].IsActive; level--)
            {
                r = level;
            }

            reach = (short)r;
        }

        return new Lineage((short)depth, reach, ancestors);
    }

    private static Task<List<ResourceRow>> ReadResourcesAsync()
        => ReadAsync(
            TestDatabase.Rewrite($"SELECT [Id], [ParentId], [IsActive], [ResourceTypeId], [Seq], [Depth], [Reach], {AncestorColumns("Ancestor")} FROM [dbo].[SqlOSFgaResources] ORDER BY [Seq]"),
            reader => new ResourceRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt16(5),
                reader.IsDBNull(6) ? null : reader.GetInt16(6),
                ReadAncestors(reader, 7)));

    /// <summary>
    /// Whole-table comparisons in the order <see cref="ReadResourcesAsync"/> reads (by Seq): matching rows in any
    /// order costs the square of the table, and the shared database holds every suite's resources.
    /// </summary>
    private static FluentAssertions.Equivalency.EquivalencyAssertionOptions<ResourceRow> InKeyOrder(FluentAssertions.Equivalency.EquivalencyAssertionOptions<ResourceRow> options)
        => options.WithStrictOrdering();

    private static string AncestorColumns(string prefix)
        => string.Join(", ", Enumerable.Range(0, Levels).Select(l => $"[{prefix}{l}]"));

    private static long?[] ReadAncestors(DbDataReader reader, int first)
    {
        var ancestors = new long?[Levels];
        for (var level = 0; level < Levels; level++)
        {
            ancestors[level] = reader.IsDBNull(first + level) ? null : reader.GetInt64(first + level);
        }

        return ancestors;
    }

    private static async Task<List<T>> ReadAsync<T>(string sql, Func<DbDataReader, T> map)
    {
        var connection = Context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<T>();
            while (await reader.ReadAsync())
            {
                rows.Add(map(reader));
            }

            return rows;
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }
}
