using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// What SqlOS does at startup, on a fresh database of the engine under test, the way an application's life
/// goes: SqlOS may start before the application's migrations run, rows may be written without triggers (a bulk
/// load), and later migrations rename tables or stop protecting them. SqlOS keeps everything of its own on its
/// own tables: an application table carries nothing but its resource id, so none of this needs a start.
/// </summary>
[TestClass]
public class SqlOSFgaStartupIntegrationTests
{
    private const string FolderType = "st_folder";
    private const string DocumentType = "st_document";
    private const string Read = "ST_READ";
    private const string ReadPermissionId = "perm_st_read";
    private const string ReaderRoleId = "role_st_reader";

    [TestMethod]
    public async Task SqlOSStartsBeforeTheApplicationsMigrations_AndTheFilterWorksWithoutAnotherStart()
    {
        // The documented order for an application whose migrations reference SqlOS's tables: SqlOS's bootstrap,
        // then the application's migrations. Nothing of SqlOS's depends on the application's tables existing.
        await using var db = await FreshDatabase.CreateAsync();
        await using var app = db.Open<DocsContext>();
        await SetUpSqlOSAsync(app);
        await StartAsync(app);

        await CreateApplicationTablesAsync(app);
        var (alice, _) = await CreateFoldersAndAliceAsync(app);
        await AddDocsAsync(app, ("a1", "folder_a"), ("b1", "folder_b"));
        (await VisibleAsync(app, alice)).Should().Equal("a1");

        await AddDocsAsync(app, ("a2", "folder_a"));
        (await VisibleAsync(app, alice)).Should().Equal("a1", "a2");
        (await TableObjectsAsync(app, "StDocs")).Should().BeEquivalentTo(["PK_StDocs", "IX_StDocs_Rank", "IX_StDocs_SqlOSFgaResourceId"], "the table carries only the application's own objects");
    }

    [TestMethod]
    public async Task RowsWrittenWithoutTriggers_AreVisibleAtOnce()
    {
        await using var db = await FreshDatabase.CreateAsync();
        await using var app = db.Open<DocsContext>();
        await SetUpAsync(app);
        var (alice, _) = await CreateFoldersAndAliceAsync(app);
        await AddDocsAsync(app, ("a1", "folder_a"));

        // A bulk load that skips triggers has nothing of SqlOS's to skip: the rows are visible as soon as they exist.
        await WithoutTriggersAsync(app, () => AddDocsAsync(app, ("a2", "folder_a")));
        (await VisibleAsync(app, alice)).Should().Equal("a1", "a2");
    }

    [TestMethod]
    public async Task TheResourcesTable_CarriesOneIndexPerLevel()
    {
        await using var db = await FreshDatabase.CreateAsync();
        await using var app = db.Open<DocsContext>();
        await SetUpAsync(app);

        var levels = SqlOSFgaLineage.Levels(new SqlOSFgaOptions());
        var indexes = (await TableObjectsAsync(app, "SqlOSFgaResources", "dbo")).Where(n => n.Contains("Ancestor", StringComparison.Ordinal));
        indexes.Should().BeEquivalentTo(Enumerable.Range(0, levels).Select(l => SqlOSFgaLineage.AncestorIndexName("SqlOSFgaResources", l)));
    }

    [TestMethod]
    public async Task AProtectedTableIsRenamed_NothingOfSqlOSsFollows_AndTheFilterWorks()
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

        // The application's own objects, under the names its migrations gave them; nothing of SqlOS's to move.
        (await TableObjectsAsync(renamed, "StDocsRenamed")).Should().BeEquivalentTo(["PK_StDocs", "IX_StDocs_Rank", "IX_StDocs_SqlOSFgaResourceId"]);

        var alice = await AliceAsync(renamed);
        await AddDocsAsync(renamed, ("a2", "folder_a"));
        (await VisibleAsync(renamed, alice)).Should().Equal("a1", "a2");

