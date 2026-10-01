using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;

namespace SqlOS.Tests;

[TestClass]
public class SqlOSDatabaseProviderTests
{
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
        sql.Should().Contain("strpos");
        sql.Should().Contain("truncated.\"Depth\" = 7");
        // The roots are read as an array from an uncorrelated subquery, which PostgreSQL evaluates once per query.
        sql.Should().Contain("= ANY (ARRAY(SELECT roots.\"ResourceId\" FROM \"dbo\".\"fn_AccessRoots\"(p_subject_ids, p_permission_id) roots))");
    }

    [TestMethod]
    public void PostgreSqlClosureSql_UsesStatementTriggersWithTransitionTables()
    {
        var options = new SqlOSFgaOptions { Schema = "ten\"ant", MaxResourceHierarchyDepth = 5 };
        options.TableNames.Resources = "res\"ources";

        var batches = PostgreSqlDatabaseProvider.Instance.BuildResourceClosureMaintenanceSql(options);
        var all = string.Join("\n", batches);

        batches.Should().HaveCount(6);
        all.Should().Contain("\"ten\"\"ant\".\"fn_res\"\"ourcesClosure_Apply\"(p_ids varchar[], p_reject boolean)");
        // One walk per Apply: the insert and the malformed count share the recursive CTE; no temp table on the insert path.
        all.Should().Contain("RETURNING 1");
        all.Should().Contain("CREATE TEMP TABLE \"SqlOSClosureOld\" ON COMMIT DROP AS");
        all.Should().Contain("LEFT JOIN pg_temp.\"SqlOSClosureOld\" o ON o.\"Id\" = r.\"Id\"");
        all.Should().Contain("\"ten\"\"ant\".\"fn_res\"\"ourcesClosure_Rebuild\"()");
        all.Should().Contain("REFERENCING NEW TABLE AS new_rows");
        all.Should().Contain("REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows");
        all.Should().Contain("REFERENCING OLD TABLE AS old_rows");
        all.Should().Contain("FOR EACH STATEMENT");
        all.Should().NotContain("AFTER UPDATE OF", "transition tables are not allowed on column-list triggers");
        all.Should().Contain("c.\"Depth\" <= 5");
        all.Should().Contain("\"Depth\" > 5");
        all.Should().Contain("ERRCODE = 'SQ011'");
        all.Should().Contain("ERRCODE = 'SQ012'");

        var page = PostgreSqlDatabaseProvider.Instance.BuildVisibleResourcesPageSql(options);
        page.Should().Contain("CROSS JOIN LATERAL");
        // The range read is bounded inside its own derived table, so the engine stops the index scan at k rows.
        page.Should().MatchRegex("ORDER BY c\\.\"DescendantSeq\"\\s+LIMIT @PageSize\\s+\\) c\\s+UNION ALL");
        page.Should().Contain("\"ten\"\"ant\".\"res\"\"ourcesClosure\"");
        page.Should().Contain("c.\"AncestorSeq\" = a.\"ResourceSeq\" AND c.\"TypeSeq\" = rt.\"Seq\" AND c.\"DescendantSeq\" > @Cursor");
        page.Should().Contain("rt.\"Id\" = @ResourceTypeId");
        page.Should().Contain("s.\"AncestorSeq\" = a.\"ParentSeq\" AND s.\"TypeSeq\" = a.\"TypeSeq\" AND s.\"DescendantSeq\" = a.\"ResourceSeq\"");
        page.Should().NotContain("WITH ", "the page composes under EF Core as a subquery, which a CTE cannot");

        var hash = PostgreSqlDatabaseProvider.Instance.BuildSelectRoutinesHashSql(options);
        hash.Should().Contain("\"ten\"\"ant\".\"SqlOSFgaSchema\"");
        hash.Should().Contain("p.proname = 'fn_AccessRoots'");
        hash.Should().Contain("p.proname = 'fn_res\"ourcesClosure_Rebuild'");
        hash.Should().Contain("t.tgname = 'TR_res\"ourcesClosure_Update'");
        hash.Should().Contain("c.relname = 'res\"ources'");
    }

    [TestMethod]
    public void SqlServerVisiblePageSql_ReadsOneRangePerRoot()
    {
        var page = SqlServerDatabaseProvider.Instance.BuildVisibleResourcesPageSql(new SqlOSFgaOptions());

        page.Should().Contain("CROSS APPLY");
        page.Should().Contain("SELECT TOP (@PageSize) c.DescendantSeq");
        // The range read is bounded inside its own derived table, so the engine stops the index scan at k rows.
        page.Should().MatchRegex("ORDER BY c\\.DescendantSeq\\s+\\) c\\s+UNION ALL");
        page.Should().Contain("c.AncestorSeq = a.ResourceSeq AND c.TypeSeq = rt.Seq AND c.DescendantSeq > @Cursor");
        page.Should().Contain("UNION ALL");
        page.Should().Contain("SELECT DISTINCT v.Seq");
        page.Should().Contain("rt.Id = @ResourceTypeId");
        page.Should().Contain("a.ParentSeq IS NULL OR a.ParentIsActive = 0 OR EXISTS");
        page.Should().Contain("s.AncestorSeq = a.ParentSeq AND s.TypeSeq = a.TypeSeq AND s.DescendantSeq = a.ResourceSeq");
        page.Should().Contain("ORDER BY d.Seq");
        page.Should().NotContain("WITH ", "the page composes under EF Core as a subquery, which a CTE cannot");
    }

    [TestMethod]
    public void SqlServerRoutinesHashSql_RequiresEveryRoutine()
    {
        var options = new SqlOSFgaOptions { Schema = "ten'ant" };
        options.TableNames.Resources = "res]ources";

        var hash = SqlServerDatabaseProvider.Instance.BuildSelectRoutinesHashSql(options);

        hash.Should().Contain("FROM [ten'ant].[SqlOSFgaSchema]");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_AccessRoots]', N'IF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[fn_IsResourceAccessible]', N'IF') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[sp_res]]ourcesClosure_Apply]', N'P') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[sp_res]]ourcesClosure_Rebuild]', N'P') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[TR_res]]ourcesClosure_Insert]', N'TR') IS NOT NULL");
        hash.Should().Contain("OBJECT_ID(N'[ten''ant].[TR_res]]ourcesClosure_Delete]', N'TR') IS NOT NULL");
        SqlServerDatabaseProvider.Instance.BuildStoreRoutinesHashSql(options)
            .Should().Be("UPDATE [ten'ant].[SqlOSFgaSchema] SET [RoutinesHash] = @RoutinesHash");
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
