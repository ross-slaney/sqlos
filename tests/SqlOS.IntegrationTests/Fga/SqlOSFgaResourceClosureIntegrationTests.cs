using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// The resource closure invariant: for every resource, exactly one row per proper ancestor along a fully
/// active path, keyed by the descendant's type. The triggers must keep it after every kind of write the
/// resource table receives.
/// </summary>
[TestClass]
public class SqlOSFgaResourceClosureIntegrationTests : FgaIntegrationTestBase
{
    private const int MaxDepth = 10;

    private sealed record ResourceRow(string Id, string? ParentId, bool IsActive, string ResourceTypeId, long Seq);

    private sealed record ClosureRow(long AncestorSeq, int TypeSeq, long DescendantSeq);

    [TestMethod]
    public async Task InsertedChain_HasOnePairPerActiveAncestor()
    {
        var ids = await CreateChainAsync("chain", "agency", "team", "project", "project");
        try
        {
            await AssertClosureMatchesAsync(ids);

            // Explicitly: the deepest node lists root and every node above it, in the paper's sense of ancestors.
            var expected = await ExpectedAsync(ids);
            var deepest = (await ReadResourcesAsync()).Single(r => r.Id == ids[^1]);
            expected.Count(p => p.DescendantSeq == deepest.Seq).Should().Be(ids.Length - 1 + 1, "three created ancestors plus the root");
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
            + "({0}, 'root', 'Parent', 'agency', 1, SYSUTCDATETIME(), SYSUTCDATETIME()), "
            + "({1}, {0}, 'Child', 'team', 1, SYSUTCDATETIME(), SYSUTCDATETIME())");
        await Context.Database.ExecuteSqlRawAsync(sql, parent, child);
        try
        {
            await AssertClosureMatchesAsync([parent, child]);
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

            await AssertClosureMatchesAsync(ids.Concat(other).ToArray());
            var expected = await ExpectedAsync(ids);
            var rows = await ReadResourcesAsync();
            var project = rows.Single(r => r.Id == ids[2]);
            var oldAgency = rows.Single(r => r.Id == ids[0]);
            var newAgency = rows.Single(r => r.Id == other[0]);
            expected.Should().NotContain(p => p.DescendantSeq == project.Seq && p.AncestorSeq == oldAgency.Seq);
            expected.Should().Contain(p => p.DescendantSeq == project.Seq && p.AncestorSeq == newAgency.Seq);
        }
        finally
        {
            await DeleteAsync(ids);
            await DeleteAsync(other);
        }
    }

    [TestMethod]
    public async Task DeactivatingAMiddleNode_CutsEveryPathThroughIt_AndReactivatingRestoresThem()
    {
        var ids = await CreateChainAsync("deact", "agency", "team", "project");
        try
        {
            var team = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == ids[1]);
            team.IsActive = false;
            await Context.SaveChangesAsync();
            await AssertClosureMatchesAsync(ids);

            var rows = await ReadResourcesAsync();
            var project = rows.Single(r => r.Id == ids[2]);
            (await ActualAsync(ids)).Should().NotContain(p => p.DescendantSeq == project.Seq, "an inactive node cuts every path through it");

            team.IsActive = true;
            await Context.SaveChangesAsync();
            await AssertClosureMatchesAsync(ids);
            (await ActualAsync(ids)).Count(p => p.DescendantSeq == project.Seq).Should().Be(3, "root, agency, team");
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [TestMethod]
    public async Task ChangingTheResourceType_RekeysItsPairs()
    {
        var ids = await CreateChainAsync("retype", "agency", "team");
        try
        {
            var team = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == ids[1]);
            team.ResourceTypeId = "project";
            await Context.SaveChangesAsync();
            await AssertClosureMatchesAsync(ids);
        }
        finally
        {
            await DeleteAsync(ids);
        }
    }

