using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;

namespace SqlOS.Tests;

[TestClass]
public class SqlOSDatabaseProviderTests
{
    [TestMethod]
    public void SqlServerSessionLock_VolunteersAsDeadlockVictim_WhileHeld()
    {
        // The routines' DDL under the lock can deadlock with queries running beside the initializer. The
        // locked session must lose that deadlock (and retry), never the query.
        var acquire = SqlServerDatabaseProvider.BuildAcquireSessionLockSql(TimeSpan.FromSeconds(30), "Could not acquire the 'lock'.");

        acquire.Should().StartWith("SET DEADLOCK_PRIORITY LOW;");
        acquire.Should().Contain("@LockOwner = 'Session'");
        acquire.Should().Contain("@LockTimeout = 30000");
        acquire.Should().Contain("THROW 51000, 'Could not acquire the ''lock''.', 1;");
        SqlServerDatabaseProvider.ReleaseSessionLockSql.Should().Contain("sp_releaseapplock");
        SqlServerDatabaseProvider.ReleaseSessionLockSql.Should().EndWith("SET DEADLOCK_PRIORITY NORMAL;");
    }

    [TestMethod]
    public void MigrationManifest_IsProviderComplete()
    {
        var act = () => SqlOSMigrationManifest.EnsureProviderComplete(typeof(SqlOSDatabase).Assembly);
        act.Should().NotThrow();
    }

