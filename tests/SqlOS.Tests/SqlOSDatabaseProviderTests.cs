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
