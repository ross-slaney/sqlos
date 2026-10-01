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
/// The shape of the predicate <c>BuildFilterAsync</c> returns, translated by EF Core (SQLite stands in for the
/// engines here; the integration tests run the real thing). One root at a level is an equality on a
/// parameter; several are a membership test; more than the list limit checks each row with the function.
/// </summary>
[TestClass]
public class SqlOSFgaFilterBuilderTests
{
    [TestMethod]
    public void OneRootPerLevel_ComparesTheAncestorWithAParameter_AndChecksLiveness()
    {
        using var context = Create(scopeColumns: false);
        var filter = Build(context, [Root(5, 1), Root(9, 3)], scoped: false, "project");
        var sql = context.Set<Item>().Where(filter).ToQueryString();

        sql.Should().Contain("EXISTS (");
        sql.Should().Contain("fn_ActiveSubjects");
        sql.Should().Contain("\"SqlOSFgaResources\"");
        sql.Should().Contain("\"Ancestor1\" = @");
        sql.Should().Contain("\"Ancestor3\" = @");
        sql.Should().Contain("\"Reach\" <= 1");
        sql.Should().Contain("\"Reach\" <= 3");
        sql.Should().Contain("\"ResourceTypeId\" = @");
        sql.Should().NotContain("fn_AccessRoots");
    }

    [TestMethod]
    public void SeveralRootsAtALevel_TestMembership()
    {
        using var context = Create(scopeColumns: false);
        var filter = Build(context, [Root(5, 2), Root(6, 2), Root(7, 2)], scoped: false, null);
        var sql = context.Set<Item>().Where(filter).ToQueryString();

        sql.Should().Contain("\"Ancestor2\" IN (");
        sql.Should().NotContain("\"ResourceTypeId\" = @", "the permission applies to every type");
    }

    [TestMethod]
    public void MoreRootsThanTheListLimit_CheckEachRowWithTheFunction()
    {
        using var context = Create(scopeColumns: false);
        var roots = Enumerable.Range(1, SqlOSFgaLineage.MaxListedRoots + 1).Select(i => Root(i, 4)).ToList();
        var filter = Build(context, roots, scoped: false, "project");
        var sql = context.Set<Item>().Where(filter).ToQueryString();

        sql.Should().Contain("fn_IsResourceAccessible");
        sql.Should().NotContain("Ancestor", "the row's ancestors are probed inside the function, never listed");
        sql.Should().NotContain("fn_ActiveSubjects", "the function checks the caller's liveness itself");
    }

    [TestMethod]
    public void WithScopeColumns_ReadsTheRowItself()
    {
        using var context = Create(scopeColumns: true);
        var filter = Build(context, [Root(5, 2)], scoped: true, "project", typeSeq: 7);
        var sql = context.Set<Item>().Where(filter).ToQueryString();

        sql.Should().Contain("\"i\".\"SqlOSFgaAncestor2\" = @");
        sql.Should().Contain("\"i\".\"SqlOSFgaReach\" <= 2");
        sql.Should().Contain("\"i\".\"SqlOSFgaTypeSeq\" = @");
        sql.Should().Contain("fn_ActiveSubjects");
        sql.Should().NotContain("\"SqlOSFgaResources\"", "no join: the lineage sits on the row");
    }

    [TestMethod]
    public void ARootDeeperThanTheModel_Fails()
    {
        using var context = Create(scopeColumns: false);
        var act = () => Build(context, [Root(5, 11)], scoped: false, null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*MaxResourceHierarchyDepth*");
    }

    private static SqlOSFgaAccessRoot Root(long seq, short depth) => new() { ResourceSeq = seq, Depth = depth };

    private static System.Linq.Expressions.Expression<Func<Item, bool>> Build(FilterDbContext context, IReadOnlyList<SqlOSFgaAccessRoot> roots, bool scoped, string? type, int? typeSeq = null)
    {
        var liveQuery = context.Set<SqlOSFgaActiveSubject>().FromSqlRaw("SELECT SubjectId FROM fn_ActiveSubjects({0})", "[\"u\"]").AsNoTracking();
        return SqlOSFgaFilterBuilder.Build<Item>(context, roots, liveQuery, "[\"u\"]", "perm", type, typeSeq, scoped, levels: 11);
    }

    private static FilterDbContext Create(bool scopeColumns)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return scopeColumns
            ? new ScopedFilterDbContext(new DbContextOptionsBuilder<ScopedFilterDbContext>().UseSqlite(connection).Options)
            : new PlainFilterDbContext(new DbContextOptionsBuilder<PlainFilterDbContext>().UseSqlite(connection).Options);
    }

    private abstract class FilterDbContext(DbContextOptions options, bool scopeColumns) : DbContext(options), ISqlOSFgaDbContext
    {
        public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(string resourceId, string subjectIds, string permissionId)
            => throw new NotSupportedException();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Item>(item =>
            {
                item.ToTable("Items");
                item.HasKey(i => i.Id);
            });

            // SQLite only translates here; the index filters the scope pass declares are SQL Server's.
            modelBuilder.UseSqlOS(GetType(), SqlOSDatabase.SqlServerProviderName, new SqlOSFgaOptions { ScopeColumns = scopeColumns });
        }
    }

    // EF Core caches the model by context type; two configurations need two types.
    private sealed class PlainFilterDbContext(DbContextOptions<PlainFilterDbContext> options) : FilterDbContext(options, scopeColumns: false);

    private sealed class ScopedFilterDbContext(DbContextOptions<ScopedFilterDbContext> options) : FilterDbContext(options, scopeColumns: true);

    private sealed class Item : IHasResourceId
    {
        public int Id { get; set; }
        public string ResourceId { get; set; } = string.Empty;
    }
}
