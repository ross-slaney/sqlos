using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// What SqlOS does at startup to the application's tables, on a fresh database of the engine under test, the
/// way an application's life goes: SqlOS may start before the application's migrations run, rows may be
/// written without SqlOS's triggers (a bulk load), and later migrations rename tables or stop protecting them.
/// After every start the protected tables have exactly SqlOS's objects, and every row has its scope.
/// </summary>
[TestClass]
public class SqlOSFgaStartupIntegrationTests
{
    private const string FolderType = "st_folder";
    private const string DocumentType = "st_document";
    private const string Read = "ST_READ";
    private const string ReaderRoleId = "role_st_reader";

    [TestMethod]
    public async Task SqlOSStartsBeforeTheApplicationsMigrations_ThenTheNextStartProtectsTheTable()
    {
        // The documented order for an application whose migrations reference SqlOS's tables: SqlOS's bootstrap,
        // then the application's migrations. The first start finds the protected table missing and leaves it.
        await using var db = await FreshDatabase.CreateAsync();
        await using var app = db.Open<DocsContext>();
        await SetUpSqlOSAsync(app);
        await StartAsync(app);

        await CreateApplicationTablesAsync(app);
        var (alice, _) = await CreateFoldersAndAliceAsync(app);
        await AddDocsAsync(app, ("a1", "folder_a"), ("b1", "folder_b"));
        (await ScopedRowsAsync(app)).Should().Be(0, "SqlOS has not added the table's triggers yet");

        // The next start (the hosted service, after the migrations) protects the table and fills its rows.
        await StartAsync(app);
        (await ScopedRowsAsync(app)).Should().Be(2);
        (await VisibleAsync(app, alice)).Should().Equal("a1");

        await AddDocsAsync(app, ("a2", "folder_a"));
        (await VisibleAsync(app, alice)).Should().Equal("a1", "a2");
    }

    [TestMethod]
    public async Task RowsWrittenWithoutTriggers_AreFilledByTheRoutineOrTheNextStart_AndOnlyThey()
    {
        await using var db = await FreshDatabase.CreateAsync();
        await using var app = db.Open<DocsContext>();
        await SetUpAsync(app);
        var (alice, _) = await CreateFoldersAndAliceAsync(app);
        await AddDocsAsync(app, ("a1", "folder_a"));

        // A bulk load that skips triggers writes rows without a scope: nobody sees them.
        await WithoutTriggersAsync(app, () => AddDocsAsync(app, ("a2", "folder_a")));
        (await VisibleAsync(app, alice)).Should().Equal("a1");

        // The job runs the fill routine and the rows are visible, without a restart.
        await app.Database.ExecuteSqlRawAsync(TestDatabase.IsPostgreSql
            ? "SELECT \"dbo\".\"fn_SqlOSFgaResources_ScopeFill\"();"
            : "EXEC [dbo].[sp_SqlOSFgaResources_ScopeFill];");
        (await VisibleAsync(app, alice)).Should().Equal("a1", "a2");

        // A start fills rows written without triggers too, and leaves every row that has a scope alone: the
        // fill is an index seek on the rows without one, not a pass over the table.
        await app.Database.ExecuteSqlRawAsync(TestDatabase.Rewrite("UPDATE [StDocs] SET [FgaScope] = {0} WHERE [Id] = 'a1'"), new byte[] { 0x0A });
        await WithoutTriggersAsync(app, () => AddDocsAsync(app, ("a3", "folder_a")));
        await StartAsync(app);
        (await ScopeOfAsync(app, "a1")).Should().Equal(new byte[] { 0x0A });
        (await ScopeOfAsync(app, "a3")).Should().NotBeNull();
    }