    [TestMethod]
    public async Task DeletingALeaf_RemovesOnlyItsPairs()
    {
        var ids = await CreateChainAsync("del", "agency", "team", "project");
        try
        {
            var before = await ActualAsync(ids);
            var rows = await ReadResourcesAsync();
            var project = rows.Single(r => r.Id == ids[2]);

            var entity = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == ids[2]);
            Context.Set<SqlOSFgaResource>().Remove(entity);
            await Context.SaveChangesAsync();

            var after = await ActualAsync(ids);
            after.Should().BeEquivalentTo(before.Where(p => p.DescendantSeq != project.Seq));
            await AssertClosureMatchesAsync(ids[..2]);
        }
        finally
        {
            await DeleteAsync(ids[..2]);
        }
    }

    [TestMethod]
    public async Task ARowDeeperThanTheConfiguredDepth_IsRejected()
    {
        // root has depth 0; a node with MaxDepth ancestors is allowed, one more is not.
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

            await AssertClosureMatchesAsync(ids.ToArray());

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
    public async Task Rebuild_ReproducesTheTriggerMaintainedClosure()
    {
        var ids = await CreateChainAsync("rebuild", "agency", "team", "project");
        var other = await CreateChainAsync("rebuild2", "agency", "team");
        try
        {
            var team = await Context.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == other[1]);
            team.IsActive = false;
            await Context.SaveChangesAsync();

            var maintained = await ReadClosureAsync();
            await Context.Database.ExecuteSqlRawAsync(TestDatabase.IsPostgreSql
                ? "SELECT \"dbo\".\"fn_SqlOSFgaResourcesClosure_Rebuild\"()"
                : "EXEC [dbo].[sp_SqlOSFgaResourcesClosure_Rebuild]");
            var rebuilt = await ReadClosureAsync();

            rebuilt.Should().BeEquivalentTo(maintained);
            rebuilt.Should().NotBeEmpty();
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
        await AssertClosureMatchesAsync(all);
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

    private static async Task AssertClosureMatchesAsync(string[] ids)
    {
        var expected = await ExpectedAsync(ids);
        var actual = await ActualAsync(ids);
        actual.Should().BeEquivalentTo(expected, "the closure must hold exactly the active ancestor pairs of {0}", string.Join(", ", ids));
    }

    /// <summary>Definition of the closure, computed from the resource table by walking up.</summary>
    private static async Task<HashSet<ClosureRow>> ExpectedAsync(string[] ids)
    {
        var rows = await ReadResourcesAsync();
        var byId = rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var typeSeq = await ReadTypeSeqAsync();
        var expected = new HashSet<ClosureRow>();
        foreach (var id in ids)
        {
            var descendant = byId[id];
            if (!descendant.IsActive)
            {
                continue;
            }

            var current = descendant.ParentId;
            var depth = 0;
            while (current != null && byId.TryGetValue(current, out var ancestor) && ancestor.IsActive && depth < MaxDepth)
            {
                expected.Add(new ClosureRow(ancestor.Seq, typeSeq[descendant.ResourceTypeId], descendant.Seq));
                current = ancestor.ParentId;
                depth++;
            }
        }

        return expected;
    }

    private static async Task<HashSet<ClosureRow>> ActualAsync(string[] ids)
    {
        var seqs = (await ReadResourcesAsync()).Where(r => ids.Contains(r.Id)).Select(r => r.Seq).ToHashSet();
        return (await ReadClosureAsync()).Where(c => seqs.Contains(c.DescendantSeq)).ToHashSet();
    }

    private static Task<List<ResourceRow>> ReadResourcesAsync()
        => ReadAsync(
            TestDatabase.Rewrite("SELECT [Id], [ParentId], [IsActive], [ResourceTypeId], [Seq] FROM [dbo].[SqlOSFgaResources]"),
            reader => new ResourceRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                reader.GetInt64(4)));

    private static async Task<Dictionary<string, int>> ReadTypeSeqAsync()
        => (await ReadAsync(
            TestDatabase.Rewrite("SELECT [Id], [Seq] FROM [dbo].[SqlOSFgaResourceTypes]"),
            reader => (reader.GetString(0), reader.GetInt32(1))))
            .ToDictionary(t => t.Item1, t => t.Item2, StringComparer.Ordinal);

    private static Task<List<ClosureRow>> ReadClosureAsync()
        => ReadAsync(
            TestDatabase.Rewrite("SELECT [AncestorSeq], [TypeSeq], [DescendantSeq] FROM [dbo].[SqlOSFgaResourcesClosure]"),
            reader => new ClosureRow(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2)));

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
