using System.Linq.Expressions;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Indexing;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Tests.Fga;

[TestClass]
public class SqlOSFgaNativePageRewriterTests
{
    [TestMethod]
    public void AConditionAfterTakeRemainsOutsideTheSelectedPage()
    {
        using var db = Context();
        var query = db.Items.Where(Auth(db)).OrderBy(i => i.Id).Take(1).Where(i => i.Id == 2);
        var rewritten = Rewrite(db, query.Expression);
        rewritten.Should().NotBeSameAs(query.Expression);
        var outer = (MethodCallExpression)rewritten;
        outer.Method.Name.Should().Be(nameof(Queryable.Where));
        ((MethodCallExpression)outer.Arguments[0]).Method.Name.Should().Be(nameof(Queryable.Take));
        outer.Arguments[1].Should().BeSameAs(((MethodCallExpression)query.Expression).Arguments[1]);
        query.Provider.CreateQuery(rewritten).ToQueryString().Should().Contain("CROSS APPLY");
    }

    [TestMethod]
    public void AnOrderPastTheUniqueKeyStopsAtTheActualKey()
    {
        using var db = Context();
        var query = db.Items.Where(Auth(db)).OrderBy(i => i.Id).ThenBy(i => i.Rank).Take(3);
        var sql = query.Provider.CreateQuery(Rewrite(db, query.Expression)).ToQueryString();
        sql.Should().Contain("CROSS APPLY");
        sql.Should().Contain("ORDER BY candidate.[__SqlOS_Order0] ASC");
        sql.Should().NotContain("__SqlOS_Order1");
    }

    [TestMethod]
    public void OrAndMultipleAuthorizationPredicatesKeepTheOriginalQuery()
    {
        using var db = Context();
        var auth = Auth(db);
        var expanded = Expression.Lambda<Func<Item, bool>>(
            Expression.OrElse(auth.Body, Expression.Property(auth.Parameters[0], nameof(Item.Public))), auth.Parameters);
        var withOr = db.Items.Where(expanded).OrderBy(i => i.Id).Take(3);
        Rewrite(db, withOr.Expression).Should().BeSameAs(withOr.Expression);
        var twice = db.Items.Where(auth).Where(Auth(db, "another_permission")).OrderBy(i => i.Id).Take(3);
        Rewrite(db, twice.Expression).Should().BeSameAs(twice.Expression);
    }

    [TestMethod]
    public void AFilterAfterSkipKeepsItsOriginalExecutionOrder()
    {
        using var db = Context();
        var query = db.Items.Where(Auth(db)).OrderBy(i => i.Id).Skip(1).Where(i => i.Id > 3).Take(2);
        Rewrite(db, query.Expression).Should().BeSameAs(query.Expression);
    }

    [TestMethod]
    public void ExplicitIncludesAndOuterIgnoreFiltersDoNotRewriteAnInnerPage()
    {
        using var db = Context();
        var include = db.Items.Where(Auth(db)).OrderBy(i => i.Id).Take(3).Include(i => i.Children);
        Rewrite(db, include.Expression).Should().BeSameAs(include.Expression);
        var ignore = db.Items.Where(Auth(db)).OrderBy(i => i.Id).Take(3).IgnoreQueryFilters();
        Rewrite(db, ignore.Expression).Should().BeSameAs(ignore.Expression);
    }

    [TestMethod]
    public void IgnoredFiltersOnASiblingOrBeforeTakePreserveTheWholeOriginalQuery()
    {
        using var db = Context();
        var page = db.Items.Where(Auth(db)).OrderBy(i => i.Id).Take(3);
        var sibling = page.Concat(db.Items.IgnoreQueryFilters().Where(i => i.Id == 5));
        Rewrite(db, sibling.Expression).Should().BeSameAs(sibling.Expression);
        var before = db.Items.IgnoreQueryFilters().Where(Auth(db)).OrderBy(i => i.Id).Take(3);
        Rewrite(db, before.Expression).Should().BeSameAs(before.Expression);
        var named = db.Items.IgnoreQueryFilters(["VisibleItems"]).Where(Auth(db)).OrderBy(i => i.Id).Take(3);
        Rewrite(db, named.Expression).Should().BeSameAs(named.Expression);
    }

