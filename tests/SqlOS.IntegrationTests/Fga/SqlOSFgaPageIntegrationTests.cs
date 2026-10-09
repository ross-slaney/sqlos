using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Paging;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// One API: <c>BuildFilterAsync</c>, composed into the query the application writes anyway. Building the
/// filter reads no grant. On the context SqlOS registers, a page over the filter (<c>Where(filter)</c>, an
/// order, <c>Take</c>) is walked by SqlOS, which reads no access root either; the same query without
/// <c>Take</c> runs as EF Core would, over the predicate whose roots are read when it runs. These tests hold
/// the two to the same rows in the same order for every shape of access (one grant, several, direct row
/// grants, grants behind an inactive node, unusable grants, overlapping principals), through every page of a
/// keyset walk, ascending and descending, with filters and a non-key order; for every query shape a page can
/// take (Skip, First, Single, Select, sync); and as grants, rows and the tree change, with the grant counts
/// and direct index that make the walk cheap staying exact.
/// </summary>
[TestClass]
public class SqlOSFgaPageIntegrationTests
{
    private const string FolderType = "page_folder";
    private const string ItemType = "page_item";
    private const string OtherType = "page_other";
    private const string Read = "PAGE_READ";
    private const string Other = "PAGE_OTHER";
    private const string ReaderRoleId = "role_page_reader";
    private const string OtherRoleId = "role_page_other";

    [TestMethod]
    public async Task BaseClass_LeavesApplicationColumnsEqualToTheEfModel_AndUpgradesTheEarlierLayout()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PageDbContext>();
        await Tree.CreateAsync(db);
        var store = StoreObjectIdentifier.Table("PageItems", null);
        var expected = db.Model.FindEntityType(typeof(PageItem))!.GetProperties()
            .Select(p => p.GetColumnName(store)!).Order(StringComparer.Ordinal).ToList();
        var columnsSql = TestDatabase.IsSqlServer
            ? "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'PageItems') ORDER BY name"
            : "SELECT column_name FROM information_schema.columns WHERE table_name = 'PageItems' ORDER BY column_name";
        (await ReadAsync(db, columnsSql)).Should().BeEquivalentTo(expected,
            "extending SqlOSResourceEntity must not cause undeclared columns to appear on the application table");

        await AssertScopeProjectionAsync(db);
        if (TestDatabase.IsSqlServer)
        {
            var comparison = await EFCore.SchemaSync.SchemaSync.ApplyAsync(db, new EFCore.SchemaSync.SchemaSyncOptions { DryRun = true });
            comparison.Changes.Should().BeEmpty("SqlOS's startup must not leave undeclared columns for the application's schema tool to remove");
        }

        // Reproduce the released v8 table shape, with application data already present: the per-level indexes on
        // the application table (on SQL Server over computed columns). Startup must remove them and build the
        // projection without an application migration.
        await db.Database.ExecuteSqlRawAsync(TestDatabase.IsSqlServer
            ? """
              ALTER TABLE [PageItems] ADD [FgaScopeType] AS SUBSTRING([FgaScope], 2, 4);
              ALTER TABLE [PageItems] ADD [FgaScope0] AS SUBSTRING([FgaScope], 6, 8);
              CREATE STATISTICS [ST_PageItems_FgaScopeType] ON [PageItems] ([FgaScopeType]);
              CREATE INDEX [IX_PageItems_FgaScope0] ON [PageItems] ([FgaScope0], [Id]) WHERE [FgaScope] >= 0x00;
              DROP TABLE [dbo].[SqlOSFgaScopeIndex_PageItems];
              """
            : """
              CREATE INDEX "IX_PageItems_FgaScope0" ON "PageItems" ((SUBSTRING("FgaScope", 6, 8)), "Id") WHERE "FgaScope" >= '\x00'::bytea;
              CREATE STATISTICS "ST_PageItems_FgaScopeType" ON ((SUBSTRING("FgaScope", 2, 4))) FROM "PageItems";
              DROP TABLE "dbo"."SqlOSFgaScopeIndex_PageItems";
              """);
        var initializer = new SqlOSFgaFunctionInitializer(db,
            Options.Create(new SqlOS.Fga.Configuration.SqlOSFgaOptions()), NullLogger<SqlOSFgaFunctionInitializer>.Instance);
        await initializer.EnsureFunctionsExistAsync();
        (await ReadAsync(db, columnsSql)).Should().BeEquivalentTo(expected);
        (await ReadAsync(db, TestDatabase.IsSqlServer
                ? "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(N'PageItems') AND name LIKE 'IX[_]PageItems[_]FgaScope%'"
                : "SELECT indexname FROM pg_indexes WHERE tablename = 'PageItems' AND indexname LIKE 'IX\\_PageItems\\_FgaScope%'"))
            .Should().Equal(["IX_PageItems_FgaScopeMissing"], "the application table keeps only the index on rows without a scope");
        await AssertScopeProjectionAsync(db);
        await initializer.EnsureFunctionsExistAsync();
        await AssertScopeProjectionAsync(db);
        if (TestDatabase.IsSqlServer)
        {
            (await EFCore.SchemaSync.SchemaSync.ApplyAsync(db)).Outcome.Should().Be(EFCore.SchemaSync.SchemaSyncOutcome.NoChangesNeeded);
        }