    [TestMethod]
    public async Task AProtectedTableIsRenamed_TheNextStartMovesSqlOSObjectsToTheNewName()
    {
        await using var db = await FreshDatabase.CreateAsync();
        await using (var app = db.Open<DocsContext>())
        {
            await SetUpAsync(app);
            await CreateFoldersAndAliceAsync(app);
            await AddDocsAsync(app, ("a1", "folder_a"));
        }

        await using var renamed = db.Open<RenamedDocsContext>();
        await renamed.Database.ExecuteSqlRawAsync(TestDatabase.IsPostgreSql
            ? "ALTER TABLE \"StDocs\" RENAME TO \"StDocsRenamed\";"
            : "EXEC sp_rename 'dbo.StDocs', 'StDocsRenamed';");
        await StartAsync(renamed);

        // Nothing named for the old table is left. (The application's own index keeps its name, IX_StDocs_Rank,
        // through a table rename, so SqlOS's copies of it end with that name.)
        (await SqlOSObjectsAsync(renamed)).Should().NotContain(
            name => name.StartsWith("TR_StDocs_", StringComparison.Ordinal)
                || name.StartsWith("IX_StDocs_FgaScope", StringComparison.Ordinal)
                || name.StartsWith("ST_StDocs_", StringComparison.Ordinal));
        (await SqlOSObjectsAsync(renamed)).Should().Contain(["TR_StDocsRenamed_SqlOSFgaScope_Insert", "IX_StDocsRenamed_FgaScope0", "IX_StDocsRenamed_FgaScopeMissing"]);

        var alice = await AliceAsync(renamed);
        await AddDocsAsync(renamed, ("a2", "folder_a"));
        (await VisibleAsync(renamed, alice)).Should().Equal("a1", "a2");

        // The resource table's triggers address the table under its new name: a move updates its rows.
        await MoveAsync(renamed, "doc::a2", "folder_b");
        (await VisibleAsync(renamed, alice)).Should().Equal("a1");
    }

