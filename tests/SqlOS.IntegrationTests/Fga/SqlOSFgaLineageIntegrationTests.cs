using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// The lineage invariant: every resource holds its depth, its ancestor at every level, and its reach (the
/// highest level from which access flows down to it through active resources only), and every application
/// row with scope columns holds its resource's lineage and type. The triggers must keep it after every kind
/// of write the resources table and the application table receive, and reject a cycle or an over-deep row.
/// </summary>
[TestClass]
public class SqlOSFgaLineageIntegrationTests : FgaIntegrationTestBase
{
    private const int MaxDepth = 10;
    private const int Levels = MaxDepth + 1;

    private sealed record ResourceRow(string Id, string? ParentId, bool IsActive, string ResourceTypeId, long Seq, short? Depth, short? Reach, long?[] Ancestors);

    private sealed record Lineage(short? Depth, short? Reach, long?[] Ancestors);

    private sealed record EntityRow(string Id, string ResourceId, long?[] Ancestors, short? Reach, int? TypeSeq);

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
    public async Task ChangingTheResourceType_UpdatesTheScopeTypeOfItsRows()
    {
        var ids = await CreateChainAsync("retype", "agency", "team");
        var entityId = await CreateEntityAsync(ids[1]);
        try
        {
            var typeSeq = await ReadTypeSeqAsync();
            (await ReadEntityAsync(entityId)).TypeSeq.Should().Be(typeSeq["team"]);

            var team = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == ids[1]);
            team.ResourceTypeId = "project";
            await Context.SaveChangesAsync();

            (await ReadEntityAsync(entityId)).TypeSeq.Should().Be(typeSeq["project"]);
            await AssertLineageMatchesAsync(ids);
        }
        finally
        {
            await DeleteEntityAsync(entityId);
            await DeleteAsync(ids);
        }
    }

    [TestMethod]
    public async Task DeletingAResource_ClearsTheScopeColumnsOfItsRows()
    {
        var ids = await CreateChainAsync("del", "agency", "team", "project");
        var entityId = await CreateEntityAsync(ids[2]);
        try
        {
            (await ReadEntityAsync(entityId)).Reach.Should().Be(0);

            var entity = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == ids[2]);
            Context.Set<SqlOSFgaResource>().Remove(entity);
            await Context.SaveChangesAsync();

            var row = await ReadEntityAsync(entityId);
            row.Reach.Should().BeNull("a row whose resource is gone is visible to nobody");
            row.TypeSeq.Should().BeNull();
            row.Ancestors.Should().AllSatisfy(a => a.Should().BeNull());
            await AssertLineageMatchesAsync(ids[..2]);
        }
        finally
        {
            await DeleteEntityAsync(entityId);
            await DeleteAsync(ids[..2]);
        }
    }

    [TestMethod]
    public async Task ScopeColumns_FollowTheRowsResource()
    {
        var ids = await CreateChainAsync("follow", "agency", "team");
        var suffix = Guid.NewGuid().ToString("N");
        var entityId = $"follow_{suffix}";
        var pending = $"follow_pending_{suffix}";
        try
        {
            // The application row arrives before its resource exists: it takes the lineage when the resource is created.
            await Context.Database.ExecuteSqlRawAsync(
                TestDatabase.Rewrite("INSERT INTO [LifecycleProtectedEntities] ([Id], [ResourceId], [Rank]) VALUES ({0}, {1}, 0)"),
                entityId,
                pending);
            (await ReadEntityAsync(entityId)).Reach.Should().BeNull();

            Context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource { Id = pending, ParentId = ids[1], Name = pending, ResourceTypeId = "project" });
            await Context.SaveChangesAsync();
            var row = await ReadEntityAsync(entityId);
            row.Reach.Should().Be(0);
            row.Ancestors[3].Should().Be((await ReadResourcesAsync()).Single(r => r.Id == pending).Seq);

            // Pointing the row at another resource takes that resource's lineage; at nothing, none.
            await Context.Database.ExecuteSqlRawAsync(
                TestDatabase.Rewrite("UPDATE [LifecycleProtectedEntities] SET [ResourceId] = {0} WHERE [Id] = {1}"),
                ids[0],
                entityId);
            (await ReadEntityAsync(entityId)).Ancestors[1].Should().Be((await ReadResourcesAsync()).Single(r => r.Id == ids[0]).Seq);
            await Context.Database.ExecuteSqlRawAsync(
                TestDatabase.Rewrite("UPDATE [LifecycleProtectedEntities] SET [ResourceId] = {0} WHERE [Id] = {1}"),
                "no_such_resource",
                entityId);
            (await ReadEntityAsync(entityId)).Reach.Should().BeNull();
        }
        finally
        {
            await DeleteEntityAsync(entityId);
            await DeleteAsync([pending]);
            await DeleteAsync(ids);
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
        var entityId = await CreateEntityAsync(ids[2]);
        try
        {
            var team = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == other[1]);
            team.IsActive = false;
            await Context.SaveChangesAsync();

            var maintained = await ReadResourcesAsync();
            var maintainedEntities = await ReadEntitiesAsync();
            await Context.Database.ExecuteSqlRawAsync(TestDatabase.IsPostgreSql
                ? "SELECT \"dbo\".\"fn_SqlOSFgaResources_LineageRebuild\"()"
                : "EXEC [dbo].[sp_SqlOSFgaResources_LineageRebuild]");

            (await ReadResourcesAsync()).Should().BeEquivalentTo(maintained);
            (await ReadEntitiesAsync()).Should().BeEquivalentTo(maintainedEntities);
            maintained.Should().Contain(r => r.Depth == 3);
        }
        finally
        {
            await DeleteEntityAsync(entityId);
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

    private static async Task<string> CreateEntityAsync(string resourceId)
    {
        var id = $"lineage_{Guid.NewGuid():N}";
        Context.Set<LifecycleProtectedEntity>().Add(new LifecycleProtectedEntity { Id = id, ResourceId = resourceId });
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();
        return id;
    }

    private static Task DeleteEntityAsync(string id)
        => Context.Database.ExecuteSqlRawAsync(TestDatabase.Rewrite("DELETE FROM [LifecycleProtectedEntities] WHERE [Id] = {0}"), id);

    /// <summary>The lineage of the given resources, and the scope columns of every application row, match the definition.</summary>
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

        var typeSeq = await ReadTypeSeqAsync();
        foreach (var row in await ReadEntitiesAsync())
        {
            if (byId.TryGetValue(row.ResourceId, out var resource))
            {
                new Lineage(null, row.Reach, row.Ancestors).Should().BeEquivalentTo(
                    new Lineage(null, resource.Reach, resource.Ancestors), "row {0} must carry the lineage of {1}", row.Id, row.ResourceId);
                row.TypeSeq.Should().Be(typeSeq[resource.ResourceTypeId]);
            }
            else
            {
                row.Reach.Should().BeNull("row {0} has no resource", row.Id);
                row.TypeSeq.Should().BeNull();
            }
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
            TestDatabase.Rewrite($"SELECT [Id], [ParentId], [IsActive], [ResourceTypeId], [Seq], [Depth], [Reach], {AncestorColumns("Ancestor")} FROM [dbo].[SqlOSFgaResources]"),
            reader => new ResourceRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt16(5),
                reader.IsDBNull(6) ? null : reader.GetInt16(6),
                ReadAncestors(reader, 7)));

    private static async Task<EntityRow> ReadEntityAsync(string id)
        => (await ReadEntitiesAsync()).Single(e => e.Id == id);

    private static Task<List<EntityRow>> ReadEntitiesAsync()
        => ReadAsync(
            TestDatabase.Rewrite($"SELECT [Id], [ResourceId], [SqlOSFgaReach], [SqlOSFgaTypeSeq], {AncestorColumns("SqlOSFgaAncestor")} FROM [LifecycleProtectedEntities]"),
            reader => new EntityRow(
                reader.GetString(0),
                reader.GetString(1),
                ReadAncestors(reader, 4),
                reader.IsDBNull(2) ? null : reader.GetInt16(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3)));

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

    private static async Task<Dictionary<string, int>> ReadTypeSeqAsync()
        => (await ReadAsync(
            TestDatabase.Rewrite("SELECT [Id], [Seq] FROM [dbo].[SqlOSFgaResourceTypes]"),
            reader => (reader.GetString(0), reader.GetInt32(1))))
            .ToDictionary(t => t.Item1, t => t.Item2, StringComparer.Ordinal);

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
