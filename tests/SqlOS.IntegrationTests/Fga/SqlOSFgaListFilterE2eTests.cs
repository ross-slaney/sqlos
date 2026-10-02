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
    private const string Read = "E2E_READ";
    private const string ReaderRoleId = "role_e2e_reader";

    [TestMethod]
    public async Task SqlOSDbContext_WithResourceEntities()
    {
        var database = "FgaE2eHosted_" + Guid.NewGuid().ToString("N");
        await TestDatabase.CreateDatabaseAsync(AspireFixture.SqlConnectionString, database);
        var connectionString = TestDatabase.CreateIsolatedConnectionString(AspireFixture.SqlConnectionString, database);
        try
        {
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
                        .Permission(Read, "Read documents", DocumentType)
                        .Role(ReaderRoleId, "e2e_reader", "Reader")
                        .RolePermission("e2e_reader", Read));
                });
            await using var app = builder.Build();

            // The application creates its tables, then SqlOS initializes (the hosted service does it on start).
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HostedFgaDbContext>().Database.EnsureCreatedAsync();
            }

            await app.StartAsync();
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
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
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            TestDatabase.ClearPools();
            await TestDatabase.DropDatabaseAsync(AspireFixture.SqlConnectionString, database);
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
        public string FolderResourceId { get; private set; } = string.Empty;
        public byte[]? FgaScope { get; private set; }

        public string ResourceTypeId => DocumentType;
        public string ResourceName => Id;
        public string? ParentResourceId => FolderResourceId;
        public string? ResourceDescription => null;
        public bool ResourceIsActive => true;
    }

    public sealed class HostedFgaDbContext(DbContextOptions<HostedFgaDbContext> options) : SqlOSDbContext<HostedFgaDbContext>(options)
    {
        public DbSet<HostedDocument> Documents => Set<HostedDocument>();

        protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<HostedDocument>(document =>
            {
                document.ToTable("E2eDocuments");
                document.HasKey(d => d.Id);
                document.Property(d => d.Id).HasMaxLength(64);
                document.Property(d => d.ResourceId).HasMaxLength(128);
                document.Property(d => d.FolderResourceId).HasMaxLength(128);
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

        public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(string resourceId, string subjectIds, string permissionId)
            => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Called first and without a provider name: neither matters, the column is the entity's own.
            modelBuilder.ApplySqlOSFgaModel(GetType());
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
