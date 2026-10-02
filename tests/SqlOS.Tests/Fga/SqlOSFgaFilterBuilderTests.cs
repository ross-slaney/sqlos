using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Tests.Fga;

/// <summary>
/// The shape of the predicate <c>BuildFilterAsync</c> returns, as EF Core translates it for SQL Server (SQLite
/// prints the SQL here; the integration tests run the real thing on both engines). A root at a level compares
/// the level's eight bytes of the scope column, read with the expression of the computed column the level's
/// index is built on, against a parameter, under the comparison on the depth byte that is the index's
/// filter; several roots at a level, however many, are a membership test.
/// </summary>
[TestClass]
public class SqlOSFgaFilterBuilderTests
{
    [TestMethod]
    public void OneRootPerLevel_ComparesTheLevelsBytesWithAParameter_AndChecksLiveness()
    {
        using var context = Create();
        var filter = Build(context, [Root(5, 1), Root(9, 3)], typeSeq: 7);
        var sql = context.Set<Item>().Where(filter).ToQueryString();

        // The caller's subjects travel as a parameter (SQLite prints its value in a .param line), so every caller
        // shares the query's plan.
        sql.Should().MatchRegex(@"fn_ActiveSubjects""\((@\w+)\)");
        sql.Should().Contain(".param set @__p_0 '[\"u\"]'");
        sql.Should().Contain($"SUBSTRING(\"i\".\"FgaScope\", {SqlOSFgaLineage.ScopeAncestorOffset(1)}, 8) = @", "level 1");
        sql.Should().Contain($"SUBSTRING(\"i\".\"FgaScope\", {SqlOSFgaLineage.ScopeAncestorOffset(3)}, 8) = @", "level 3");
        sql.Should().Contain("\"i\".\"FgaScope\" >= X'01'", "the depth byte selects the level's filtered index");
        sql.Should().Contain("\"i\".\"FgaScope\" >= X'03'");
        sql.Should().Contain($"SUBSTRING(\"i\".\"FgaScope\", {SqlOSFgaLineage.ScopeTypeOffset}, 4) = @", "the permission's type");
        sql.Should().NotContain("SqlOSFgaResources", "no join: the lineage sits on the row");
        sql.Should().NotContain("fn_AccessRoots");
    }

    [TestMethod]
    public void SeveralRootsAtALevel_TestMembership()
    {
        using var context = Create();
        var filter = Build(context, [Root(5, 2), Root(6, 2), Root(7, 2)], typeSeq: null);
        var sql = context.Set<Item>().Where(filter).ToQueryString();

        // The roots travel as one collection parameter the level's bytes are tested against (SQLite spells the
        // test with json_each; SQL Server with OPENJSON).
        sql.Should().Contain($"SUBSTRING(\"i\".\"FgaScope\", {SqlOSFgaLineage.ScopeAncestorOffset(2)}, 8)");
        sql.Should().Contain("json_each(@__p_1)");
        sql.Should().Contain("\"i\".\"FgaScope\" >= X'02'");
        sql.Should().NotContain($"SUBSTRING(\"i\".\"FgaScope\", {SqlOSFgaLineage.ScopeTypeOffset}, 4)", "the permission applies to every type");
    }

    [TestMethod]
    public void ThousandsOfRoots_AreTheSameMembershipTest()
    {
        // Any number of grants is the same predicate: the roots at a level travel as one collection parameter.
        using var context = Create();
        var roots = Enumerable.Range(1, 5_000).Select(i => Root(i, 4)).Append(Root(9_999, 1)).ToList();
        var filter = Build(context, roots, typeSeq: 7);
        var sql = context.Set<Item>().Where(filter).ToQueryString();

        sql.Should().Contain($"SUBSTRING(\"i\".\"FgaScope\", {SqlOSFgaLineage.ScopeAncestorOffset(4)}, 8)");
        sql.Should().Contain("json_each(@__p_");
        sql.Should().Contain($"SUBSTRING(\"i\".\"FgaScope\", {SqlOSFgaLineage.ScopeAncestorOffset(1)}, 8) = @", "one root at level 1");
        sql.Should().Contain("fn_ActiveSubjects");
        sql.Should().NotContain("fn_IsResourceAccessible");
    }

    [TestMethod]
    public void AFilterBuiltOnOneContextInstance_ComposesIntoAnother()
    {
        // The filter carries no DbContext instance: SqlOS's functions are mapped to static methods. So a filter
        // built by the request's ISqlOSFgaAuthService composes into a query on a context from a factory or a pool.
        using var builtOn = Create();
        using var queried = Create();
        var filter = Build(builtOn, [Root(5, 1)], typeSeq: 7);

        var sql = queried.Set<Item>().Where(filter).ToQueryString();

        sql.Should().Contain("fn_ActiveSubjects");
        sql.Should().Contain($"SUBSTRING(\"i\".\"FgaScope\", {SqlOSFgaLineage.ScopeAncestorOffset(1)}, 8) = @");
    }

    [TestMethod]
    public void ARootDeeperThanTheConfiguredDepth_Fails()
    {
        using var context = Create();
        var act = () => Build(context, [Root(5, 11)], typeSeq: null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*MaxResourceHierarchyDepth*");
    }

    [TestMethod]
    public void ParameterBytes_AreBigEndian_LikeTheDatabasesCast()
    {
        // CAST(bigint AS BINARY(8)) and CAST(int AS BINARY(4)) write the most significant byte first.
        SqlOSFgaScope.Bytes(258L).Should().Equal(0, 0, 0, 0, 0, 0, 1, 2);
        SqlOSFgaScope.Bytes(258).Should().Equal(0, 0, 1, 2);

        var encoded = SqlOSFgaScope.Encode(depth: 2, typeSeq: 7, [1L, 20L, 300L]);
        var (depth, typeSeq, ancestors) = SqlOSFgaScope.Decode(encoded, levels: 3);
        depth.Should().Be(2);
        typeSeq.Should().Be(7);
        ancestors.Should().Equal(1L, 20L, 300L);
    }

    private static SqlOSFgaAccessRoot Root(long seq, short depth) => new() { ResourceSeq = seq, Depth = depth };

    private static System.Linq.Expressions.Expression<Func<Item, bool>> Build(FilterDbContext context, IReadOnlyList<SqlOSFgaAccessRoot> roots, int? typeSeq)
    {
        return SqlOSFgaFilterBuilder.Build<Item>(roots, "[\"u\"]", typeSeq, levels: 11);
    }

    private static FilterDbContext Create()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return new FilterDbContext(new DbContextOptionsBuilder<FilterDbContext>().UseSqlite(connection).Options);
    }

    private sealed class FilterDbContext(DbContextOptions<FilterDbContext> options) : DbContext(options), ISqlOSFgaDbContext
    {

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Item>(item =>
            {
                item.ToTable("Items");
                item.HasKey(i => i.Id);
            });

            // SQLite only prints the SQL here; the model is SQL Server's (a byte string scope column).
            modelBuilder.UseSqlOS(SqlOSDatabase.SqlServerProviderName, new SqlOSFgaOptions());
        }
    }

    private sealed class Item : IHasResourceId
    {
        public byte[]? FgaScope { get; private set; }

        public int Id { get; set; }
        public string ResourceId { get; set; } = string.Empty;
    }
}
