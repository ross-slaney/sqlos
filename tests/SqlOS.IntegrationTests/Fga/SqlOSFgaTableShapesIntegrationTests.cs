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
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// A protected table that holds more than the root entity type's own properties (a hierarchy and an owned
/// type), read through every statement shape; and two transactions writing adjacent rows of a protected
/// table at once. On SQL Server the planned statements read SqlOS's projection of the table joined to the
/// row, and the row triggers keep that projection current: neither may assume the table holds exactly the
/// root type's properties, and neither may make a writer wait for another writer's open transaction.
/// </summary>
[TestClass]
public class SqlOSFgaTableShapesIntegrationTests
{
    private const string FolderType = "shape_folder";
    private const string DocumentType = "shape_document";
    private const string TicketType = "shape_ticket";
    private const string Read = "SHAPE_READ";
    private const string ReaderRoleId = "role_shape_reader";

    [TestMethod]
    public async Task AHierarchyWithAnOwnedType_IsReadWhole_ThroughEveryStatementShape()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShapeDbContext>();
        var (alice, bob) = await CreateFoldersUsersAndGrantAsync(db);
        db.Documents.AddRange(
            new ShapeDocument("a1", "folder_a") { Extent = new ShapeExtent { Width = 1, Height = 10 } },
            new ShapeMemo("a2", "folder_a") { Extent = new ShapeExtent { Width = 2, Height = 20 }, Subject = "memo a2" },
            new ShapeDocument("b1", "folder_b") { Extent = new ShapeExtent { Width = 3, Height = 30 } });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        var filter = await fga.BuildFilterAsync<ShapeDocument>(alice, Read);

        // Not a page (no Take): the filter runs as a planned statement. On SQL Server that statement reads
        // SqlOS's projection of the table joined to the row, and it must return every column of the table:
        // the derived type's, the owned type's, the discriminator, the computed column.
        var list = db.Documents.AsNoTracking().Where(filter).OrderBy(d => d.Id);
        if (TestDatabase.IsSqlServer)
        {
            list.ToQueryString().Should().Contain("SqlOSFgaScopeIndex_ShapeDocuments", "a planned statement reads SqlOS's projection of the table");
        }

        var rows = await list.ToListAsync();
        rows.Select(r => r.Id).Should().Equal("a1", "a2");
        rows.Select(r => r.Extent.Width).Should().Equal(1, 2);
        rows.Select(r => r.Area).Should().Equal(10, 40);
        rows[1].Should().BeOfType<ShapeMemo>().Which.Subject.Should().Be("memo a2");

        (await db.Documents.Where(filter).CountAsync()).Should().Be(2);
        (await db.Documents.AsNoTracking().Where(filter).OfType<ShapeMemo>().Select(m => m.Subject).ToListAsync()).Should().Equal("memo a2");
        var memos = await fga.BuildFilterAsync<ShapeMemo>(alice, Read);
        (await db.Set<ShapeMemo>().AsNoTracking().Where(memos).Select(m => m.Subject).ToListAsync()).Should().Equal("memo a2");

        // A page is walked, and its rows are loaded by key through the application's query: the same columns.
        // EF Core aliases this table "s", like the round SQL once aliased its own streams table: every seek
        // of the walk has both in scope, and the application's must not shadow SqlOS's.
        var page = await db.Documents.AsNoTracking().Where(filter).OrderBy(d => d.Id).Take(10).ToListAsync();
        page.Select(r => r.Id).Should().Equal("a1", "a2");
        page[1].Should().BeOfType<ShapeMemo>().Which.Extent.Height.Should().Be(20);
        var filtered = await db.Documents.AsNoTracking().Where(filter).Where(d => d.FolderResourceId == "folder_a").OrderBy(d => d.Id).Take(10).ToListAsync();
        filtered.Select(r => r.Id).Should().Equal(["a1", "a2"], "with a residual filter, the stream joins the row under EF Core's alias of the table");