    [TestMethod]
    public void AnOuterNavigationProjectionRetainsItsRelatedEntityGlobalFilter()
    {
        using var db = Context();
        var query = db.Items.Where(Auth(db)).OrderBy(i => i.Id).Take(3).Select(i => new { i.Id, Children = i.Children.Count() });
        var rewritten = Rewrite(db, query.Expression);
        rewritten.Should().NotBeSameAs(query.Expression);
        rewritten.ToString().Should().NotContain(nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters));
        var sql = query.Provider.CreateQuery(rewritten).ToQueryString();
        sql.Should().Contain("CROSS APPLY");
        var child = Regex.Match(sql, @"FROM (?:\[dbo\]\.)?\[NativeChildren\] AS \[(?<alias>[^\]]+)\]");
        child.Success.Should().BeTrue(sql);
        sql.Should().Contain($"[{child.Groups["alias"].Value}].[Visible]", "the navigation count must still exclude globally filtered children");
    }

    [TestMethod]
    public void PredicateRewritingNeverTouchesSqlLiteralsOrComments()
    {
        const string sql = "[i].[Rank] > @rank AND [i].[Description] = N'[i].[Rank]' /* [i].[Rank] */ -- [i].[Rank]\nAND [i].[Id] > 0";
        var (rewritten, join) = SqlOSFgaNativePageRewriter.RewritePredicate(sql, "[i]", "[scope]", new HashSet<string> { "Id", "Rank" });
        rewritten.Should().Be("[scope].[Rank] > @rank AND [i].[Description] = N'[i].[Rank]' /* [i].[Rank] */ -- [i].[Rank]\nAND [scope].[Id] > 0");
        join.Should().BeTrue("Description is not copied to the projection");
    }

    [TestMethod]
    public void SubqueryAliasesStayInTheOriginalApplicationScope()
    {
        const string sql = "[i].[Id] > 0 AND EXISTS (SELECT 1 FROM [Elsewhere] AS [i] WHERE [i].[Id] = 2)";
        var (rewritten, join) = SqlOSFgaNativePageRewriter.RewritePredicate(sql, "[i]", "[scope]", new HashSet<string> { "Id" });
        rewritten.Should().Be(sql);
        join.Should().BeTrue();
    }

    [TestMethod]
    public void PostgreSqlDollarQuotedTextIsNotAnAliasReferenceOrASelect()
    {
        const string sql = "i.\"Id\" > 0 AND i.\"Description\" = $tag$ SELECT i.\"Id\" $tag$";
        var (rewritten, join) = SqlOSFgaNativePageRewriter.RewritePredicate(sql, "i", "scope", new HashSet<string> { "Id", "Description" });
        rewritten.Should().Be("scope.\"Id\" > 0 AND scope.\"Description\" = $tag$ SELECT i.\"Id\" $tag$");
        join.Should().BeFalse();
    }

    [TestMethod]
    public void LiteralBracesInAFilterAreNotTreatedAsRawSqlParameterPlaceholders()
    {
        using var db = Context();
        var query = db.Items.Where(Auth(db)).Where(i => i.Description == "{0} {").OrderBy(i => i.Id).Take(2);
        var rewritten = Rewrite(db, query.Expression);
        var sql = query.Provider.CreateQuery(rewritten).ToQueryString();
        sql.Should().Contain("CROSS APPLY");
        sql.Should().Contain("N'{0} {'");
    }

    private static Expression Rewrite(ProbeContext db, Expression expression)
        => SqlOSFgaNativePageRewriter.Rewrite(expression, db, NullLogger.Instance);

    private static ProbeContext Context()
    {
        var db = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>()
            .UseSqlServer("Server=localhost;Database=NativeRewriterUnit;User ID=unit;Password=not-used;TrustServerCertificate=True").Options);
        var discovery = SqlOSFgaScopeModel.Discover(db.GetService<IDesignTimeModel>().Model, db.Database.ProviderName!);
        discovery.Entities.Should().ContainSingle();
        SqlOSFgaNativeOptions.Register(db, discovery);
        return db;
    }

    // The same concrete-context constant and TVF Any shape BuildFilterAsync constructs, without executing
    // the unrelated principal-resolution queries in these translation-only tests.
    private static Expression<Func<Item, bool>> Auth(ProbeContext db, string permission = "read")
    {
        var item = Expression.Parameter(typeof(Item), "item");
        var tvf = Expression.Call(Expression.Constant(db), typeof(ProbeContext).GetMethod(nameof(ProbeContext.IsResourceAccessible))!,
            Expression.Property(item, nameof(Item.ResourceId)), Expression.Constant("[\"caller\"]"), Expression.Constant(permission));
        var any = Expression.Call(typeof(Queryable), nameof(Queryable.Any), [typeof(SqlOSFgaAccessibleResource)], tvf);
        return Expression.Lambda<Func<Item, bool>>(any, item);
    }

    public sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options), ISqlOSFgaDbContext
    {
        public DbSet<Item> Items => Set<Item>();
        public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(string resourceId, string subjectIds, string permissionId)
            => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Item>(item =>
            {
                item.ToTable("NativeItems");
                item.HasKey(i => i.Id);
                item.Property(i => i.ResourceId).HasMaxLength(128);
                item.HasIndex(i => i.Rank);
                item.HasIndex(i => new { i.Id, i.Rank });
                item.HasQueryFilter(i => i.Visible);
            });
            builder.Entity<Child>(child =>
            {
                child.ToTable("NativeChildren");
                child.HasKey(c => c.Id);
                child.HasOne(c => c.Item).WithMany(i => i.Children).HasForeignKey(c => c.ItemId);
                child.HasQueryFilter(c => c.Visible);
            });
            builder.ApplySqlOSFgaModel(GetType());
        }
    }

    public sealed class Item : IHasResourceId
    {
        public int Id { get; set; }
        public string ResourceId { get; set; } = "";
        public int? Rank { get; set; }
        public string Description { get; set; } = "";
        public bool Visible { get; set; }
        public bool Public { get; set; }
        public ICollection<Child> Children { get; set; } = [];
    }

    public sealed class Child
    {
        public int Id { get; set; }
        public int ItemId { get; set; }
        public Item Item { get; set; } = null!;
        public bool Visible { get; set; }
    }
}