        // Raw/bulk statement changes use the same maintained projection, including primary-key changes.
        await db.Database.ExecuteSqlRawAsync(TestDatabase.IsSqlServer
            ? "UPDATE [PageItems] SET [Id] = N'renamed-' + [Id], [Rank] = [Rank] + 1 WHERE [Rank] < 3"
            : "UPDATE \"PageItems\" SET \"Id\" = 'renamed-' || \"Id\", \"Rank\" = \"Rank\" + 1 WHERE \"Rank\" < 3");
        await AssertScopeProjectionAsync(db);
        await db.Database.ExecuteSqlRawAsync(TestDatabase.IsSqlServer
            ? "DELETE FROM [PageItems] WHERE [Id] LIKE N'renamed-%'"
            : "DELETE FROM \"PageItems\" WHERE \"Id\" LIKE 'renamed-%'");
        await AssertScopeProjectionAsync(db);
        // SQL Server's equality ignores case/trailing spaces under the default collation; a projection must
        // nevertheless preserve exactly what the application stored, for keys as well as sort values.
        await db.Database.ExecuteSqlRawAsync(TestDatabase.IsSqlServer
            ? "UPDATE [PageItems] SET [Id] = UPPER([Id]), [Label] = N'ORIGINAL '"
            : "UPDATE \"PageItems\" SET \"Id\" = UPPER(\"Id\"), \"Label\" = 'ORIGINAL '");
        await AssertScopeProjectionAsync(db);
        if (TestDatabase.IsSqlServer)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE [PageItems] SET [Status] = [Status] + 1");
            await AssertScopeProjectionAsync(db); // the computed sort value changes without being in SET
            // A migration changes the computation without changing the column's name or SQL type.
            await db.Database.ExecuteSqlRawAsync("""
                DROP INDEX [IX_PageItems_ComputedStatus] ON [PageItems];
                ALTER TABLE [PageItems] DROP COLUMN [ComputedStatus];
                ALTER TABLE [PageItems] ADD [ComputedStatus] AS [Status] * 3 PERSISTED;
                CREATE INDEX [IX_PageItems_ComputedStatus] ON [PageItems] ([ComputedStatus]);
                """);
            await initializer.EnsureFunctionsExistAsync();
            await AssertScopeProjectionAsync(db);
        }
    }

    private static async Task AssertScopeProjectionAsync(PageDbContext db)
    {
        // Compare with the source rows, not with another query that uses the same authorization index.
        var (projection, source, copy, order) = TestDatabase.IsSqlServer
            ? ("Id, Label, Rank, Stamp, ComputedStatus, CONVERT(varchar(max), FgaScope, 2) AS ScopeBytes", "PageItems", "dbo.SqlOSFgaScopeIndex_PageItems", "ORDER BY Id")
            : ("\"Id\", \"Label\", \"Rank\", \"Stamp\", encode(\"FgaScope\", 'hex') AS \"ScopeBytes\"", "\"PageItems\"", "\"dbo\".\"SqlOSFgaScopeIndex_PageItems\"", "ORDER BY \"Id\"");
        (await ReadAsync(db, $"SELECT {projection} FROM {copy} {order}"))
            .Should().Equal(await ReadAsync(db, $"SELECT {projection} FROM {source} {order}"),
                "the projection must equal the application rows, including every scope byte");
    }

    [TestMethod]
    public async Task AuthorizedSetBasedWrites_UpdateApplicationRows_AndMaintainTheirProjection()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PageDbContext>();
        await Tree.CreateAsync(db);
        var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
        var alice = (await subjects.CreateUserAsync("Projection writer", $"writer-{Guid.NewGuid():N}@example.test")).SubjectId;
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "projection_writer", SubjectId = alice, ResourceId = Tree.Workspace(1), RoleId = ReaderRoleId });
        await db.SaveChangesAsync();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        var filter = await fga.BuildFilterAsync<PageItem>(alice, Read);
        // The fixture gives every eleventh item a different resource type, outside this permission.
        var perWorkspace = Tree.FoldersPerWorkspace * Tree.ItemsPerFolder;
        var expected = Enumerable.Range(perWorkspace, perWorkspace).Count(n => n % 11 != 5);

        (await db.Items.Where(filter).ExecuteUpdateAsync(set => set.SetProperty(i => i.Rank, i => i.Rank + 1000)))
            .Should().Be(expected);
        db.ChangeTracker.Clear();
        (await db.Items.CountAsync(i => i.Rank >= 1000)).Should().Be(expected,
            "ExecuteUpdate must target the application's table, not just its private index");
        await AssertScopeProjectionAsync(db);
        (await db.Items.Where(filter).ExecuteDeleteAsync()).Should().Be(expected);
        (await db.Items.CountAsync()).Should().Be(Tree.Items - expected);
        await AssertScopeProjectionAsync(db);
    }

    [TestMethod]
    public async Task EveryAccessShape_WalksTheRowsTheFilterReturns()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PageDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        await Tree.CreateAsync(db);

        var users = new Dictionary<string, string>();
        async Task<string> User(string name)
        {
            var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
            var user = await subjects.CreateUserAsync(name, $"{name}-{Guid.NewGuid():N}@example.test");
            users[name] = user.SubjectId;
            return user.SubjectId;
        }

        void Grant(string subject, string resource, string role = ReaderRoleId, DateTime? from = null, DateTime? to = null)
            => db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = $"g_{Guid.NewGuid():N}", SubjectId = subject, ResourceId = resource, RoleId = role, EffectiveFrom = from, EffectiveTo = to });

        Grant(await User("admin"), "root");
        Grant(await User("one"), Tree.Folder(2, 3));
        foreach (var (w, f) in new[] { (0, 1), (1, 4), (3, 2) }) { Grant(users.GetValueOrDefault("three") ?? await User("three"), Tree.Folder(w, f)); }
        var direct = await User("direct");
        foreach (var item in Enumerable.Range(0, Tree.Items).Where(i => i % 7 == 3)) { Grant(direct, Tree.ItemResource(item)); }
        Grant(await User("cut"), Tree.Folder(Tree.InactiveWorkspace, 1));
        var cutAdmin = await User("cutadmin");
        Grant(cutAdmin, "root");
        Grant(cutAdmin, Tree.Folder(Tree.InactiveWorkspace, 2));
        var mixed = await User("mixed");
        Grant(mixed, Tree.Folder(1, 0), to: DateTime.UtcNow.AddDays(-1));
        Grant(mixed, Tree.Folder(1, 1), from: DateTime.UtcNow.AddDays(1));
        Grant(mixed, Tree.Folder(1, 2), role: OtherRoleId);
        Grant(mixed, Tree.Folder(1, 3));
        var overlap = await User("overlap");
        Grant(overlap, Tree.Folder(2, 1));
        Grant(overlap, Tree.Workspace(2));
        var folderAndItems = await User("folderanditems");
        Grant(folderAndItems, Tree.Folder(0, 0));
        foreach (var item in Tree.ItemsOf(0, 0).Take(2)) { Grant(folderAndItems, Tree.ItemResource(item)); }
        await User("nobody");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var held = 0;
        foreach (var (name, subject) in users)
        {
            var filter = await fga.BuildFilterAsync<PageItem>(subject, Read);
            foreach (var shape in Shapes())
            {
                var expected = await Truth(db, filter, shape);
                var actual = await PageAll(db, filter, shape, walked: name != "nobody", counters => held += counters.RowsRetained);
                actual.Should().Equal(expected, $"user {name}, shape {shape.Name}: the walked pages must return exactly what the filter returns");
            }
        }

        held.Should().BePositive("pages merging several streams hold rows between rounds, and those rows went through the database's merge in every type the orders use");
    }

    [TestMethod]
    public async Task Maintenance_KeepsTheCountsAndTheDirectIndexExact()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PageDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        await Tree.CreateAsync(db);
        var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
        var alice = (await subjects.CreateUserAsync("Alice", $"alice-{Guid.NewGuid():N}@example.test")).SubjectId;
        var bob = (await subjects.CreateUserAsync("Bob", $"bob-{Guid.NewGuid():N}@example.test")).SubjectId;

        async Task Check(string because)
        {
            foreach (var subject in new[] { alice, bob })
            {
                // The filter is built per request, as an application builds it: it carries the grants of now.
                var filter = await fga.BuildFilterAsync<PageItem>(subject, Read);
                foreach (var shape in Shapes())
                {
                    var expected = await Truth(db, filter, shape);
                    (await PageAll(db, filter, shape, walked: expected.Count > 0)).Should().Equal(expected, $"{because}; shape {shape.Name}");
                }
            }

            await AssertIndexMatchesRebuildAsync(db);
        }

        // Grants come and go.
        var grantA = new SqlOSFgaGrant { Id = "m_a", SubjectId = alice, ResourceId = Tree.Folder(0, 1), RoleId = ReaderRoleId };
        db.Set<SqlOSFgaGrant>().Add(grantA);
        await db.SaveChangesAsync();
        await Check("after a folder grant");
        var item = Tree.ItemsOf(2, 2).First();
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "m_b", SubjectId = bob, ResourceId = Tree.ItemResource(item), RoleId = ReaderRoleId });
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "m_b2", SubjectId = bob, ResourceId = Tree.Workspace(3), RoleId = ReaderRoleId });
        await db.SaveChangesAsync();
        await Check("after a direct and a workspace grant");

        // A directly granted row's resource gains a child and becomes a container (its grant enters the counts,
        // where only grants on resources with children live), then loses it again.
        var child = new SqlOSFgaResource { Id = "m_child", ParentId = Tree.ItemResource(item), Name = "Child of a granted item", ResourceTypeId = OtherType };
        db.Set<SqlOSFgaResource>().Add(child);
        await db.SaveChangesAsync();
        await Check("after a granted item became a container");
        db.Set<SqlOSFgaResource>().Remove(child);
        await db.SaveChangesAsync();
        await Check("after the granted item lost its only child");

        // A grant changes hands and window.
        grantA.ResourceId = Tree.Folder(0, 2);
        grantA.EffectiveTo = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        await Check("after a grant moved and expired");
        grantA.EffectiveTo = null;
        await db.SaveChangesAsync();
        await Check("after the grant's window reopened");

        // Rows change: a sort key, a parent, a deactivation, a delete.
        var row = await db.Items.SingleAsync(i => i.Id == Tree.ItemId(item));
        row.Rank = -5;
        await db.SaveChangesAsync();
        await Check("after a directly granted row's sort key changed");
        var moved = await db.Items.SingleAsync(i => i.Id == Tree.ItemId(Tree.ItemsOf(0, 2).First()));
        moved.FolderResourceId = Tree.Folder(3, 0);
        await db.SaveChangesAsync();
        await Check("after a row moved to another folder");
        var folder = await db.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == Tree.Folder(0, 2));
        folder.IsActive = false;
        await db.SaveChangesAsync();
        await Check("after the granted folder was deactivated");
        folder.IsActive = true;
        await db.SaveChangesAsync();
        await Check("after it was reactivated");
        db.Items.Remove(row);
        await db.SaveChangesAsync();
        await Check("after a directly granted row was deleted");
        db.Set<SqlOSFgaGrant>().Remove(grantA);
        await db.SaveChangesAsync();
        await Check("after the folder grant was revoked");

        // A whole folder of rows moves under the other user's workspace.
        var folderRow = await db.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == Tree.Folder(0, 1));
        folderRow.ParentId = Tree.Workspace(3);
        await db.SaveChangesAsync();
        await Check("after a folder moved into a granted workspace");
    }

    [TestMethod]
    public async Task EveryQueryShape_OnePageOrPlain_ReturnsWhatEfCoreWould()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PageDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        await Tree.CreateAsync(db);
        var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
        var alice = (await subjects.CreateUserAsync("Alice", $"alice-{Guid.NewGuid():N}@example.test")).SubjectId;
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "c_a", SubjectId = alice, ResourceId = Tree.Workspace(1), RoleId = ReaderRoleId });
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "c_b", SubjectId = alice, ResourceId = Tree.Folder(2, 4), RoleId = ReaderRoleId });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Building the filter resolves the caller's principals and the permission, and reads no grant.
        SqlOSFgaPageDiagnostics.Collect();
        var filter = await fga.BuildFilterAsync<PageItem>(alice, Read);
        SqlOSFgaPageDiagnostics.RootsResolved.Should().BeNull("BuildFilterAsync reads no access root");

        // Ground truth: the filter in a query that is not a page (no Take), which EF Core runs as written,
        // over the predicate whose roots are read when the query runs.
        SqlOSFgaPageDiagnostics.Collect();
        var truth = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).ToListAsync();
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull("a query without Take is not a page");
        SqlOSFgaPageDiagnostics.RootsResolved.Should().Be(2, "Alice's two grants are the roots of the planned statement, read when it ran");
        truth.Should().HaveCountGreaterThan(20);
        var ids = truth.Select(i => i.Id).ToList();

        // Skip and Take: the walk fetches skip + take rows and drops the first skip, and reads no root.
        SqlOSFgaPageDiagnostics.Collect();
        var skipped = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).Skip(5).Take(5).ToListAsync();
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
        SqlOSFgaPageDiagnostics.RootsResolved.Should().BeNull("a walked page reads no access root");
        skipped.Select(i => i.Id).Should().Equal(ids.Skip(5).Take(5));

        // Single-row methods, with their own semantics.
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).FirstAsync()).Id.Should().Be(ids[0]);
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
        (await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).FirstOrDefaultAsync(i => i.Status == 2))!.Id.Should().Be(truth.First(i => i.Status == 2).Id);
        var one = truth[3];
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.AsNoTracking().Where(filter).Where(i => i.Id == one.Id).OrderBy(i => i.Rank).SingleAsync()).Id.Should().Be(one.Id);
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
        (await db.Items.AsNoTracking().Where(filter).Where(i => i.Id == "no such item").OrderBy(i => i.Rank).FirstOrDefaultAsync()).Should().BeNull();
        var several = () => db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).SingleAsync();
        await several.Should().ThrowAsync<InvalidOperationException>();

        // A projection on the way out, before or after Take.
        SqlOSFgaPageDiagnostics.Collect();
        var projected = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).Take(4).Select(i => new { i.Id, Doubled = i.Rank * 2 }).ToListAsync();
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
        projected.Select(p => (p.Id, p.Doubled)).Should().Equal(truth.Take(4).Select(i => (i.Id, i.Rank * 2)));
        SqlOSFgaPageDiagnostics.Collect();
        var scalars = await db.Items.Where(filter).OrderBy(i => i.Rank).Select(i => i.Id).Take(4).ToListAsync();
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
        scalars.Should().Equal(ids.Take(4));

        // Synchronous execution, and the filter composed after the order.
        SqlOSFgaPageDiagnostics.Collect();
        db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).Take(3).ToList().Select(i => i.Id).Should().Equal(ids.Take(3));
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.AsNoTracking().OrderBy(i => i.Rank).Where(filter).Take(3).ToListAsync()).Select(i => i.Id).Should().Equal(ids.Take(3));
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();

        // The rows load through the application's query: tracking is honored either way.
        db.ChangeTracker.Clear();
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.Where(filter).OrderBy(i => i.Rank).Take(3).ToListAsync()).Should().HaveCount(3);
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
        db.ChangeTracker.Entries<PageItem>().Should().HaveCount(3);
        db.ChangeTracker.Clear();
        (await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).Take(3).ToListAsync()).Should().HaveCount(3);
        db.ChangeTracker.Entries().Should().BeEmpty();

        // Not pages: counts, no Take, an order no index covers, an order of mixed directions, an order over a
        // projection. They run as EF Core would, the filter as a predicate over the roots read then, and return
        // the same rows.
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.Where(filter).CountAsync()).Should().Be(truth.Count);
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull();
        SqlOSFgaPageDiagnostics.RootsResolved.Should().Be(2);
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.AsNoTracking().Where(filter).Select(i => new { i.Id, i.Rank }).OrderBy(r => r.Rank).Take(5).ToListAsync()).Select(r => r.Id).Should().Equal(ids.Take(5));
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull("an order over a projection is not a page");
        SqlOSFgaPageDiagnostics.RootsResolved.Should().Be(2);
        SqlOSFgaPageDiagnostics.Collect();
        db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).Take(5).ToQueryString().Should().Contain("FgaScope", "the query string is the planned statement's");
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull();
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Price).ThenBy(i => i.Id).Take(5).ToListAsync()).Select(i => i.Id)
            .Should().Equal(truth.OrderBy(i => i.Price).ThenBy(i => i.Id, StringComparer.Ordinal).Take(5).Select(i => i.Id));
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull("Price has no declared index, so the page is not walked");
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).ThenByDescending(i => i.Id).Take(5).ToListAsync()).Select(i => i.Id).Should().Equal(ids.Take(5));
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull("an order of mixed directions is not walked");

        // A context without SqlOS's query execution cannot evaluate the filter, and says so when it is built.
        var plainOptions = new DbContextOptionsBuilder<PageDbContext>();
        if (TestDatabase.IsPostgreSql)
        {
            plainOptions.UseNpgsql(host.ConnectionString);
        }
        else
        {
            plainOptions.UseSqlServer(host.ConnectionString);
        }

        await using (var plain = new PageDbContext(plainOptions.Options))
        {
            var elsewhere = new SqlOSFgaAuthService(plain, Microsoft.Extensions.Options.Options.Create(new SqlOS.Fga.Configuration.SqlOSFgaOptions()), NullLogger<SqlOSFgaAuthService>.Instance);
            var build = () => elsewhere.BuildFilterAsync<PageItem>(alice, Read);
            (await build.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("UseSqlOSFga");
        }

        // Nothing visible: the filter is false, the page empty.
        var bob = (await subjects.CreateUserAsync("Bob", $"bob-{Guid.NewGuid():N}@example.test")).SubjectId;
        var nothing = await fga.BuildFilterAsync<PageItem>(bob, Read);
        (await db.Items.AsNoTracking().Where(nothing).OrderBy(i => i.Rank).Take(5).ToListAsync()).Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnOrderThatContinuesPastTheKey_ReturnsOnlyWhatTheCallerMaySee()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PageDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        await Tree.CreateAsync(db);
        var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
        var one = (await subjects.CreateUserAsync("One", $"one-{Guid.NewGuid():N}@example.test")).SubjectId;
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "k_one", SubjectId = one, ResourceId = Tree.ItemResource(0), RoleId = ReaderRoleId });
        await db.SaveChangesAsync();

        // A second row shares the first's rank: a page that took the rank for the key would load it too.
        var rows = await db.Items.Where(i => i.Id == Tree.ItemId(0) || i.Id == Tree.ItemId(1)).OrderBy(i => i.Id).ToListAsync();
        rows[1].Rank = rows[0].Rank;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var filter = await fga.BuildFilterAsync<PageItem>(one, Read);
        (await fga.Allows(one, Read, Tree.ItemResource(1))).Should().BeFalse("the caller may see one row");

        // The order ends at the key as far as the rows are concerned (the key is unique), so this is the key
        // order, walked; the page must carry the key, not the column that follows it.
        SqlOSFgaPageDiagnostics.Collect();
        var page = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Id).ThenBy(i => i.Rank).Take(2).ToListAsync();
        page.Select(i => i.Id).Should().Equal(Tree.ItemId(0));
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();

        SqlOSFgaPageDiagnostics.Collect();
        var first = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Id).ThenBy(i => i.Rank).FirstOrDefaultAsync();
        first!.Id.Should().Be(Tree.ItemId(0));
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
    }

    [TestMethod]
    public async Task AConditionAfterThePage_RunsAsEfCoreWould()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PageDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        await Tree.CreateAsync(db);
        var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
        var alice = (await subjects.CreateUserAsync("Alice", $"alice-{Guid.NewGuid():N}@example.test")).SubjectId;
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "p_a", SubjectId = alice, ResourceId = "root", RoleId = ReaderRoleId });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var filter = await fga.BuildFilterAsync<PageItem>(alice, Read);
        var truth = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).ToListAsync();
        var second = truth[1];

        // A condition after Take applies to the page's rows: the one row is not the second, so nothing.
        SqlOSFgaPageDiagnostics.Collect();
        var after = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).Take(1).Where(i => i.Id == second.Id).ToListAsync();
        after.Should().BeEmpty("the condition applies to the page's one row, which is the first, not the second");
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull("a query with a condition after its page is not a page SqlOS walks");

        // An order after Take re-sorts the page's rows: the first row by rank, however it is then sorted.
        SqlOSFgaPageDiagnostics.Collect();
        var resorted = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).Take(3).OrderBy(i => i.Id).ToListAsync();
        resorted.Select(i => i.Id).Should().BeEquivalentTo(truth.Take(3).Select(i => i.Id));
        resorted.Select(i => i.Id).Should().BeInAscendingOrder(StringComparer.Ordinal);
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull();

        // The same condition before the page is the page's own filter, and the page is walked.
        SqlOSFgaPageDiagnostics.Collect();
        var before = await db.Items.AsNoTracking().Where(filter).Where(i => i.Id == second.Id).OrderBy(i => i.Rank).Take(1).ToListAsync();
        before.Select(i => i.Id).Should().Equal(second.Id);
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
    }

    [TestMethod]
    public async Task AGrantWhoseWindowOpened_IsWalkedBeforeTheCountsAreRefreshed()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PageDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        await Tree.CreateAsync(db);
        var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
        var later = (await subjects.CreateUserAsync("Later", $"later-{Guid.NewGuid():N}@example.test")).SubjectId;
        var opensAt = DateTime.UtcNow.AddSeconds(4);
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "w_later", SubjectId = later, ResourceId = Tree.Folder(0, 1), RoleId = ReaderRoleId, EffectiveFrom = opensAt });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var item = Tree.ItemsOf(0, 1).First();
        (await fga.Allows(later, Read, Tree.ItemResource(item))).Should().BeFalse("the grant's window has not opened");
        var closed = await fga.BuildFilterAsync<PageItem>(later, Read);
        (await db.Items.AsNoTracking().Where(closed).OrderBy(i => i.Rank).Take(10).ToListAsync()).Should().BeEmpty();

        // The trigger counted nothing (the grant is not usable yet) but recorded when the counts fall behind
        // the clock: the moment the window opens, on the principal's root row.
        var validUntil = TestDatabase.IsPostgreSql
            ? $"SELECT \"ValidUntil\" FROM \"dbo\".\"SqlOSFgaGrantCounts\" WHERE \"SubjectId\" = '{later}' AND \"ResourceSeq\" = (SELECT \"Seq\" FROM \"dbo\".\"SqlOSFgaResources\" WHERE \"Id\" = 'root')"
            : $"SELECT ValidUntil FROM dbo.SqlOSFgaGrantCounts WHERE SubjectId = '{later}' AND ResourceSeq = (SELECT Seq FROM dbo.SqlOSFgaResources WHERE Id = 'root')";
        (await ReadAsync(db, validUntil)).Should().ContainSingle().Which.Should().NotBe("null");

        // The window opens as the clock moves, which no trigger sees; the hosted refresh is asleep (it woke at
        // start, when no window was pending, and sleeps an hour). The point check and a planned statement read
        // the grant itself and see the rows; so must the page, rebuilding the caller's counts first.
        var untilOpen = opensAt.AddSeconds(1) - DateTime.UtcNow;
        if (untilOpen > TimeSpan.Zero)
        {
            // (A slow engine may already be past the window: then there is nothing to wait for.)
            await Task.Delay(untilOpen);
        }

        (await fga.Allows(later, Read, Tree.ItemResource(item))).Should().BeTrue();
        var filter = await fga.BuildFilterAsync<PageItem>(later, Read);
        (await db.Items.Where(filter).CountAsync()).Should().Be(Tree.ItemsPerFolder);
        SqlOSFgaPageDiagnostics.Collect();
        var page = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).Take(10).ToListAsync();
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
        SqlOSFgaPageDiagnostics.LastCounters!.CountsRebuilt.Should().Be(1, "the caller's counts had fallen behind the clock");
        page.Select(i => i.Id).Should().BeEquivalentTo(Tree.ItemsOf(0, 1).Select(Tree.ItemId), "the page must see a grant whose window opened before the counts were refreshed");

        // Having rebuilt them, the next page finds nothing to rebuild, and the counts equal a rebuild from scratch.
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).Take(10).ToListAsync()).Should().HaveCount(Tree.ItemsPerFolder);
        SqlOSFgaPageDiagnostics.LastCounters!.CountsRebuilt.Should().Be(0);
        await AssertIndexMatchesRebuildAsync(db);
    }

    [TestMethod]
    public async Task ATimestampOrder_InASessionWhoseTimeZoneIsNotUtc_KeepsEveryInstant()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PageDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        await Tree.CreateAsync(db);

        // The two earliest rows: one under an active folder, in the root's reach; one under a folder of the
        // inactive workspace, a cut the walk opens in a later round. The root's row is held meanwhile.
        var early = Tree.ItemsOf(0, 0).First();
        var cut = Tree.ItemsOf(Tree.InactiveWorkspace, 2).First();
        var items = await db.Items.Where(i => i.Id == Tree.ItemId(early) || i.Id == Tree.ItemId(cut)).ToListAsync();
        items.Single(i => i.Id == Tree.ItemId(early)).Stamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        items.Single(i => i.Id == Tree.ItemId(cut)).Stamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
        var zoned = (await subjects.CreateUserAsync("Zoned", $"zoned-{Guid.NewGuid():N}@example.test")).SubjectId;
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "z_root", SubjectId = zoned, ResourceId = "root", RoleId = ReaderRoleId });
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "z_cut", SubjectId = zoned, ResourceId = Tree.Folder(Tree.InactiveWorkspace, 2), RoleId = ReaderRoleId });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        if (TestDatabase.IsPostgreSql)
        {
            // A session whose time zone is not UTC reads a timestamp that names no zone in that zone: twelve
            // hours behind UTC here (POSIX sign), far from any machine's own zone. The connection stays open,
            // so every statement of the page runs in this session.
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'Etc/GMT+12'");
            (await ReadAsync(db, "SHOW TIME ZONE")).Should().Equal(["Etc/GMT+12"]);
            (await ReadAsync(db, "SELECT '2026-01-01T10:00:00'::timestamptz AT TIME ZONE 'UTC'")).Should().Equal(["01/01/2026 22:00:00"], "a literal without a zone is read in the session's zone");
        }

        var filter = await fga.BuildFilterAsync<PageItem>(zoned, Read);
        var truth = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Stamp).ThenBy(i => i.Id).Select(i => i.Id).ToListAsync();
        truth.Take(2).Should().Equal(Tree.ItemId(early), Tree.ItemId(cut));

        SqlOSFgaPageDiagnostics.Collect();
        var first = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Stamp).ThenBy(i => i.Id).Take(1).ToListAsync();
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
        SqlOSFgaPageDiagnostics.LastCounters!.RowsRetained.Should().BePositive("the root's row is held while the cut below the root is opened");
        first.Select(i => i.Id).Should().Equal([Tree.ItemId(early)], $"a held timestamp must keep its instant ({SqlOSFgaPageDiagnostics.LastCounters})");

        // A longer page refills the root's stream from the position it was fetched to, a timestamp as well; and
        // the next page starts from a timestamp the application read back.
        var page = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Stamp).ThenBy(i => i.Id).Take(5).ToListAsync();
        page.Select(i => i.Id).Should().Equal(truth.Take(5), "a refill's position must keep its instant");
        var last = page[^1];
        var next = await db.Items.AsNoTracking().Where(filter)
            .Where(i => i.Stamp > last.Stamp || (i.Stamp == last.Stamp && i.Id.CompareTo(last.Id) > 0))
            .OrderBy(i => i.Stamp).ThenBy(i => i.Id).Take(5).ToListAsync();
        next.Select(i => i.Id).Should().Equal(truth.Skip(5).Take(5), "a cursor's instant must be read as the instant it is");
    }

    /// <summary>A query shape every scenario is checked in: the application's own filter, its order, and the keyset predicate it writes for the next page.</summary>
    private sealed record Shape(
        string Name,
        int PageSize,
        Func<IQueryable<PageItem>, IQueryable<PageItem>> Filter,
        Func<IQueryable<PageItem>, IOrderedQueryable<PageItem>> Order,
        Func<IQueryable<PageItem>, PageItem, IQueryable<PageItem>> After);

    private static IEnumerable<Shape> Shapes()
    {
        yield return new("key order", 7, q => q, q => q.OrderBy(i => i.Id), (q, l) => q.Where(i => string.Compare(i.Id, l.Id) > 0));
        yield return new("rank order, key implied", 5, q => q, q => q.OrderBy(i => i.Rank), (q, l) => q.Where(i => i.Rank > l.Rank || (i.Rank == l.Rank && string.Compare(i.Id, l.Id) > 0)));
        yield return new("rank order, served filter", 4, q => q.Where(i => i.Status == 1), q => q.OrderBy(i => i.Rank).ThenBy(i => i.Id), (q, l) => q.Where(i => i.Rank > l.Rank || (i.Rank == l.Rank && i.Id.CompareTo(l.Id) > 0)));
        yield return new("rank order, residual filter", 6, q => q.Where(i => i.Price > 60), q => q.OrderBy(i => i.Rank).ThenBy(i => i.Id), (q, l) => q.Where(i => i.Rank > l.Rank || (i.Rank == l.Rank && string.Compare(i.Id, l.Id) > 0)));
        yield return new("key order, both filters", 3, q => q.Where(i => i.Status == 2 && i.Price < 80), q => q.OrderBy(i => i.Id), (q, l) => q.Where(i => string.Compare(i.Id, l.Id) > 0));
        yield return new("rank descending", 5, q => q, q => q.OrderByDescending(i => i.Rank).ThenByDescending(i => i.Id), (q, l) => q.Where(i => i.Rank < l.Rank || (i.Rank == l.Rank && string.Compare(i.Id, l.Id) < 0)));
    }

    /// <summary>Ground truth: the same filter and order in a query that is not a page (no Take), run by EF Core as written.</summary>
    private static async Task<List<string>> Truth(PageDbContext db, Expression<Func<PageItem, bool>> filter, Shape shape)
    {
        SqlOSFgaPageDiagnostics.Collect();
        var ids = await shape.Order(shape.Filter(db.Items.AsNoTracking().Where(filter))).Select(i => i.Id).ToListAsync();
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull("a query without Take runs as EF Core would");
        return ids;
    }

    /// <summary>Every page of a keyset walk, concatenated: k + 1 rows asked for, the last one telling whether a next page exists.</summary>
    private static async Task<List<string>> PageAll(PageDbContext db, Expression<Func<PageItem, bool>> filter, Shape shape, bool walked, Action<SqlOSFgaPageCounters>? observe = null)
    {
        var all = new List<string>();
        PageItem? last = null;
        for (var pages = 0; pages < 1_000; pages++)
        {
            var rows = db.Items.AsNoTracking().Where(filter);
            if (last is not null)
            {
                rows = shape.After(rows, last);
            }

            SqlOSFgaPageDiagnostics.Collect();
            var page = await shape.Order(shape.Filter(rows)).Take(shape.PageSize + 1).ToListAsync();
            if (walked)
            {
                SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull($"shape {shape.Name} must run through SqlOS's walk");
                SqlOSFgaPageDiagnostics.RootsResolved.Should().BeNull($"shape {shape.Name}: a walked page reads no access root");
                observe?.Invoke(SqlOSFgaPageDiagnostics.LastCounters!);
            }

            var more = page.Count > shape.PageSize;
            if (more)
            {
                page.RemoveAt(shape.PageSize);
            }

            all.AddRange(page.Select(i => i.Id));
            if (!more)
            {
                return all;
            }

            last = page[^1];
        }

        throw new InvalidOperationException("The keyset walk did not end.");
    }

    /// <summary>The maintained grant counts and direct index equal a rebuild from scratch.</summary>
    private static async Task AssertIndexMatchesRebuildAsync(PageDbContext db)
    {
        var provider = Database.SqlOSDatabase.Resolve(db.Database);
        var counts = provider.Kind == Database.SqlOSDatabaseProviderKind.PostgreSql
            ? "SELECT \"SubjectId\", \"ResourceSeq\", \"ParentSeq\", \"Grants\", \"GrantChildren\", \"CutGrants\" FROM \"dbo\".\"SqlOSFgaGrantCounts\" ORDER BY 1, 2"
            : "SELECT SubjectId, ResourceSeq, ParentSeq, Grants, GrantChildren, CutGrants FROM dbo.SqlOSFgaGrantCounts ORDER BY 1, 2";
        var direct = provider.Kind == Database.SqlOSDatabaseProviderKind.PostgreSql
            ? "SELECT \"GrantId\", \"SubjectId\", \"RoleId\", \"TypeSeq\", \"Active\", \"EffectiveFrom\", \"EffectiveTo\", \"Id\", \"Rank\" FROM \"dbo\".\"SqlOSFgaDirect_PageItems\" ORDER BY 1, 8"
            : "SELECT GrantId, SubjectId, RoleId, TypeSeq, Active, EffectiveFrom, EffectiveTo, Id, Rank FROM dbo.SqlOSFgaDirect_PageItems ORDER BY 1, 8";
        var maintainedCounts = await ReadAsync(db, counts);
        var maintainedDirect = await ReadAsync(db, direct);
        await AssertScopeProjectionAsync(db);
        await db.Database.ExecuteSqlRawAsync(provider.BuildPageIndexRebuildSql(new SqlOS.Fga.Configuration.SqlOSFgaOptions()));
        (await ReadAsync(db, counts)).Should().Equal(maintainedCounts, "the maintained grant counts must equal a rebuild");
        (await ReadAsync(db, direct)).Should().Equal(maintainedDirect, "the maintained direct index must equal a rebuild");
        await AssertScopeProjectionAsync(db);
    }

    private static async Task<List<string>> ReadAsync(DbContext db, string sql)
    {
        var rows = new List<string>();
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(string.Join("|", values.Select(v => v is DBNull ? "null" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))));
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }

        return rows;
    }

    /// <summary>
    /// The tree: root → 4 workspaces (one inactive) → 5 folders each → 6 items each (120 items), every item its
    /// own resource. Items carry a shuffled Rank, a Status in 0..2 and a Price in 0..99.
    /// </summary>
    private sealed class Tree
    {
        public const int Workspaces = 4;
        public const int FoldersPerWorkspace = 5;
        public const int ItemsPerFolder = 6;
        public const int Items = Workspaces * FoldersPerWorkspace * ItemsPerFolder;
        public const int InactiveWorkspace = 3;

        /// <summary>Every item's stamp is this plus its number in hours; a test moves a few to earlier instants.</summary>
        public static readonly DateTime Epoch = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static string Workspace(int w) => $"pw{w}";
        public static string Folder(int w, int f) => $"pf{w}_{f}";
        public static string ItemId(int n) => $"item{n:D3}";
        public static string ItemResource(int n) => "pi::" + ItemId(n);
        public static IEnumerable<int> ItemsOf(int w, int f) => Enumerable.Range((w * FoldersPerWorkspace + f) * ItemsPerFolder, ItemsPerFolder);

        public static async Task<Tree> CreateAsync(PageDbContext db)
        {
            var random = new Random(5);
            var ranks = Enumerable.Range(0, Items).OrderBy(_ => random.Next()).ToArray();
            for (var w = 0; w < Workspaces; w++)
            {
                db.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource { Id = Workspace(w), ParentId = "root", Name = Workspace(w), ResourceTypeId = FolderType, IsActive = w != InactiveWorkspace });
            }

            await db.SaveChangesAsync();
            for (var w = 0; w < Workspaces; w++)
            {
                for (var f = 0; f < FoldersPerWorkspace; f++)
                {
                    db.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource { Id = Folder(w, f), ParentId = Workspace(w), Name = Folder(w, f), ResourceTypeId = FolderType });
                }
            }

            await db.SaveChangesAsync();
            for (var n = 0; n < Items; n++)
            {
                var w = n / (FoldersPerWorkspace * ItemsPerFolder);
                var f = n / ItemsPerFolder % FoldersPerWorkspace;
                db.Items.Add(new PageItem(ItemId(n), Folder(w, f)) { Rank = ranks[n], Status = n % 3, Price = (n * 37) % 100, Stamp = Epoch.AddHours(n), TypeId = n % 11 == 5 ? OtherType : ItemType });
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Tree();
        }
    }

    public sealed class PageItem : SqlOSResourceEntity
    {
        private PageItem()
        {
        }

        public PageItem(string id, string folderResourceId)
        {
            Id = id;
            ResourceId = "pi::" + id;
            FolderResourceId = folderResourceId;
        }

        public string Id { get; private set; } = string.Empty;
        public string FolderResourceId { get; set; } = string.Empty;
        public int Rank { get; set; }
        public int Status { get; set; }
        public int Price { get; set; }
        public string Label { get; set; } = "Original";

        /// <summary>An instant (UTC), the declared order of the timestamp page: <c>timestamp with time zone</c> on PostgreSQL, <c>datetime2</c> on SQL Server.</summary>
        public DateTime Stamp { get; set; } = Tree.Epoch;
        public string TypeId { get; set; } = ItemType;
        public bool Active { get; set; } = true;

        public override string ResourceTypeId => TypeId;
        public override string ResourceName => Id;
        public override string? ParentResourceId => FolderResourceId;
        public override bool ResourceIsActive => Active;
    }

    public sealed class PageDbContext(DbContextOptions<PageDbContext> options) : SqlOSDbContext<PageDbContext>(options)
    {
        public DbSet<PageItem> Items => Set<PageItem>();

        protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PageItem>(item =>
            {
                item.ToTable("PageItems");
                item.HasKey(i => i.Id);
                item.Property(i => i.Id).HasMaxLength(64);
                item.Property(i => i.ResourceId).HasMaxLength(128);
                item.Property(i => i.FolderResourceId).HasMaxLength(128);
                item.Property(i => i.TypeId).HasMaxLength(64);
                item.Property(i => i.Label).HasMaxLength(80);
                item.HasIndex(i => i.Label);
                item.HasIndex(i => i.Rank).HasDatabaseName("IX_PageItems_Rank");
                item.HasIndex(i => i.Stamp).HasDatabaseName("IX_PageItems_Stamp");
                if (TestDatabase.IsSqlServer)
                {
                    item.Property<int>("ComputedStatus").HasComputedColumnSql("[Status] * 2", stored: true);
                    item.HasIndex("ComputedStatus");
                }
                if (TestDatabase.IsPostgreSql)
                {
                    // An instant column. (Under the library's timestamp compatibility switch, Npgsql maps DateTime
                    // to a plain timestamp by default and reads this type back as local time.)
                    item.Property(i => i.Stamp).HasColumnType("timestamp with time zone");
                }

                // An order that continues past the key: the key is unique, so the rest of it never matters.
                item.HasIndex(i => new { i.Id, i.Rank }).HasDatabaseName("IX_PageItems_Id_Rank");
            });
        }
    }

    private sealed class HostedApp : IAsyncDisposable
    {
        private readonly string _database;

        private HostedApp(WebApplication app, string database, string connectionString)
        {
            App = app;
            _database = database;
            ConnectionString = connectionString;
        }

        public WebApplication App { get; }

        /// <summary>The test database's connection string, for a context built by hand beside the hosted one.</summary>
        public string ConnectionString { get; }

        public static async Task<HostedApp> StartAsync()
        {
            var database = "FgaPage_" + Guid.NewGuid().ToString("N");
            await TestDatabase.CreateDatabaseAsync(AspireFixture.SqlConnectionString, database);
            var connectionString = TestDatabase.CreateIsolatedConnectionString(AspireFixture.SqlConnectionString, database);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.AddSqlOS<PageDbContext>(
                db => db.UseTestProvider(connectionString),
                options =>
                {
                    options.AuthServer.PublicOrigin = "http://localhost";
                    options.AuthServer.Issuer = "http://localhost/sqlos/auth";
                    options.Fga.Seed(seed => seed
                        .ResourceType(FolderType, "Folder")
                        .ResourceType(ItemType, "Item")
                        .ResourceType(OtherType, "Other")
                        .Permission(Read, "Read items", ItemType)
                        .Permission(Other, "Other permission", OtherType)
                        .Role(ReaderRoleId, "page_reader", "Reader")
                        .Role(OtherRoleId, "page_other", "Other")
                        .RolePermission("page_reader", Read)
                        .RolePermission("page_other", Other));
                });
            var app = builder.Build();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<PageDbContext>().Database.EnsureCreatedAsync();
            }

            await app.StartAsync();
            return new HostedApp(app, database, connectionString);
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            TestDatabase.ClearPools();
            await TestDatabase.DropDatabaseAsync(AspireFixture.SqlConnectionString, _database);
        }
    }
}