    [TestMethod]
    public async Task AnEntityNoLongerHasAResourceId_TheNextStartRemovesSqlOSObjects_SoItsMigrationsWork()
    {
        await using var db = await FreshDatabase.CreateAsync();
        await using (var app = db.Open<DocsContext>())
        {
            await SetUpAsync(app);
            await CreateFoldersAndAliceAsync(app);
            await AddDocsAsync(app, ("a1", "folder_a"));
        }

        await using var plain = db.Open<UnprotectedDocsContext>();
        await StartAsync(plain);
        (await SqlOSObjectsAsync(plain)).Should().BeEmpty("the table is no longer SqlOS's to maintain");

        // The application's own migrations can drop the column (SQL Server refuses while SqlOS's computed
        // columns depend on it), and its rows are written without SqlOS.
        await plain.Database.ExecuteSqlRawAsync(TestDatabase.Rewrite("ALTER TABLE [StDocs] DROP COLUMN [FgaScope]"));
        plain.Docs.Add(new StPlainDoc { Id = "p1", ResourceId = "doc::p1" });
        await plain.SaveChangesAsync();

        // Resource writes no longer touch the table.
        await MoveAsync(plain, "folder_a", "root");
        plain.Set<SqlOSFgaResource>().Remove(await plain.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == "doc::a1"));
        await plain.SaveChangesAsync();
    }

    [TestMethod]
    public async Task AnotherSqlOSInstallation_InTheSameDatabase_LeavesThisOnesTablesAlone()
    {
        // Two SqlOS installations, each in its own schema, share a database. The second protects none of the
        // first's tables, and its start must not treat them as stale.
        await using var db = await FreshDatabase.CreateAsync();
        await using var app = db.Open<DocsContext>();
        await SetUpAsync(app);
        var (alice, _) = await CreateFoldersAndAliceAsync(app);
        await AddDocsAsync(app, ("a1", "folder_a"), ("b1", "folder_b"));

        await using var other = db.Open<OtherInstallationContext>();
        if (!TestDatabase.IsPostgreSql)
        {
            await other.Database.ExecuteSqlRawAsync($"CREATE SCHEMA [{OtherInstallationContext.Schema}];");
        }

        var otherOptions = Options.Create(new SqlOSFgaOptions { Schema = OtherInstallationContext.Schema });
        await new SqlOSFgaSchemaInitializer(other, otherOptions, NullLogger<SqlOSFgaSchemaInitializer>.Instance).EnsureSchemaAsync();
        await new SqlOSFgaSeedService(other, otherOptions, NullLogger<SqlOSFgaSeedService>.Instance).SeedCoreAsync();
        await new SqlOSFgaFunctionInitializer(other, otherOptions, NullLogger<SqlOSFgaFunctionInitializer>.Instance).EnsureFunctionsExistAsync();

        (await SqlOSObjectsAsync(app)).Should().Contain(["TR_StDocs_SqlOSFgaScope_Insert", "TR_StDocs_SqlOSFgaScope_Update", "IX_StDocs_FgaScope0"]);

        // The triggers still keep the first installation's rows current: a row pointed at a resource Alice
        // cannot see stops being visible to her.
        var doc = await app.Set<StDoc>().SingleAsync(d => d.Id == "a1");
        doc.ResourceId = "doc::b1";
        await app.SaveChangesAsync();
        app.ChangeTracker.Clear();
        (await VisibleAsync(app, alice)).Should().BeEmpty();

        // And the first installation's next start still finds its own stale objects: a table it no longer
        // protects is cleaned up as before.
        await using var plain = db.Open<UnprotectedDocsContext>();
        await StartAsync(plain);
        (await SqlOSObjectsAsync(plain)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task WhileAnotherInstanceSetsUp_AStartWaitsForIt_InsteadOfFailing()
    {
        await using var db = await FreshDatabase.CreateAsync();
        await using var app = db.Open<DocsContext>();
        await SetUpAsync(app);

        var previous = SqlOSFgaFunctionInitializer.LockWait;
        SqlOSFgaFunctionInitializer.LockWait = TimeSpan.FromMilliseconds(500);
        try
        {
            await using var other = TestDatabase.CreateConnection(db.ConnectionString);
            await other.OpenAsync();
            await ExecuteAsync(other, TestDatabase.IsPostgreSql
                ? "SELECT pg_advisory_lock(('x' || substr(md5('SqlOS:FgaFunctionInitializer'), 1, 8))::bit(32)::int, ('x' || substr(md5('SqlOS:FgaFunctionInitializer'), 9, 8))::bit(32)::int);"
                : "EXEC sp_getapplock @Resource = 'SqlOS:FgaFunctionInitializer', @LockMode = 'Exclusive', @LockOwner = 'Session';");

            var logger = new ListLogger();
            var start = new SqlOSFgaFunctionInitializer(app, Options.Create(new SqlOSFgaOptions()), logger).EnsureFunctionsExistAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));
            start.IsCompleted.Should().BeFalse("the other instance holds the lock");
            logger.Messages.Should().Contain(m => m.Contains("waits for it to finish", StringComparison.Ordinal));

            await other.CloseAsync();
            await start;
        }
        finally
        {
            SqlOSFgaFunctionInitializer.LockWait = previous;
        }
    }

    [TestMethod]
    public async Task SqlServer_ASnapshotTransaction_CannotChangeTheTree()
    {
        if (TestDatabase.IsPostgreSql)
        {
            return;
        }

        await using var db = await FreshDatabase.CreateAsync();
        await using var app = db.Open<DocsContext>();
        await SetUpAsync(app);
        await CreateFoldersAndAliceAsync(app);
        await app.Database.ExecuteSqlRawAsync($"ALTER DATABASE [{db.Name}] SET ALLOW_SNAPSHOT_ISOLATION ON;");

        foreach (var sql in new[]
        {
            "UPDATE [dbo].[SqlOSFgaResources] SET [IsActive] = 0 WHERE [Id] = 'folder_a';",
            "INSERT INTO [StDocs] ([Id], [ResourceId], [Rank]) VALUES ('s1', 'folder_a', 0);",
        })
        {
            await using var connection = TestDatabase.CreateConnection(db.ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Snapshot);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            var act = () => command.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<DbException>()).Which.Message.Should().Contain("not SNAPSHOT");
        }
    }

    // ---- The database and the application ----

    private sealed class FreshDatabase : IAsyncDisposable
    {
        private FreshDatabase(string name, string connectionString)
        {
            Name = name;
            ConnectionString = connectionString;
        }

        public string Name { get; }

        public string ConnectionString { get; }

        public static async Task<FreshDatabase> CreateAsync()
        {
            var name = "FgaStartup_" + Guid.NewGuid().ToString("N");
            await TestDatabase.CreateDatabaseAsync(AspireFixture.SqlConnectionString, name);
            return new FreshDatabase(name, TestDatabase.CreateIsolatedConnectionString(AspireFixture.SqlConnectionString, name));
        }

        public TContext Open<TContext>()
            where TContext : DbContext
            => (TContext)Activator.CreateInstance(typeof(TContext), new DbContextOptionsBuilder<TContext>().UseTestProvider(ConnectionString).Options)!;

        public async ValueTask DisposeAsync()
        {
            TestDatabase.ClearPools();
            await TestDatabase.DropDatabaseAsync(AspireFixture.SqlConnectionString, Name);
        }
    }

    /// <summary>SqlOS's FGA tables and the authorization model, before anything of the application's.</summary>
    private static async Task SetUpSqlOSAsync<TContext>(TContext app)
        where TContext : DbContext, ISqlOSFgaDbContext
    {
        var options = Options.Create(new SqlOSFgaOptions());
        await new SqlOSFgaSchemaInitializer(app, options, NullLogger<SqlOSFgaSchemaInitializer>.Instance).EnsureSchemaAsync();
        var seed = new SqlOSFgaSeedService(app, options, NullLogger<SqlOSFgaSeedService>.Instance);
        await seed.SeedCoreAsync();
        await seed.SeedAuthorizationDataAsync(new SqlOSFgaSeedData
        {
            ResourceTypes = [new SqlOSFgaResourceType { Id = FolderType, Name = "Folder" }, new SqlOSFgaResourceType { Id = DocumentType, Name = "Document" }],
            Permissions = [new SqlOSFgaPermission { Id = "perm_st_read", Key = Read, Name = "Read documents", ResourceTypeId = DocumentType }],
            Roles = [new SqlOSFgaRole { Id = ReaderRoleId, Key = "st_reader", Name = "Reader" }],
            RolePermissions = [("st_reader", [Read])],
        });
    }

    /// <summary>The application's migrations: its tables (SqlOS's are excluded from them).</summary>
    private static Task CreateApplicationTablesAsync(DbContext app)
        => app.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();

    private static async Task SetUpAsync<TContext>(TContext app)
        where TContext : DbContext, ISqlOSFgaDbContext
    {
        await SetUpSqlOSAsync(app);
        await CreateApplicationTablesAsync(app);
        await StartAsync(app);
    }

    private static Task StartAsync(ISqlOSFgaDbContext app)
        => new SqlOSFgaFunctionInitializer(app, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaFunctionInitializer>.Instance)
            .EnsureFunctionsExistAsync();

    /// <summary>Two folders under the root, and Alice holding the reader role on the first.</summary>
    private static async Task<(string Alice, string Bob)> CreateFoldersAndAliceAsync<TContext>(TContext app)
        where TContext : DbContext, ISqlOSFgaDbContext
    {
        app.Set<SqlOSFgaResource>().AddRange(
            new SqlOSFgaResource { Id = "folder_a", ParentId = "root", Name = "Folder A", ResourceTypeId = FolderType },
            new SqlOSFgaResource { Id = "folder_b", ParentId = "root", Name = "Folder B", ResourceTypeId = FolderType });
        await app.SaveChangesAsync();

        var subjects = new SqlOSFgaSubjectService(app, NullLogger<SqlOSFgaSubjectService>.Instance);
        var alice = await subjects.CreateUserAsync("Alice", "alice@startup.test");
        var bob = await subjects.CreateUserAsync("Bob", "bob@startup.test");
        app.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = "grant_alice_a", SubjectId = alice.SubjectId, ResourceId = "folder_a", RoleId = ReaderRoleId });
        await app.SaveChangesAsync();
        app.ChangeTracker.Clear();
        return (alice.SubjectId, bob.SubjectId);
    }

    private static Task<string> AliceAsync(DbContext app)
        => app.Set<SqlOSFgaGrant>().AsNoTracking().Where(g => g.Id == "grant_alice_a").Select(g => g.SubjectId).SingleAsync();

    /// <summary>Documents and their resources, each under a folder.</summary>
    private static async Task AddDocsAsync(DbContext app, params (string Id, string Folder)[] docs)
    {
        foreach (var (id, folder) in docs)
        {
            app.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource { Id = "doc::" + id, ParentId = folder, Name = id, ResourceTypeId = DocumentType });
        }

        await app.SaveChangesAsync();
        foreach (var (id, _) in docs)
        {
            if (app is RenamedDocsContext renamed)
            {
                renamed.Docs.Add(new StDoc { Id = id, ResourceId = "doc::" + id });
            }
            else
            {
                app.Set<StDoc>().Add(new StDoc { Id = id, ResourceId = "doc::" + id });
            }
        }

        await app.SaveChangesAsync();
        app.ChangeTracker.Clear();
    }

    private static async Task MoveAsync(DbContext app, string resourceId, string parentId)
    {
        var resource = await app.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == resourceId);
        resource.ParentId = parentId;
        await app.SaveChangesAsync();
        app.ChangeTracker.Clear();
    }

    private static async Task<List<string>> VisibleAsync<TContext>(TContext app, string subjectId)
        where TContext : DbContext, ISqlOSFgaDbContext
    {
        var fga = new SqlOSFgaAuthService(app, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaAuthService>.Instance);
        var filter = await fga.BuildFilterAsync<StDoc>(subjectId, Read);
        return await app.Set<StDoc>().AsNoTracking().Where(filter).OrderBy(d => d.Id).Select(d => d.Id).ToListAsync();
    }

    private static Task<int> ScopedRowsAsync(DbContext app)
        => app.Set<StDoc>().AsNoTracking().CountAsync(d => d.FgaScope != null);

    private static async Task<byte[]?> ScopeOfAsync(DbContext app, string id)
        => await app.Set<StDoc>().AsNoTracking().Where(d => d.Id == id).Select(d => d.FgaScope).SingleAsync();

    private static async Task WithoutTriggersAsync(DbContext app, Func<Task> write)
    {
        await app.Database.ExecuteSqlRawAsync(TestDatabase.IsPostgreSql ? "ALTER TABLE \"StDocs\" DISABLE TRIGGER USER;" : "ALTER TABLE [StDocs] DISABLE TRIGGER ALL;");
        try
        {
            await write();
        }
        finally
        {
            await app.Database.ExecuteSqlRawAsync(TestDatabase.IsPostgreSql ? "ALTER TABLE \"StDocs\" ENABLE TRIGGER USER;" : "ALTER TABLE [StDocs] ENABLE TRIGGER ALL;");
        }
    }

    /// <summary>The names of SqlOS's objects on application tables: triggers, indexes, statistics, computed columns.</summary>
    private static async Task<List<string>> SqlOSObjectsAsync(DbContext app)
    {
        var sql = TestDatabase.IsPostgreSql
            ? """
              SELECT tgname::text FROM pg_trigger WHERE NOT tgisinternal AND tgname LIKE 'TR\_%\_SqlOSFgaScope\_%'
              UNION ALL SELECT indexname::text FROM pg_indexes WHERE indexname ~ '^IX_.*_FgaScope'
              UNION ALL SELECT stxname::text FROM pg_statistic_ext WHERE stxname LIKE 'ST\_%\_FgaScopeType'
              """
            : """
              SELECT name FROM sys.triggers WHERE name LIKE 'TR[_]%[_]SqlOSFgaScope[_]%'
              UNION ALL SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]%[_]FgaScope%'
              UNION ALL SELECT name FROM sys.stats WHERE user_created = 1 AND name LIKE 'ST[_]%[_]FgaScopeType'
              UNION ALL SELECT name FROM sys.computed_columns WHERE name LIKE 'FgaScope%'
              """;
        var names = new List<string>();
        var connection = app.Database.GetDbConnection();
        await app.Database.OpenConnectionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }
        finally
        {
            await app.Database.CloseConnectionAsync();
        }

        return names;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ListLogger : ILogger<SqlOSFgaFunctionInitializer>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception));
    }

    // ---- The application's models: the same table, protected, renamed, and no longer protected ----

    public sealed class StDoc : IHasResourceId
    {
        public string Id { get; set; } = string.Empty;
        public string ResourceId { get; set; } = string.Empty;
        public byte[]? FgaScope { get; private set; }
        public int Rank { get; set; }
    }

    public sealed class StPlainDoc
    {
        public string Id { get; set; } = string.Empty;
        public string ResourceId { get; set; } = string.Empty;
        public int Rank { get; set; }
    }

    public sealed class DocsContext(DbContextOptions<DocsContext> options) : DbContext(options), ISqlOSFgaDbContext
    {
        public DbSet<StDoc> Docs => Set<StDoc>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StDoc>(doc =>
            {
                doc.ToTable("StDocs");
                doc.HasKey(d => d.Id);
                doc.Property(d => d.Id).HasMaxLength(64);
                doc.Property(d => d.ResourceId).HasMaxLength(128);
                doc.HasIndex(d => new { d.Rank, d.Id }).HasDatabaseName("IX_StDocs_Rank");
            });
            modelBuilder.ApplySqlOSFgaModel();
        }
    }

    public sealed class RenamedDocsContext(DbContextOptions<RenamedDocsContext> options) : DbContext(options), ISqlOSFgaDbContext
    {
        public DbSet<StDoc> Docs => Set<StDoc>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StDoc>(doc =>
            {
                doc.ToTable("StDocsRenamed");
                doc.HasKey(d => d.Id);
                doc.Property(d => d.Id).HasMaxLength(64);
                doc.Property(d => d.ResourceId).HasMaxLength(128);
                doc.HasIndex(d => new { d.Rank, d.Id }).HasDatabaseName("IX_StDocs_Rank");
            });
            modelBuilder.ApplySqlOSFgaModel();
        }
    }

    /// <summary>A second SqlOS installation in its own schema, protecting none of the first's tables.</summary>
    public sealed class OtherInstallationContext(DbContextOptions<OtherInstallationContext> options) : DbContext(options), ISqlOSFgaDbContext
    {
        public const string Schema = "sqlos_other";

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplySqlOSFgaModel(o => o.Schema = Schema);
    }

    public sealed class UnprotectedDocsContext(DbContextOptions<UnprotectedDocsContext> options) : DbContext(options), ISqlOSFgaDbContext
    {
        public DbSet<StPlainDoc> Docs => Set<StPlainDoc>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StPlainDoc>(doc =>
            {
                doc.ToTable("StDocs");
                doc.HasKey(d => d.Id);
                doc.Property(d => d.Id).HasMaxLength(64);
                doc.Property(d => d.ResourceId).HasMaxLength(128);
            });
            modelBuilder.ApplySqlOSFgaModel();
        }
    }
}