        // A move changes the resource's lineage, which the filter reads; the table is not involved.
        await MoveAsync(renamed, "doc::a2", "folder_b");
        (await VisibleAsync(renamed, alice)).Should().Equal("a1");
    }

    [TestMethod]
    public async Task AnEntityNoLongerHasAResourceId_ResourceWritesNeverTouchedItsTable()
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
        plain.Docs.Add(new StPlainDoc { Id = "p1", ResourceId = "doc::p1" });
        await plain.SaveChangesAsync();

        // Resource writes go on without the table.
        await MoveAsync(plain, "folder_a", "root");
        plain.Set<SqlOSFgaResource>().Remove(await plain.Set<SqlOSFgaResource>().SingleAsync(r => r.Id == "doc::a1"));
        await plain.SaveChangesAsync();
    }

    [TestMethod]
    public async Task AnotherSqlOSInstallation_InTheSameDatabase_LeavesThisOneAlone()
    {
        // Two SqlOS installations, each in its own schema, share a database.
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

        // The first installation's filter reads its own resources: a row pointed at a resource Alice cannot see
        // stops being visible to her.
        (await VisibleAsync(app, alice)).Should().Equal("a1");
        var doc = await app.Set<StDoc>().SingleAsync(d => d.Id == "a1");
        doc.ResourceId = "doc::b1";
        await app.SaveChangesAsync();
        app.ChangeTracker.Clear();
        (await VisibleAsync(app, alice)).Should().BeEmpty();
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

        await using var connection = TestDatabase.CreateConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Snapshot);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE [dbo].[SqlOSFgaResources] SET [IsActive] = 0 WHERE [Id] = 'folder_a';";

        var act = () => command.ExecuteNonQueryAsync();
        (await act.Should().ThrowAsync<DbException>()).Which.Message.Should().Contain("not SNAPSHOT");
    }

    [TestMethod]
    public async Task PostgreSql_TheListAndTheRowCheckAreInlined_AndTheSetIsNot()
    {
        // fn_ListVisible, fn_AccessRoots and fn_IsResourceAccessible are SQL functions the planner inlines into the
        // statement that uses them; fn_VisibleSet is PL/pgSQL on purpose, a small set the statement starts from.
        if (!TestDatabase.IsPostgreSql)
        {
            return;
        }

        await using var db = await FreshDatabase.CreateAsync();
        await using var app = db.Open<DocsContext>();
        await SetUpAsync(app);
        var (alice, _) = await CreateFoldersAndAliceAsync(app);
        await AddDocsAsync(app, ("a1", "folder_a"), ("b1", "folder_b"));
        var subjects = JsonSerializer.Serialize(new[] { alice });

        var count = await ExplainAsync(db, """SELECT count(*) FROM (SELECT 1 FROM "dbo"."fn_ListVisible"(@subjects, @permission, @type) LIMIT 1000) c""", subjects);
        count.Should().NotContain("Function Scan", count);
        count.Should().Contain("SqlOSFgaGrants", "the roots are planned as part of the statement");

        var rowCheck = await ExplainAsync(db, """SELECT d."Id" FROM "StDocs" d WHERE EXISTS (SELECT 1 FROM "dbo"."fn_IsResourceAccessible"(d."ResourceId", @subjects, @permission) f) ORDER BY d."Id" LIMIT 20""", subjects);
        rowCheck.Should().NotContain("Function Scan", rowCheck);

        var listed = await ExplainAsync(db, """SELECT d."Id" FROM "StDocs" d WHERE EXISTS (SELECT 1 FROM "dbo"."fn_VisibleSet"(@subjects, @permission, @type) v WHERE v."ResourceId" = d."ResourceId") ORDER BY d."Id" LIMIT 20""", subjects);
        listed.Should().Contain("Function Scan on \"fn_VisibleSet\"", listed);
    }

    private static async Task<string> ExplainAsync(FreshDatabase db, string sql, string subjects)
    {
        await using var connection = TestDatabase.CreateConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN " + sql;
        TestDatabase.AddParameter(command, "@subjects", subjects);
        TestDatabase.AddParameter(command, "@permission", ReadPermissionId);
        TestDatabase.AddParameter(command, "@type", DocumentType);
        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(0));
        }

        return string.Join("\n", plan);
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
            Permissions = [new SqlOSFgaPermission { Id = ReadPermissionId, Key = Read, Name = "Read documents", ResourceTypeId = DocumentType }],
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

    /// <summary>The triggers and indexes of a table.</summary>
    private static async Task<List<string>> TableObjectsAsync(DbContext app, string table, string? schema = null)
    {
        var sql = TestDatabase.IsPostgreSql
            ? $"""
              SELECT t.tgname::text FROM pg_trigger t INNER JOIN pg_class c ON c.oid = t.tgrelid INNER JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE NOT t.tgisinternal AND c.relname = '{table}' AND n.nspname = {(schema is null ? "current_schema()" : $"'{schema}'")}
              UNION ALL SELECT indexname::text FROM pg_indexes WHERE tablename = '{table}' AND schemaname = {(schema is null ? "current_schema()" : $"'{schema}'")}
              """
            : $"""
              SELECT name FROM sys.triggers WHERE parent_id = OBJECT_ID(N'{(schema is null ? "" : $"[{schema}].")}[{table}]')
              UNION ALL SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(N'{(schema is null ? "" : $"[{schema}].")}[{table}]') AND name IS NOT NULL
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

    /// <summary>A second SqlOS installation in its own schema.</summary>
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
