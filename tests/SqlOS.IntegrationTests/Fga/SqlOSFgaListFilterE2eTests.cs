using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Services;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// The two ways an application adds SqlOS FGA, each on a fresh database of the engine under test
/// (SQLOS_TEST_PROVIDER), the way an application starts: the entity declares its scope column, the
/// application creates its tables, SqlOS initializes, and a list filtered by <c>BuildFilterAsync</c>
/// returns exactly the rows the caller may see, including a row inserted after the grant.
/// </summary>
[TestClass]
public class SqlOSFgaListFilterE2eTests
{
    private const string FolderType = "e2e_folder";
    private const string DocumentType = "e2e_document";
    private const string NoteType = "e2e_note";
    private const string Read = "E2E_READ";
    private const string ReaderRoleId = "role_e2e_reader";

    [TestMethod]
    public async Task SqlOSDbContext_WithResourceEntities()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HostedFgaDbContext>();
        var (alice, bob) = await CreateFoldersUsersAndGrantAsync(db);

        // ISqlOSResourceEntity: saving the documents creates their FGA resources under their folder.
        db.Documents.AddRange(
            new HostedDocument("a1", "folder_a"), new HostedDocument("a2", "folder_a"), new HostedDocument("a3", "folder_a"),
            new HostedDocument("b1", "folder_b"), new HostedDocument("b2", "folder_b"));
        await db.SaveChangesAsync();

        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        (await PageAsync(db.Documents, fga, alice)).Should().Equal("a1", "a2", "a3");
        (await PageAsync(db.Documents, fga, bob)).Should().BeEmpty();

        db.Documents.Add(new HostedDocument("a4", "folder_a"));
        await db.SaveChangesAsync();
        (await PageAsync(db.Documents, fga, alice)).Should().Equal("a1", "a2", "a3", "a4");

