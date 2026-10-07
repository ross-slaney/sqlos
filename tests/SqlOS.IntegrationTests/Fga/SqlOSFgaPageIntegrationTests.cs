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
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Pagination;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// <c>PageAsync</c> on the engine under test, against <c>BuildFilterAsync</c> as ground truth: the same rows in
/// the same order for every shape of access (one grant, several, direct row grants, grants behind an inactive
/// node, unusable grants, overlapping principals), through every page of a cursor walk, with filters and a
/// non-key order; and the grant counts and direct index that make it cheap stay exact as grants, rows and the
/// tree change.
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
    public async Task EveryAccessShape_PagesTheRowsTheFilterReturns()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PageDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        var tree = await Tree.CreateAsync(db);

        var random = new Random(11);
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
            foreach (var shape in Shapes(db))
            {
                var expected = await Truth(shape.Query, fga, subject);
                var actual = await PageAll(shape.Query, fga, subject, shape.PageSize);
                actual.Should().Equal(expected, $"user {name}, shape {shape.Name}: the page must return exactly what the filter returns");
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
        var tree = await Tree.CreateAsync(db);
        var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
        var alice = (await subjects.CreateUserAsync("Alice", $"alice-{Guid.NewGuid():N}@example.test")).SubjectId;
        var bob = (await subjects.CreateUserAsync("Bob", $"bob-{Guid.NewGuid():N}@example.test")).SubjectId;

        async Task Check(string because)
        {
            foreach (var subject in new[] { alice, bob })
            {
                foreach (var shape in Shapes(db))
                {
                    (await PageAll(shape.Query, fga, subject, shape.PageSize)).Should().Equal(await Truth(shape.Query, fga, subject), $"{because}; shape {shape.Name}");
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
    public async Task Cursors_AndQueryShapes()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PageDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        await Tree.CreateAsync(db);
        var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
        var alice = (await subjects.CreateUserAsync("Alice", $"alice-{Guid.NewGuid():N}@example.test")).SubjectId;
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "c_a", SubjectId = alice, ResourceId = "root", RoleId = ReaderRoleId });
        await db.SaveChangesAsync();

        // A page's cursor is bound to its query: another order or filter refuses it.
        var first = await db.Items.OrderBy(i => i.Rank).ToAccessiblePageAsync(fga, alice, Read, null, 5);
        first.Data.Should().HaveCount(5);
        first.NextCursor.Should().NotBeNull();
        var second = await db.Items.OrderBy(i => i.Rank).ToAccessiblePageAsync(fga, alice, Read, first.NextCursor, 5);
        var lastRank = first.Data[^1].Rank;
        second.Data.Select(i => i.Rank).Should().OnlyContain(r => r > lastRank);
        var act = () => db.Items.OrderBy(i => i.Id).ToAccessiblePageAsync(fga, alice, Read, first.NextCursor, 5);
        await act.Should().ThrowAsync<SqlOSCursorException>();
        var filtered = () => db.Items.Where(i => i.Status == 1).OrderBy(i => i.Rank).ToAccessiblePageAsync(fga, alice, Read, first.NextCursor, 5);
        await filtered.Should().ThrowAsync<SqlOSCursorException>();

        // The rows load through the application's query: Include-free here, but AsNoTracking is honored.
        db.ChangeTracker.Clear();
        var untracked = await db.Items.AsNoTracking().OrderBy(i => i.Rank).ToAccessiblePageAsync(fga, alice, Read, null, 3);
        db.ChangeTracker.Entries().Should().BeEmpty();
        untracked.Data.Should().HaveCount(3);
        var tracked = await db.Items.OrderBy(i => i.Rank).ToAccessiblePageAsync(fga, alice, Read, null, 3);
        db.ChangeTracker.Entries<PageItem>().Should().HaveCount(3);
        tracked.Data.Should().HaveCount(3);

        // Shapes a page cannot take say what to do instead.
        var unindexed = () => db.Items.OrderBy(i => i.Price).ToAccessiblePageAsync(fga, alice, Read, null, 5);
        (await unindexed.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("declare HasIndex(Price)");
        var projected = () => fga.PageAsync(db.Items.Select(i => i).OrderBy(i => i.Rank), alice, Read, null, 5);
        (await projected.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Select");
        var descending = () => db.Items.OrderByDescending(i => i.Rank).ToAccessiblePageAsync(fga, alice, Read, null, 5);
        await descending.Should().ThrowAsync<InvalidOperationException>();

        // Nothing visible: an empty page, no cursor.
        var bob = (await subjects.CreateUserAsync("Bob", $"bob-{Guid.NewGuid():N}@example.test")).SubjectId;
        var empty = await db.Items.OrderBy(i => i.Rank).ToAccessiblePageAsync(fga, bob, Read, null, 5);
        empty.Data.Should().BeEmpty();
        empty.NextCursor.Should().BeNull();
    }

    /// <summary>The query shapes every scenario is checked in.</summary>
    private static IEnumerable<(string Name, IQueryable<PageItem> Query, int PageSize)> Shapes(PageDbContext db)
    {
        yield return ("key order", db.Items.OrderBy(i => i.Id), 7);
        yield return ("rank order", db.Items.OrderBy(i => i.Rank), 5);
        yield return ("rank order, served filter", db.Items.Where(i => i.Status == 1).OrderBy(i => i.Rank), 4);
        yield return ("rank order, residual filter", db.Items.Where(i => i.Price > 60).OrderBy(i => i.Rank), 6);
        yield return ("key order, both filters", db.Items.Where(i => i.Status == 2 && i.Price < 80).OrderBy(i => i.Id), 3);
    }

    /// <summary>Ground truth: the PR's filter, through EF Core.</summary>
    private static async Task<List<string>> Truth(IQueryable<PageItem> query, ISqlOSFgaAuthService fga, string subject)
    {
        var filter = await fga.BuildFilterAsync<PageItem>(subject, Read);
        return await query.AsNoTracking().Where(filter).Select(i => i.Id).ToListAsync();
    }

    /// <summary>Every page of the cursor walk, concatenated.</summary>
    private static async Task<List<string>> PageAll(IQueryable<PageItem> query, ISqlOSFgaAuthService fga, string subject, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        for (var pages = 0; pages < 1_000; pages++)
        {
            var page = await query.AsNoTracking().ToAccessiblePageAsync(fga, subject, Read, cursor, pageSize);
            page.Data.Count.Should().BeLessThanOrEqualTo(pageSize);
            all.AddRange(page.Data.Select(i => i.Id));
            if (page.NextCursor is null)
            {
                page.Data.Count.Should().BeLessThanOrEqualTo(pageSize);
                return all;
            }

            page.Data.Should().HaveCount(pageSize, "a page with a next cursor is full");
            cursor = page.NextCursor;
        }

        throw new InvalidOperationException("The cursor walk did not end.");
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

        private HostedApp(WebApplication app, string database)
        {
            App = app;
            _database = database;
        }

        public WebApplication App { get; }

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
            return new HostedApp(app, database);
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
