using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// One API: <c>BuildFilterAsync</c>, composed into the query the application writes anyway. On the context
/// SqlOS registers, a page over the filter (<c>Where(filter)</c>, an order, <c>Take</c>) is walked by SqlOS;
/// the same query without <c>Take</c>, or on a plain context, runs as EF Core would. These tests hold the two
/// to the same rows in the same order for every shape of access (one grant, several, direct row grants,
/// grants behind an inactive node, unusable grants, overlapping principals), through every page of a keyset
/// walk, ascending and descending, with filters and a non-key order; for every query shape a page can take
/// (Skip, First, Single, Select, sync); and as grants, rows and the tree change, with the grant counts and
/// direct index that make the walk cheap staying exact.
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

        foreach (var (name, subject) in users)
        {
            var filter = await fga.BuildFilterAsync<PageItem>(subject, Read);
            foreach (var shape in Shapes())
            {
                var expected = await Truth(db, filter, shape);
                var actual = await PageAll(db, filter, shape, walked: name != "nobody");
                actual.Should().Equal(expected, $"user {name}, shape {shape.Name}: the walked pages must return exactly what the filter returns");
            }
        }
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
        var filter = await fga.BuildFilterAsync<PageItem>(alice, Read);

        // Ground truth: the filter in a query that is not a page (no Take), which EF Core runs as written.
        SqlOSFgaPageDiagnostics.Collect();
        var truth = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).ToListAsync();
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull("a query without Take is not a page");
        truth.Should().HaveCountGreaterThan(20);
        var ids = truth.Select(i => i.Id).ToList();

        // Skip and Take: the walk fetches skip + take rows and drops the first skip.
        SqlOSFgaPageDiagnostics.Collect();
        var skipped = await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).Skip(5).Take(5).ToListAsync();
        SqlOSFgaPageDiagnostics.LastCounters.Should().NotBeNull();
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

        // Not pages: counts, no Take, an order no index covers, an order of mixed directions. They run as EF
        // Core would, the filter as a predicate, and return the same rows.
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.Where(filter).CountAsync()).Should().Be(truth.Count);
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull();
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Price).ThenBy(i => i.Id).Take(5).ToListAsync()).Select(i => i.Id)
            .Should().Equal(truth.OrderBy(i => i.Price).ThenBy(i => i.Id, StringComparer.Ordinal).Take(5).Select(i => i.Id));
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull("Price has no declared index, so the page is not walked");
        SqlOSFgaPageDiagnostics.Collect();
        (await db.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).ThenByDescending(i => i.Id).Take(5).ToListAsync()).Select(i => i.Id).Should().Equal(ids.Take(5));
        SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull("an order of mixed directions is not walked");

        // The same page on a plain context (no UseSqlOSFga) is the same rows, through the optimizer.
        var plainOptions = new DbContextOptionsBuilder<PageDbContext>();
        plainOptions.UseTestProvider(host.ConnectionString);
        await using (var plain = new PageDbContext(plainOptions.Options))
        {
            SqlOSFgaPageDiagnostics.Collect();
            (await plain.Items.AsNoTracking().Where(filter).OrderBy(i => i.Rank).Take(5).ToListAsync()).Select(i => i.Id).Should().Equal(ids.Take(5));
            SqlOSFgaPageDiagnostics.LastCounters.Should().BeNull();
        }

        // Nothing visible: the filter is false, the page empty.
        var bob = (await subjects.CreateUserAsync("Bob", $"bob-{Guid.NewGuid():N}@example.test")).SubjectId;
        var nothing = await fga.BuildFilterAsync<PageItem>(bob, Read);
        (await db.Items.AsNoTracking().Where(nothing).OrderBy(i => i.Rank).Take(5).ToListAsync()).Should().BeEmpty();
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
    private static async Task<List<string>> PageAll(PageDbContext db, Expression<Func<PageItem, bool>> filter, Shape shape, bool walked)
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
        await db.Database.ExecuteSqlRawAsync(provider.BuildPageIndexRebuildSql(new SqlOS.Fga.Configuration.SqlOSFgaOptions()));
        (await ReadAsync(db, counts)).Should().Equal(maintainedCounts, "the maintained grant counts must equal a rebuild");
        (await ReadAsync(db, direct)).Should().Equal(maintainedDirect, "the maintained direct index must equal a rebuild");
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
                db.Items.Add(new PageItem(ItemId(n), Folder(w, f)) { Rank = ranks[n], Status = n % 3, Price = (n * 37) % 100, TypeId = n % 11 == 5 ? OtherType : ItemType });
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
                item.HasIndex(i => i.Rank).HasDatabaseName("IX_PageItems_Rank");
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
