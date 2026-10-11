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
    public void PostgreSqlFunctionSql_ReturnsTheDecidingGrant()
    {
        var sql = PostgreSqlDatabaseProvider.Instance.BuildIsResourceAccessibleFunctionSql(
            new SqlOSFgaOptions { MaxResourceHierarchyDepth = 7 });

        // The result names the deciding grant, a different result type than 7.x returned, which CREATE OR
        // REPLACE cannot change: a function with the old shape is dropped first, in the same batch. The
        // current shape is replaced in place, never dropped, so sessions calling it keep working.
        sql.Should().Contain("NOT LIKE '%\"GrantId\"%'");
        sql.Should().Contain("DROP FUNCTION \"dbo\".\"fn_IsResourceAccessible\"(varchar, text, varchar);");
        sql.Should().NotContain("DROP FUNCTION IF EXISTS");
        sql.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_IsResourceAccessible\"");
        sql.Should().Contain("RETURNS TABLE(\"Id\" varchar(450), \"GrantId\" varchar(450), \"SubjectId\" varchar(450), \"RoleId\" varchar(450), \"Level\" integer)");
        sql.Should().Contain("ORDER BY lv.\"Level\" DESC\nLIMIT 1");
        sql.Should().Contain("CROSS JOIN LATERAL (VALUES (0, x.\"Ancestor0\"), (1, x.\"Ancestor1\")");
        sql.Should().Contain("(7, x.\"Ancestor7\")) AS lv(\"Level\", \"Seq\")");
        sql.Should().NotContain("Ancestor8");
        sql.Should().Contain("lv.\"Level\" >= x.\"Reach\"");
        // One exact seek per ancestor and live subject on (ResourceSeq, SubjectId); OFFSET 0 keeps it a seek, not
        // a read of every grant on the ancestor.
        sql.Should().Contain("CROSS JOIN LATERAL unnest(ARRAY(SELECT live.\"SubjectId\" FROM \"dbo\".\"fn_ActiveSubjects\"(p_subject_ids) live)) AS s(\"SubjectId\")");
        sql.Should().Contain("WHERE g.\"ResourceSeq\" = lv.\"Seq\" AND g.\"SubjectId\" = s.\"SubjectId\"\n    OFFSET 0\n) g");
        sql.Should().Contain("SELECT g.\"ResourceId\", g.\"Id\", g.\"SubjectId\", g.\"RoleId\", lv.\"Level\"");
        sql.Should().Contain("permission.\"ResourceTypeId\" IS NULL OR permission.\"ResourceTypeId\" = x.\"ResourceTypeId\"");
    }

    [TestMethod]
    public void PostgreSqlAccessRootsSql_ReadsTheGrantsFirst()
    {
        var sql = PostgreSqlDatabaseProvider.Instance.BuildAccessRootsFunctionSql(new SqlOSFgaOptions());

        sql.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_AccessRoots\"");
        sql.Should().Contain("RETURNS TABLE(\"ResourceSeq\" bigint, \"Depth\" smallint)");
        sql.Should().Contain("WITH g AS MATERIALIZED");
        sql.Should().NotContain("DISTINCT", "a count up to a cap reads only the first roots it needs");
        sql.Should().Contain("CROSS JOIN LATERAL (\n    SELECT x.\"Seq\", x.\"Depth\"\n    FROM \"dbo\".\"SqlOSFgaResources\" x\n    WHERE x.\"Id\" = g.\"ResourceId\" AND x.\"IsActive\" = TRUE AND x.\"Depth\" IS NOT NULL\n    LIMIT 1\n) r");
    }

    [TestMethod]
    public void PostgreSqlListSql_ListsRootByRoot_TheSetIsOpaque_AndListFirstCountsUpToTheCap()
    {
        var provider = PostgreSqlDatabaseProvider.Instance;
        var options = new SqlOSFgaOptions { MaxResourceHierarchyDepth = 2 };

        // Inlinable (LANGUAGE sql, STABLE, one SELECT); the lateral join keeps the roots driving.
        var list = provider.BuildListVisibleFunctionSql(options);
        list.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_ListVisible\"(");
        list.Should().Contain("RETURNS TABLE(\"ResourceId\" varchar(450))");
        list.Should().Contain("LANGUAGE sql\nSTABLE");
        list.Should().Contain("FROM \"dbo\".\"fn_AccessRoots\"(p_subject_ids, p_permission_id) a\nCROSS JOIN LATERAL (");
        for (var level = 0; level <= 2; level++)
        {
            list.Should().Contain($"SELECT r.\"Id\" FROM \"dbo\".\"SqlOSFgaResources\" r WHERE a.\"Depth\" = {level} AND r.\"Ancestor{level}\" = a.\"ResourceSeq\" AND r.\"Ancestor{level}\" IS NOT NULL AND r.\"Reach\" <= {level} AND (p_type_id IS NULL OR r.\"ResourceTypeId\" = p_type_id)");
        }

        list.Should().NotContain("Ancestor3");
        // A function with a SET clause is never inlined; the list must be, inside the set and the count.
        list.Should().NotContain("SET jit");

        // PL/pgSQL, never inlined: the planner sees a small set and starts the statement from it. Without JIT:
        // an over-costed list of a few rows would compile for longer than it runs.
        var set = provider.BuildVisibleSetFunctionSql(options);
        set.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_VisibleSet\"(");
        set.Should().Contain("LANGUAGE plpgsql\nSTABLE\nROWS 10\nSET jit = off");
        set.Should().Contain("SELECT DISTINCT l.\"ResourceId\" FROM \"dbo\".\"fn_ListVisible\"(p_subject_ids, p_permission_id, p_type_id) l;");

        var listFirst = provider.BuildListFirstFunctionSql(options);
        listFirst.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_ListFirst\"(");
        listFirst.Should().Contain("RETURNS TABLE(\"ListFirst\" boolean)");
        listFirst.Should().Contain("LANGUAGE sql\nSTABLE\nSET jit = off");
        listFirst.Should().Contain("CASE WHEN 8 * sqrt(greatest(coalesce((SELECT t.reltuples FROM pg_class t WHERE t.oid = to_regclass(p_table)), 0), 0)::float8) > 1000");
        listFirst.Should().Contain("FROM (SELECT 1 FROM \"dbo\".\"fn_ListVisible\"(p_subject_ids, p_permission_id, p_type_id) LIMIT c.cap) t");
        listFirst.Should().Contain("SELECT v.visible < c.cap");

        var checkRow = provider.BuildCheckRowFunctionSql(options);
        checkRow.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_CheckRow\"(");
        checkRow.Should().Contain("LANGUAGE sql\nSTABLE");
        checkRow.Should().Contain("SELECT TRUE FROM \"dbo\".\"fn_IsResourceAccessible\"(p_resource_id, p_subject_ids, p_permission_id)");

        provider.BuildListFirstQuerySql(options).Should().Be(
            "SELECT f.\"ListFirst\" AS \"Value\" FROM \"dbo\".\"fn_ListFirst\"(CAST({0} AS text), CAST({1} AS varchar(450)), CAST({2} AS varchar(450)), CAST({3} AS text)) AS f");
    }

    [TestMethod]
    public void PostgreSqlLineageSql_UsesStatementTriggersWithTransitionTables()
    {
        var options = new SqlOSFgaOptions { Schema = "ten\"ant", MaxResourceHierarchyDepth = 4 };
        options.TableNames.Resources = "res\"ources";

        var ensure = PostgreSqlDatabaseProvider.Instance.BuildEnsureLineageColumnsSql(options);
        ensure.Should().HaveCount(2, "the columns, then the indexes over them");
        ensure[0].Should().Contain("ALTER TABLE \"ten\"\"ant\".\"res\"\"ources\" ADD COLUMN IF NOT EXISTS \"Ancestor4\" bigint NULL;");
        ensure[0].Should().NotContain("Ancestor5");
        ensure[1].Should().Contain("CREATE INDEX IF NOT EXISTS \"IX_res\"\"ources_Ancestor4\" ON \"ten\"\"ant\".\"res\"\"ources\" (\"Ancestor4\", \"ResourceTypeId\") INCLUDE (\"Reach\", \"Id\") WHERE \"Ancestor4\" IS NOT NULL;");
        ensure[1].Should().NotContain("Ancestor5");

        var batches = PostgreSqlDatabaseProvider.Instance.BuildLineageMaintenanceSql(options);
        var all = string.Join("\n", batches);
        all.Should().Contain("CREATE OR REPLACE FUNCTION \"ten\"\"ant\".\"fn_res\"\"ources_LineageRefresh\"(p_ids varchar[], p_reject boolean)");
        all.Should().Contain("CREATE OR REPLACE FUNCTION \"ten\"\"ant\".\"fn_res\"\"ources_LineageRebuild\"()");
        all.Should().Contain("CREATE TEMP TABLE \"SqlOSLineageNodes\" AS");
        all.Should().Contain("WHERE EXISTS (SELECT 1 FROM \"ten\"\"ant\".\"res\"\"ources\" c WHERE c.\"ParentId\" = r.\"Id\")");
        all.Should().Contain("CREATE TEMP TABLE \"SqlOSLineageAffected\"");
        all.Should().Contain("AFTER INSERT ON \"ten\"\"ant\".\"res\"\"ources\"\n    REFERENCING NEW TABLE AS new_rows\n    FOR EACH STATEMENT");
        all.Should().Contain("AFTER UPDATE ON \"ten\"\"ant\".\"res\"\"ources\"\n    REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows");
        all.Should().NotContain("AFTER DELETE", "a resource with children cannot be deleted, and a leaf is in no other row's lineage");
        all.Should().Contain("BEFORE INSERT OR UPDATE OF \"ResourceId\" ON \"ten\"\"ant\".\"SqlOSFgaGrants\"\n    FOR EACH ROW EXECUTE FUNCTION \"ten\"\"ant\".\"fn_SqlOSFgaGrants_ResourceSeq\"();");
        all.Should().Contain("NEW.\"ResourceSeq\" := (SELECT r.\"Seq\" FROM \"ten\"\"ant\".\"res\"\"ources\" r WHERE r.\"Id\" = NEW.\"ResourceId\");");
        all.Should().Contain("WHERE r.\"Id\" = g.\"ResourceId\" AND g.\"ResourceSeq\" IS DISTINCT FROM r.\"Seq\";", "the rebuild refreshes the grants' copy too");
        all.Should().Contain("WHERE \"Steps\" > 4");
        all.Should().Contain("p.\"Depth\" = 4");
        all.Should().Contain("USING ERRCODE = 'SQ012'");
        all.Should().Contain("\"Ancestor4\" = CASE WHEN nd.\"Depth\" = 4 THEN n.\"Seq\" WHEN nd.\"Depth\" > 4 THEN p.\"Ancestor4\" ELSE NULL END");
        all.Should().Contain("SELECT \"Id\", \"ParentId\", \"IsActive\" FROM new_rows\n        EXCEPT\n        SELECT \"Id\", \"ParentId\", \"IsActive\" FROM old_rows", "changed rows come from a hashed set operation");
        all.Should().NotContain("JOIN old_rows", "a join of the transition tables has nothing to plan by");
        all.Should().NotContain("FgaScope", "nothing of SqlOS's is on an application table");
        all.Should().NotContain("ResourceTypeId", "a retype changes no lineage");
        var key = SqlOSFgaLineage.LineageLockKey(options).ToString(System.Globalization.CultureInfo.InvariantCulture);
        all.Should().Contain($"PERFORM pg_advisory_xact_lock_shared({key});");
        all.Should().Contain($"PERFORM pg_advisory_xact_lock({key});");
        all.Should().Contain("IF current_setting('transaction_isolation') = 'repeatable read' THEN");

        var hash = PostgreSqlDatabaseProvider.Instance.BuildSelectRoutinesHashSql(options);
        hash.Should().Contain("p.proname = 'fn_ActiveSubjects'");
        hash.Should().Contain("p.proname = 'fn_AccessRoots'");
        hash.Should().Contain("p.proname = 'fn_ListVisible'");
        hash.Should().Contain("p.proname = 'fn_VisibleSet'");
        hash.Should().Contain("p.proname = 'fn_ListFirst'");
        hash.Should().Contain("p.proname = 'fn_CheckRow'");
        hash.Should().Contain("p.proname = 'fn_res\"ources_LineageRebuild'");
        hash.Should().Contain("t.tgname = 'TR_res\"ources_Lineage_Update'");
        hash.Should().Contain("c.relname = 'SqlOSFgaGrants' AND t.tgname = 'TR_SqlOSFgaGrants_ResourceSeq'");
        hash.Should().Contain("p.proname = 'fn_SqlOSFgaGrants_ResourceSeq'");
        hash.Should().Contain("column_name = 'Ancestor4'");
        hash.Should().Contain("indexname IN ('IX_res\"ources_Ancestor0', 'IX_res\"ources_Ancestor1', 'IX_res\"ources_Ancestor2', 'IX_res\"ources_Ancestor3', 'IX_res\"ources_Ancestor4')) = 5");
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
        modelBuilder.UseSqlOS(SqlOSDatabase.PostgreSqlProviderName);
    }
}

// Lives in SqlOS.Tests so a namespace prefix of "SqlOS" would still rewrite it.
file sealed class HostDateTimeRow
{
    public string Id { get; set; } = "host-1";
    public DateTime CreatedAt { get; set; }
}
