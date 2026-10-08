using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Paging;

namespace SqlOS.Tests.Fga;

/// <summary>
/// What SqlOS reads off a query before deciding to walk it: which shapes are pages, and what a page's order
/// and key are. Over a SQLite context with the FGA model, which is enough to translate the filters.
/// </summary>
[TestClass]
public class SqlOSFgaPagePlannerTests
{
    private static readonly SqlOSFgaAccessToken Token = new(["subj_a"], "[\"subj_a\"]", "perm_read", "READ", TypeSeq: null, new SqlOSFgaOptions());

    [TestMethod]
    public void AnOrderThatContinuesPastTheKey_EndsAtTheKey()
    {
        using var context = Create();
        var materialization = context.Set<Item>().AsQueryable().Expression;

        // The key is unique: whatever follows it in the order never decides anything, and the page's position
        // ends with the key, as the walk requires.
        var past = SqlOSFgaPageQuery<Item>.Create(context, [], [("Id", false), ("Rank", false)], materialization);
        past.OrderColumns.Select(c => c.Property.Name).Should().Equal("Id");
        past.Key.Property.Name.Should().Be("Id");
        past.IndexSuffix.Should().BeNull("the key alone is the key index's order");

        var before = SqlOSFgaPageQuery<Item>.Create(context, [], [("Rank", false)], materialization);
        before.OrderColumns.Select(c => c.Property.Name).Should().Equal("Rank", "Id");
        before.Key.Property.Name.Should().Be("Id");
        before.IndexSuffix.Should().Be("Rank");

        var explicitKey = SqlOSFgaPageQuery<Item>.Create(context, [], [("Rank", false), ("Id", false)], materialization);
        explicitKey.OrderColumns.Select(c => c.Property.Name).Should().Equal("Rank", "Id");
        explicitKey.Key.Property.Name.Should().Be("Id");
    }

    [TestMethod]
    public void AConditionOrAnOrderAfterThePage_IsNotAPage()
    {
        using var context = Create();
        var filter = SqlOSFgaAccess.Filter<Item>(Token);
        var set = context.Set<Item>().AsQueryable();
        var logger = NullLogger.Instance;

        Plan(set.Where(filter).OrderBy(i => i.Rank).Take(1)).Should().NotBeNull("the page as written");
        Plan(set.Where(filter).Where(i => i.Id == 2).OrderBy(i => i.Rank).Take(1)).Should().NotBeNull("a condition before the page is the page's filter");
        Plan(set.Where(filter).OrderBy(i => i.Rank).Take(1).Select(i => i.Id)).Should().NotBeNull("a projection after the page applies to its rows");

        Plan(set.Where(filter).OrderBy(i => i.Rank).Take(1).Where(i => i.Id == 2)).Should().BeNull("a condition after Take applies to the page's rows, which the walk cannot do");
        Plan(set.Where(filter).OrderBy(i => i.Rank).Take(3).OrderBy(i => i.Id)).Should().BeNull("an order after Take re-sorts the page's rows");
        Plan(set.Where(filter).OrderBy(i => i.Rank).Take(3).Skip(1)).Should().BeNull("a Skip after Take is not the page's Skip");

        ISqlOSFgaPagePlan? Plan<T>(IQueryable<T> query) => SqlOSFgaPagePlanner.TryPlan(query.Expression, context, logger);
    }

    private static PlannerDbContext Create()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return new PlannerDbContext(new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite(connection).Options);
    }

    private sealed class PlannerDbContext(DbContextOptions<PlannerDbContext> options) : DbContext(options), ISqlOSFgaDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Item>(item =>
            {
                item.ToTable("Items");
                item.HasKey(i => i.Id);
                item.HasIndex(i => i.Rank);
                item.HasIndex(i => new { i.Id, i.Rank });
            });

            // SQLite only translates the filters here; the model is SQL Server's (a byte string scope column).
            modelBuilder.UseSqlOS(SqlOSDatabase.SqlServerProviderName, new SqlOSFgaOptions());
        }
    }

    private sealed class Item : IHasResourceId
    {
        public byte[]? FgaScope { get; private set; }

        public int Id { get; set; }

        public int Rank { get; set; }

        public string ResourceId { get; set; } = string.Empty;
    }
}