        db.ChangeTracker.Clear();
        (await db.Documents.AsNoTracking().ToListAsync()).Should().OnlyContain(d => d.FgaScope != null, "SqlOS fills every row's scope");
    }

    [TestMethod]
    public async Task ExplicitScopeEntity_AndAFilterUsedOnAnotherContextInstance()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HostedFgaDbContext>();
        var (alice, bob) = await CreateFoldersUsersAndGrantAsync(db);

        // HostedNote implements FgaScope explicitly (byte[]? IHasResourceId.FgaScope => null): SqlOS maps the
        // column itself, keeps it current, and the list filter reads it.
        db.Notes.AddRange(new HostedNote("n1", "folder_a"), new HostedNote("n2", "folder_a"), new HostedNote("n3", "folder_b"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await db.Notes.AsNoTracking().CountAsync(n => EF.Property<byte[]>(n, nameof(IHasResourceId.FgaScope)) != null))
            .Should().Be(3, "SqlOS fills the column of an explicit implementation too");

        // A filter built by this request's service composes into a query on another context instance, as a
        // context from IDbContextFactory or a pool would be: the filter carries no DbContext.
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        var filter = await fga.BuildFilterAsync<HostedNote>(alice, Read);
        await using var otherScope = host.App.Services.CreateAsyncScope();
        var other = otherScope.ServiceProvider.GetRequiredService<HostedFgaDbContext>();
        other.Should().NotBeSameAs(db);
        (await other.Notes.AsNoTracking().Where(filter).OrderBy(n => n.Id).Select(n => n.Id).ToListAsync())
            .Should().Equal("n1", "n2");
        (await PageAsync(db.Notes, fga, bob)).Should().BeEmpty();

        // The point check reads the same lineage, in one query.
        (await fga.CheckAccessAsync(alice, Read, "note::n1")).Allowed.Should().BeTrue();
        (await fga.CheckAccessAsync(alice, Read, "note::n3")).Allowed.Should().BeFalse();
        (await fga.CheckAccessAsync(bob, Read, "note::n1")).Allowed.Should().BeFalse();
    }

    [TestMethod]
    public async Task SqlOSDbContext_ResourceEntityLifecycle()
    {
        await using var host = await HostedApp.StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HostedFgaDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();

        // Folders and their documents are all entities, saved together: parents and children in one SaveChanges.
        var folderA = new HostedFolder("fa");
        var folderB = new HostedFolder("fb");
        db.Folders.AddRange(folderA, folderB);
        db.Documents.AddRange(new HostedDocument("d1", folderA.ResourceId), new HostedDocument("d2", folderA.ResourceId), new HostedDocument("d3", folderB.ResourceId));
        await db.SaveChangesAsync();

        var subjects = new SqlOSFgaSubjectService(db, NullLogger<SqlOSFgaSubjectService>.Instance);
        var alice = (await subjects.CreateUserAsync("Alice", $"alice-{Guid.NewGuid():N}@example.test")).SubjectId;
        var bob = (await subjects.CreateUserAsync("Bob", $"bob-{Guid.NewGuid():N}@example.test")).SubjectId;
        db.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "grant_alice_fa", SubjectId = alice, ResourceId = folderA.ResourceId, RoleId = ReaderRoleId });
        await db.SaveChangesAsync();
        (await PageAsync(db.Documents, fga, alice)).Should().Equal("d1", "d2");

        // Move: d2 goes to folder B.
        var d2 = await db.Documents.SingleAsync(d => d.Id == "d2");
        d2.FolderResourceId = folderB.ResourceId;
        await db.SaveChangesAsync();
        (await PageAsync(db.Documents, fga, alice)).Should().Equal("d1");

        // Deactivate the granted folder: nothing flows from it. Reactivate: it flows again.
        folderA.Active = false;
        await db.SaveChangesAsync();
        (await PageAsync(db.Documents, fga, alice)).Should().BeEmpty();
        folderA.Active = true;
        await db.SaveChangesAsync();
        (await PageAsync(db.Documents, fga, alice)).Should().Equal("d1");

        // Deactivate the document itself: it is hidden even under an active folder.
        var d1 = await db.Documents.SingleAsync(d => d.Id == "d1");
        d1.Active = false;
        await db.SaveChangesAsync();
        (await PageAsync(db.Documents, fga, alice)).Should().BeEmpty();
        d1.Active = true;
        await db.SaveChangesAsync();

        // Retype: the permission covers documents, not notes.
        d1.TypeId = NoteType;
        await db.SaveChangesAsync();
        (await PageAsync(db.Documents, fga, alice)).Should().BeEmpty();
        d1.TypeId = DocumentType;
        await db.SaveChangesAsync();
        (await PageAsync(db.Documents, fga, alice)).Should().Equal("d1");

        // Delete: the row and its resource go.
        db.Documents.Remove(d1);
        await db.SaveChangesAsync();
        (await PageAsync(db.Documents, fga, alice)).Should().BeEmpty();
        (await db.Set<SqlOSFgaResource>().AnyAsync(r => r.Id == "doc::d1")).Should().BeFalse();

        (await PageAsync(db.Documents, fga, bob)).Should().BeEmpty("bob holds no grant at any point");
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
            var database = "FgaE2eHosted_" + Guid.NewGuid().ToString("N");
            await TestDatabase.CreateDatabaseAsync(AspireFixture.SqlConnectionString, database);
            var connectionString = TestDatabase.CreateIsolatedConnectionString(AspireFixture.SqlConnectionString, database);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.AddSqlOS<HostedFgaDbContext>(
                db => db.UseTestProvider(connectionString),
                options =>
                {
                    options.AuthServer.PublicOrigin = "http://localhost";
                    options.AuthServer.Issuer = "http://localhost/sqlos/auth";
                    options.Fga.Seed(seed => seed
                        .ResourceType(FolderType, "Folder")
                        .ResourceType(DocumentType, "Document")
                        .ResourceType(NoteType, "Note")
                        .Permission(Read, "Read documents", DocumentType)
                        .Role(ReaderRoleId, "e2e_reader", "Reader")
                        .RolePermission("e2e_reader", Read));
                });
            var app = builder.Build();

            // The application creates its tables, then SqlOS initializes (the hosted service does it on start).
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HostedFgaDbContext>().Database.EnsureCreatedAsync();
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

    [TestMethod]
    public async Task PlainDbContext_WithApplySqlOSFgaModel()
    {
        var database = "FgaE2eManual_" + Guid.NewGuid().ToString("N");
        await TestDatabase.CreateDatabaseAsync(AspireFixture.SqlConnectionString, database);
        var connectionString = TestDatabase.CreateIsolatedConnectionString(AspireFixture.SqlConnectionString, database);
        try
        {
            var options = Options.Create(new SqlOSFgaOptions());
            await using (var setup = ManualFgaDbContext.Create(connectionString))
            {
                // The application creates its tables, then initializes SqlOS FGA itself.
                await setup.Database.EnsureCreatedAsync();
                await new SqlOSFgaSchemaInitializer(setup, options, NullLogger<SqlOSFgaSchemaInitializer>.Instance).EnsureSchemaAsync();
                await new SqlOSFgaFunctionInitializer(setup, options, NullLogger<SqlOSFgaFunctionInitializer>.Instance).EnsureFunctionsExistAsync();
                var seed = new SqlOSFgaSeedService(setup, options, NullLogger<SqlOSFgaSeedService>.Instance);
                await seed.SeedCoreAsync();
                await seed.SeedAuthorizationDataAsync(new SqlOSFgaSeedData
                {
                    ResourceTypes = [new SqlOSFgaResourceType { Id = FolderType, Name = "Folder" }, new SqlOSFgaResourceType { Id = DocumentType, Name = "Document" }],
                    Permissions = [new SqlOSFgaPermission { Id = "perm_e2e_read", Key = Read, Name = "Read documents", ResourceTypeId = DocumentType }],
                    Roles = [new SqlOSFgaRole { Id = ReaderRoleId, Key = "e2e_reader", Name = "Reader" }],
                    RolePermissions = [("e2e_reader", [Read])],
                });
            }

            await using var db = ManualFgaDbContext.Create(connectionString);
            var (alice, bob) = await CreateFoldersUsersAndGrantAsync(db);

            // IHasResourceId: the application creates each row's FGA resource itself.
            foreach (var (id, folder) in new[] { ("a1", "folder_a"), ("a2", "folder_a"), ("a3", "folder_a"), ("b1", "folder_b"), ("b2", "folder_b") })
            {
                db.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource { Id = "doc::" + id, ParentId = folder, Name = id, ResourceTypeId = DocumentType });
                db.Tickets.Add(new ManualTicket { Id = id, ResourceId = "doc::" + id });
            }

            await db.SaveChangesAsync();
            var fga = new SqlOSFgaAuthService(db, options, NullLogger<SqlOSFgaAuthService>.Instance);
            (await PageAsync(db.Tickets, fga, alice)).Should().Equal("a1", "a2", "a3");
            (await PageAsync(db.Tickets, fga, bob)).Should().BeEmpty();

            db.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource { Id = "doc::a4", ParentId = "folder_a", Name = "a4", ResourceTypeId = DocumentType });
            db.Tickets.Add(new ManualTicket { Id = "a4", ResourceId = "doc::a4" });
            await db.SaveChangesAsync();
            (await PageAsync(db.Tickets, fga, alice)).Should().Equal("a1", "a2", "a3", "a4");

            db.ChangeTracker.Clear();
            (await db.Tickets.AsNoTracking().ToListAsync()).Should().OnlyContain(t => t.FgaScope != null, "SqlOS fills every row's scope");
        }
        finally
        {
            TestDatabase.ClearPools();
            await TestDatabase.DropDatabaseAsync(AspireFixture.SqlConnectionString, database);
        }
    }

    /// <summary>Two folders under the root, two users, and a reader grant for the first user on the first folder.</summary>
    private static async Task<(string Alice, string Bob)> CreateFoldersUsersAndGrantAsync<TContext>(TContext db)
        where TContext : DbContext, ISqlOSFgaDbContext
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
        return (alice.SubjectId, bob.SubjectId);
    }

    private static async Task<List<string>> PageAsync<T>(IQueryable<T> rows, ISqlOSFgaAuthService fga, string subjectId)
        where T : class, IHasResourceId, IE2eRow
    {
        var filter = await fga.BuildFilterAsync<T>(subjectId, Read);
        return await rows.AsNoTracking().Where(filter).OrderBy(r => r.Id).Select(r => r.Id).Take(20).ToListAsync();
    }

    public interface IE2eRow
    {
        string Id { get; }
    }

    // ---- The SqlOSDbContext approach: ISqlOSResourceEntity rows, resources synced on save ----

    public sealed class HostedDocument : ISqlOSResourceEntity, IE2eRow
    {
        private HostedDocument()
        {
        }

        public HostedDocument(string id, string folderResourceId)
        {
            Id = id;
            ResourceId = "doc::" + id;
            FolderResourceId = folderResourceId;
        }

        public string Id { get; private set; } = string.Empty;
        public string ResourceId { get; private set; } = string.Empty;
        public byte[]? FgaScope { get; private set; }
        public string FolderResourceId { get; set; } = string.Empty;
        public string TypeId { get; set; } = DocumentType;
        public bool Active { get; set; } = true;

        public string ResourceTypeId => TypeId;
        public string ResourceName => Id;
        public string? ParentResourceId => FolderResourceId;
        public string? ResourceDescription => null;
        public bool ResourceIsActive => Active;
    }

    public sealed class HostedNote : ISqlOSResourceEntity, IE2eRow
    {
        private HostedNote()
        {
        }

        public HostedNote(string id, string folderResourceId)
        {
            Id = id;
            ResourceId = "note::" + id;
            FolderResourceId = folderResourceId;
        }

        public string Id { get; private set; } = string.Empty;
        public string ResourceId { get; private set; } = string.Empty;
        public string FolderResourceId { get; private set; } = string.Empty;

        // SqlOS's column, kept off the class's public surface.
        byte[]? IHasResourceId.FgaScope => null;

        string ISqlOSResourceEntity.ResourceTypeId => DocumentType;
        string ISqlOSResourceEntity.ResourceName => Id;
        string? ISqlOSResourceEntity.ParentResourceId => FolderResourceId;
        string? ISqlOSResourceEntity.ResourceDescription => null;
        bool ISqlOSResourceEntity.ResourceIsActive => true;
    }

    // The base-class form: the resource id and the scope column come from SqlOSResourceEntity.
    public sealed class HostedFolder : SqlOSResourceEntity
    {
        private HostedFolder()
        {
        }

        public HostedFolder(string id)
        {
            Id = id;
            ResourceId = "folder::" + id;
        }

        public string Id { get; private set; } = string.Empty;
        public bool Active { get; set; } = true;

        public override string ResourceTypeId => FolderType;
        public override string ResourceName => Id;
        public override string? ParentResourceId => "root";
        public override bool ResourceIsActive => Active;
    }

    public sealed class HostedFgaDbContext(DbContextOptions<HostedFgaDbContext> options) : SqlOSDbContext<HostedFgaDbContext>(options)
    {
        public DbSet<HostedDocument> Documents => Set<HostedDocument>();

        public DbSet<HostedFolder> Folders => Set<HostedFolder>();

        public DbSet<HostedNote> Notes => Set<HostedNote>();

        protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<HostedNote>(note =>
            {
                note.ToTable("E2eNotes");
                note.HasKey(n => n.Id);
                note.Property(n => n.Id).HasMaxLength(64);
                note.Property(n => n.ResourceId).HasMaxLength(128);
                note.Property(n => n.FolderResourceId).HasMaxLength(128);
            });
            modelBuilder.Entity<HostedFolder>(folder =>
            {
                folder.ToTable("E2eFolders");
                folder.HasKey(f => f.Id);
                folder.Property(f => f.Id).HasMaxLength(64);
                folder.Property(f => f.ResourceId).HasMaxLength(128);
            });
            modelBuilder.Entity<HostedDocument>(document =>
            {
                document.ToTable("E2eDocuments");
                document.HasKey(d => d.Id);
                document.Property(d => d.Id).HasMaxLength(64);
                document.Property(d => d.ResourceId).HasMaxLength(128);
                document.Property(d => d.FolderResourceId).HasMaxLength(128);
                document.Property(d => d.TypeId).HasMaxLength(64);
            });
        }
    }

    // ---- The manual approach: a plain DbContext, ApplySqlOSFgaModel, IHasResourceId rows ----

    public sealed class ManualTicket : IHasResourceId, IE2eRow
    {
        public string Id { get; set; } = string.Empty;
        public string ResourceId { get; set; } = string.Empty;
        public byte[]? FgaScope { get; private set; }
    }

    public sealed class ManualFgaDbContext(DbContextOptions<ManualFgaDbContext> options) : DbContext(options), ISqlOSFgaDbContext
    {
        public DbSet<ManualTicket> Tickets => Set<ManualTicket>();

        public static ManualFgaDbContext Create(string connectionString)
            => new(new DbContextOptionsBuilder<ManualFgaDbContext>().UseTestProvider(connectionString).Options);


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Called first and without a provider name: neither matters, the column is the entity's own.
            modelBuilder.ApplySqlOSFgaModel();
            modelBuilder.Entity<ManualTicket>(ticket =>
            {
                ticket.ToTable("E2eTickets");
                ticket.HasKey(t => t.Id);
                ticket.Property(t => t.Id).HasMaxLength(64);
                ticket.Property(t => t.ResourceId).HasMaxLength(128);
            });
        }
    }
}