    [TestMethod]
    public void PostgreSqlAuthMigrations_SkipMissingTables()
    {
        var assembly = typeof(SqlOSDatabase).Assembly;
        var prefix = PostgreSqlDatabaseProvider.Instance.AuthMigrationResourcePrefix;
        foreach (var resourceName in assembly.GetManifestResourceNames().Where(x => x.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(resourceName);
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            foreach (var line in sql.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("ALTER TABLE \"", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("ALTER TABLE IF EXISTS", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{resourceName} has an unguarded ALTER TABLE: {trimmed}");
                }
            }
        }
    }

    [TestMethod]
    public void Resolve_UnknownProvider_FailsClosed()
    {
        var act = () => SqlOSDatabase.Resolve("Microsoft.EntityFrameworkCore.Sqlite");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*UseSqlServer*")
            .WithMessage("*UseNpgsql*");
    }

    [TestMethod]
    public void Resolve_NullProvider_FailsClosed()
    {
        var act = () => SqlOSDatabase.Resolve((string?)null);
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void PostgreSqlFunctionSql_UsesReplaceableTableFunction()
    {
        var sql = PostgreSqlDatabaseProvider.Instance.BuildIsResourceAccessibleFunctionSql(
            new SqlOSFgaOptions { MaxResourceHierarchyDepth = 7 });

        sql.Should().Contain("CREATE OR REPLACE FUNCTION");
        sql.Should().Contain("fn_IsResourceAccessible");
        sql.Should().Contain("RETURNS TABLE(\"Id\"");
        sql.Should().Contain("CROSS JOIN LATERAL (VALUES (0, x.\"Ancestor0\"), (1, x.\"Ancestor1\")");
        sql.Should().Contain("(7, x.\"Ancestor7\")) AS lv(\"Level\", \"Seq\")");
        sql.Should().NotContain("Ancestor8");
        sql.Should().Contain("lv.\"Level\" >= x.\"Reach\"");
        sql.Should().Contain("g.\"SubjectId\" = ANY (ARRAY(SELECT live.\"SubjectId\" FROM \"dbo\".\"fn_ActiveSubjects\"(p_subject_ids) live))");
        sql.Should().Contain("permission.\"ResourceTypeId\" IS NULL OR permission.\"ResourceTypeId\" = x.\"ResourceTypeId\"");
    }

    [TestMethod]
    public void PostgreSqlAccessRootsSql_ReadsTheGrantsFirst()
    {
        var sql = PostgreSqlDatabaseProvider.Instance.BuildAccessRootsFunctionSql(new SqlOSFgaOptions());

        sql.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_AccessRoots\"");
        sql.Should().Contain("RETURNS TABLE(\"ResourceSeq\" bigint, \"Depth\" smallint)");
        sql.Should().Contain("WITH g AS MATERIALIZED");
        sql.Should().Contain("r.\"IsActive\" = TRUE AND r.\"Depth\" IS NOT NULL");
        PostgreSqlDatabaseProvider.Instance.BuildAccessRootsQuerySql(new SqlOSFgaOptions())
            .Should().Be("SELECT a.\"ResourceSeq\", a.\"Depth\" FROM \"dbo\".\"fn_AccessRoots\"({0}, {1}) AS a");
    }

    [TestMethod]
    public void PostgreSqlLineageSql_UsesStatementTriggersWithTransitionTables()
    {
        var options = new SqlOSFgaOptions { Schema = "ten\"ant", MaxResourceHierarchyDepth = 4 };
        options.TableNames.Resources = "res\"ources";
        var scope = new SqlOSFgaScopeTable("app", "Items", "ResourceId", ["Id"]);

        var columns = PostgreSqlDatabaseProvider.Instance.BuildEnsureLineageColumnsSql(options).Single();
        columns.Should().Contain("ALTER TABLE \"ten\"\"ant\".\"res\"\"ources\" ADD COLUMN IF NOT EXISTS \"Ancestor4\" bigint NULL;");
        columns.Should().Contain("CREATE INDEX IF NOT EXISTS \"IX_res\"\"ources_Ancestor4\" ON \"ten\"\"ant\".\"res\"\"ources\" (\"Ancestor4\") INCLUDE (\"Reach\") WHERE \"Ancestor4\" IS NOT NULL;");
        columns.Should().NotContain("Ancestor5");

        var batches = PostgreSqlDatabaseProvider.Instance.BuildLineageMaintenanceSql(options, [scope]);
        var all = string.Join("\n", batches);
        all.Should().Contain("CREATE OR REPLACE FUNCTION \"ten\"\"ant\".\"fn_res\"\"ources_LineageRefresh\"(p_ids varchar[], p_reject boolean)");
        all.Should().Contain("CREATE OR REPLACE FUNCTION \"ten\"\"ant\".\"fn_res\"\"ources_LineageRebuild\"()");
        all.Should().Contain("CREATE TEMP TABLE \"SqlOSLineageNodes\" AS");
        all.Should().Contain("WHERE EXISTS (SELECT 1 FROM \"ten\"\"ant\".\"res\"\"ources\" c WHERE c.\"ParentId\" = r.\"Id\")");
        all.Should().Contain("CREATE TEMP TABLE \"SqlOSLineageAffected\"");
        all.Should().Contain("AFTER INSERT ON \"ten\"\"ant\".\"res\"\"ources\"\n    REFERENCING NEW TABLE AS new_rows\n    FOR EACH STATEMENT");
        all.Should().Contain("AFTER UPDATE ON \"ten\"\"ant\".\"res\"\"ources\"\n    REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows");
        all.Should().Contain("AFTER DELETE ON \"ten\"\"ant\".\"res\"\"ources\"");
        all.Should().Contain("WHERE \"Steps\" > 4");
        all.Should().Contain("p.\"Depth\" = 4");
        all.Should().Contain("USING ERRCODE = 'SQ012'");
        all.Should().Contain("\"Ancestor4\" = CASE WHEN nd.\"Depth\" = 4 THEN n.\"Seq\" WHEN nd.\"Depth\" > 4 THEN p.\"Ancestor4\" ELSE NULL END");
        all.Should().Contain("CREATE OR REPLACE FUNCTION \"ten\"\"ant\".\"fn_SqlOSFgaScope_app_Items_Insert\"()");
        all.Should().Contain("CREATE OR REPLACE FUNCTION \"ten\"\"ant\".\"fn_SqlOSFgaScope_app_Items_Update\"()");
        all.Should().Contain("SELECT \"Id\", \"ParentId\", \"IsActive\" FROM new_rows\n        EXCEPT\n        SELECT \"Id\", \"ParentId\", \"IsActive\" FROM old_rows", "changed rows come from a hashed set operation");
        all.Should().Contain("SELECT \"Id\", \"ResourceId\" FROM new_rows\n            EXCEPT\n            SELECT \"Id\", \"ResourceId\" FROM old_rows");
        all.Should().NotContain("JOIN old_rows", "a join of the transition tables has nothing to plan by");
        all.Should().Contain("IF NOT EXISTS (", "the update function must leave before updating, or its own update fires it forever");
        all.Should().Contain("AFTER UPDATE ON \"app\".\"Items\"");
        all.Should().Contain("\"SqlOSFgaAncestor4\" = r.\"Ancestor4\", \"SqlOSFgaReach\" = r.\"Reach\", \"SqlOSFgaTypeSeq\" = rt.\"Seq\"");

        var hash = PostgreSqlDatabaseProvider.Instance.BuildSelectRoutinesHashSql(options, [scope]);
        hash.Should().Contain("p.proname = 'fn_ActiveSubjects'");
        hash.Should().Contain("p.proname = 'fn_AccessRoots'");
        hash.Should().Contain("p.proname = 'fn_res\"ources_LineageRebuild'");
        hash.Should().Contain("t.tgname = 'TR_res\"ources_Lineage_Update'");
        hash.Should().Contain("n.nspname = 'app' AND c.relname = 'Items' AND t.tgname = 'TR_Items_SqlOSFgaScope_Insert'");
        hash.Should().Contain("column_name = 'Ancestor4'");
    }

    [TestMethod]
    public void PostgreSqlRateLimitSql_TreatsAdvisoryLockAsBoolean()
    {
        var increment = PostgreSqlDatabaseProvider.Instance.BuildRateLimitIncrementSql("dbo");
        increment.Should().Contain("SqlOSRateLimitBuckets");
        increment.Should().NotContain("pg_advisory_xact_lock");
        increment.Should().NotContain("FOR UPDATE");

        var reserveMany = PostgreSqlDatabaseProvider.Instance.BuildRateLimitReserveManySql("dbo", 3);
        reserveMany.Should().Contain("ON CONFLICT");
        reserveMany.Should().NotContain("pg_advisory_xact_lock");
        reserveMany.Should().NotContain("FOR UPDATE");
    }

    [TestMethod]
    public void CompositeKeySeparator_KeepsNulOnSqlServerAndUsesUnitSeparatorOnPostgreSql()
    {
        SqlOSDatabase.CompositeKeySeparator(SqlOSDatabase.SqlServerProviderName).Should().Be('\0');
        SqlOSDatabase.CompositeKeySeparator(SqlOSDatabase.InMemoryProviderName).Should().Be('\0');
        SqlOSDatabase.CompositeKeySeparator(null).Should().Be('\0');
        SqlOSDatabase.CompositeKeySeparator(SqlOSDatabase.PostgreSqlProviderName).Should().Be('\u001F');
    }

    [TestMethod]
    public void IsPostgreSql_DetectsProviderFromDbContextOptions()
    {
        var sqlServer = new DbContextOptionsBuilder()
            .UseSqlServer("Server=.;Database=SqlOS_ProviderDetect;Trusted_Connection=True;TrustServerCertificate=True");
        var postgreSql = new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Database=sqlos_provider_detect;Username=sqlos;Password=sqlos");
        var inMemory = new DbContextOptionsBuilder()
            .UseInMemoryDatabase("sqlos-provider-detect");

        SqlOSDatabase.IsPostgreSql(sqlServer).Should().BeFalse();
        SqlOSDatabase.IsPostgreSql(postgreSql).Should().BeTrue();
        SqlOSDatabase.IsPostgreSql(inMemory).Should().BeFalse();
    }

    [TestMethod]
    public void UseSqlOS_OnPostgreSql_RewritesOnlySqlOSAssemblyDateTimeColumns()
    {
        var options = new DbContextOptionsBuilder<HostDateTimeTestDbContext>()
            .UseSqlServer("Server=.;Database=SqlOS_DateTimeRewrite;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        using var context = new HostDateTimeTestDbContext(options);

        context.Model.FindEntityType(typeof(HostDateTimeRow))!
            .FindProperty(nameof(HostDateTimeRow.CreatedAt))!
            .GetColumnType()
            .Should().NotBe("timestamp without time zone");

        context.Model.FindEntityType(typeof(SqlOSFgaResource))!
            .FindProperty(nameof(SqlOSFgaResource.CreatedAt))!
            .GetColumnType()
            .Should().Be("timestamp without time zone");
    }

    [TestMethod]
    public void ModelSql_UsesProviderSpecificFilters()
    {
        SqlOSModelSql.IsNotNull(SqlOSDatabase.SqlServerProviderName, "SeedKey")
            .Should().Be("[SeedKey] IS NOT NULL");
        SqlOSModelSql.IsNotNull(SqlOSDatabase.PostgreSqlProviderName, "SeedKey")
            .Should().Be("\"SeedKey\" IS NOT NULL");
        SqlOSModelSql.EqualsTrue(SqlOSDatabase.PostgreSqlProviderName, "IsEnabled")
            .Should().Be("\"IsEnabled\" = TRUE");
        SqlOSModelSql.IsNull(SqlOSDatabase.PostgreSqlProviderName, "RevokedAt")
            .Should().Be("\"RevokedAt\" IS NULL");
    }
}

file sealed class HostDateTimeTestDbContext(DbContextOptions<HostDateTimeTestDbContext> options)
    : DbContext(options)
{
    public DbSet<HostDateTimeRow> HostRows => Set<HostDateTimeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HostDateTimeRow>();
        modelBuilder.UseSqlOS(GetType(), SqlOSDatabase.PostgreSqlProviderName);
    }
}

// Lives in SqlOS.Tests so a namespace prefix of "SqlOS" would still rewrite it.
file sealed class HostDateTimeRow
{
    public string Id { get; set; } = "host-1";
    public DateTime CreatedAt { get; set; }
}