        (await db.Documents.AsNoTracking().Where(await fga.BuildFilterAsync<ShapeDocument>(bob, Read)).ToListAsync()).Should().BeEmpty();
    }

    [TestMethod]
    public async Task SqlServer_TwoTransactionsInsertingAdjacentRows_DoNotWaitForEachOther()
    {
        if (TestDatabase.IsPostgreSql)
        {
            // PostgreSQL keeps no projection of the table: its triggers write the row alone.
            return;
        }

        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShapeDbContext>();
        await CreateFoldersUsersAndGrantAsync(db);

        // The rows' resources exist already, committed: the two transactions below write only the rows, so all
        // they can wait on is the row triggers' own work, the scope copy and SqlOS's projection of the table.
        db.Set<SqlOSFgaResource>().AddRange(
            new SqlOSFgaResource { Id = "ticket::k1", ParentId = "folder_a", Name = "k1", ResourceTypeId = TicketType },
            new SqlOSFgaResource { Id = "ticket::k2", ParentId = "folder_a", Name = "k2", ResourceTypeId = TicketType });
        await db.SaveChangesAsync();

        await using var firstScope = host.App.Services.CreateAsyncScope();
        await using var secondScope = host.App.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<ShapeDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<ShapeDbContext>();

        // The first transaction has inserted k1 (its triggers put k1 in the projection) and stays open, holding
        // every lock its triggers took.
        await using var open = await first.Database.BeginTransactionAsync();
        first.Tickets.Add(new ShapeTicket { Id = "k1", ResourceId = "ticket::k1" });
        await first.SaveChangesAsync();

        // A second transaction inserts the adjacent key. A serializable merge into the projection would have
        // left the first transaction holding a range lock past k1, and this insert would wait for its commit.
        second.Database.SetCommandTimeout(TimeSpan.FromSeconds(10));
        second.Tickets.Add(new ShapeTicket { Id = "k2", ResourceId = "ticket::k2" });
        var insert = () => second.SaveChangesAsync();
        await insert.Should().NotThrowAsync("a writer of one key must not wait on another key's open transaction");
        await open.CommitAsync();

        (await ReadAsync(db, "SELECT Id, CONVERT(varchar(max), FgaScope, 2) FROM dbo.SqlOSFgaScopeIndex_ShapeTickets ORDER BY Id"))
            .Should().Equal(await ReadAsync(db, "SELECT Id, CONVERT(varchar(max), FgaScope, 2) FROM ShapeTickets ORDER BY Id"), "both rows reached the projection with their scope");
    }

    /// <summary>Two folders under the root, two users, and a reader grant for the first user on the first folder.</summary>
    private static async Task<(string Alice, string Bob)> CreateFoldersUsersAndGrantAsync(ShapeDbContext db)
    {
        db.Set<SqlOSFgaResource>().AddRange(
            new SqlOSFgaResource { Id = "folder_a", ParentId = "root", Name = "Folder A", ResourceTypeId = FolderType },
            new SqlOSFgaResource { Id = "folder_b", ParentId = "root", Name = "Folder B", ResourceTypeId = FolderType });
        await db.SaveChangesAsync();

        var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
        var alice = await subjects.CreateUserAsync("Alice", $"alice-{Guid.NewGuid():N}@example.test");
        var bob = await subjects.CreateUserAsync("Bob", $"bob-{Guid.NewGuid():N}@example.test");
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "grant_alice_a", SubjectId = alice.SubjectId, ResourceId = "folder_a", RoleId = ReaderRoleId });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (alice.SubjectId, bob.SubjectId);
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
                rows.Add(string.Join("|", values.Select(v => v is DBNull ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))));
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

    public sealed class ShapeExtent
    {
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>The root of a hierarchy, with an owned type and a computed column, in the base-class form.</summary>
    public class ShapeDocument : SqlOSResourceEntity
    {
        protected ShapeDocument()
        {
        }

        public ShapeDocument(string id, string folderResourceId)
        {
            Id = id;
            ResourceId = "doc::" + id;
            FolderResourceId = folderResourceId;
        }

        public string Id { get; private set; } = string.Empty;
        public string FolderResourceId { get; private set; } = string.Empty;
        public ShapeExtent Extent { get; set; } = new();
        public int Area { get; private set; }

        public override string ResourceTypeId => DocumentType;
        public override string ResourceName => Id;
        public override string? ParentResourceId => FolderResourceId;
    }

    public sealed class ShapeMemo : ShapeDocument
    {
        private ShapeMemo()
        {
        }

        public ShapeMemo(string id, string folderResourceId)
            : base(id, folderResourceId)
        {
        }

        public string Subject { get; set; } = string.Empty;
    }

    /// <summary>A protected row whose resource the application creates itself.</summary>
    public sealed class ShapeTicket : IHasResourceId
    {
        public string Id { get; set; } = string.Empty;
        public string ResourceId { get; set; } = string.Empty;
        public byte[]? FgaScope { get; private set; }
    }

    public sealed class ShapeDbContext(DbContextOptions<ShapeDbContext> options) : SqlOSDbContext<ShapeDbContext>(options)
    {
        public DbSet<ShapeDocument> Documents => Set<ShapeDocument>();

        public DbSet<ShapeTicket> Tickets => Set<ShapeTicket>();

        protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ShapeDocument>(document =>
            {
                document.ToTable("ShapeDocuments");
                document.HasKey(d => d.Id);
                document.Property(d => d.Id).HasMaxLength(64);
                document.Property(d => d.ResourceId).HasMaxLength(128);
                document.Property(d => d.FolderResourceId).HasMaxLength(128);
                document.OwnsOne(d => d.Extent);
                document.Property(d => d.Area).HasComputedColumnSql(
                    TestDatabase.IsPostgreSql ? "\"Extent_Width\" * \"Extent_Height\"" : "[Extent_Width] * [Extent_Height]",
                    stored: true);
                document.HasDiscriminator<string>("Kind").HasValue<ShapeDocument>("document").HasValue<ShapeMemo>("memo");
            });
            modelBuilder.Entity<ShapeMemo>(memo => memo.Property(m => m.Subject).HasMaxLength(200));
            modelBuilder.Entity<ShapeTicket>(ticket =>
            {
                ticket.ToTable("ShapeTickets");
                ticket.HasKey(t => t.Id);
                ticket.Property(t => t.Id).HasMaxLength(64);
                ticket.Property(t => t.ResourceId).HasMaxLength(128);
            });
        }
    }

    /// <summary>An application host on a fresh database: AddSqlOS, the application's tables, then SqlOS's own start.</summary>
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
            var database = "FgaShapes_" + Guid.NewGuid().ToString("N");
            await TestDatabase.CreateDatabaseAsync(AspireFixture.SqlConnectionString, database);
            var connectionString = TestDatabase.CreateIsolatedConnectionString(AspireFixture.SqlConnectionString, database);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.AddSqlOS<ShapeDbContext>(
                db => db.UseTestProvider(connectionString),
                options =>
                {
                    options.AuthServer.PublicOrigin = "http://localhost";
                    options.AuthServer.Issuer = "http://localhost/sqlos/auth";
                    options.Fga.Seed(seed => seed
                        .ResourceType(FolderType, "Folder")
                        .ResourceType(DocumentType, "Document")
                        .ResourceType(TicketType, "Ticket")
                        .Permission(Read, "Read documents", DocumentType)
                        .Role(ReaderRoleId, "shape_reader", "Reader")
                        .RolePermission("shape_reader", Read));
                });
            var app = builder.Build();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ShapeDbContext>().Database.EnsureCreatedAsync();
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
