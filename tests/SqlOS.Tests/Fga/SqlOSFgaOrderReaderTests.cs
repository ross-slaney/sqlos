using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Paging;

namespace SqlOS.Tests.Fga;

[TestClass]
public class SqlOSFgaOrderReaderTests
{
    private PageOrderModelContext _context = null!;

    [TestInitialize]
    public void Setup()
        => _context = PageOrderModelContext.CreateProducts(_ => { });

    [TestCleanup]
    public void Cleanup() => _context.Dispose();

    [TestMethod]
    public void OrderBy_AppendsTheKey()
    {
        var ordering = Read(_context.Products.OrderBy(product => product.Price));

        Columns(ordering).Should().Equal("Price", "Id");
        ordering.Descending.Should().BeFalse();
        ordering.EntityType.ClrType.Should().Be(typeof(PagedProduct));
    }

    [TestMethod]
    public void OrderByThenBy_KeepsTheChain_Descending()
    {
        var ordering = Read(_context.Products
            .OrderByDescending(product => product.Status)
            .ThenByDescending(product => product.CreatedAt));

        Columns(ordering).Should().Equal("Status", "CreatedAt", "Id");
        ordering.Descending.Should().BeTrue();
    }

    [TestMethod]
    public void OrderByTheKey_IsTheWholeOrder_AndColumnsAfterTheKeyAreDropped()
    {
        Columns(Read(_context.Products.OrderBy(product => product.Id))).Should().Equal("Id");
        Columns(Read(_context.Products.OrderBy(product => product.Price).ThenBy(product => product.Id).ThenBy(product => product.Rank)))
            .Should().Equal("Price", "Id");
        Columns(Read(_context.Products.OrderBy(product => product.Price).ThenBy(product => product.Price)))
            .Should().Equal("Price", "Id");
    }

    [TestMethod]
    public void QueryOptionsAndFilters_PassThrough_AsTheInnerQuery()
    {
        var inner = _context.Products
            .Include(product => product.Location)
            .AsNoTracking()
            .TagWith("page")
            .IgnoreQueryFilters()
            .AsSplitQuery()
            .Where(product => product.Rank > 3);

        var ordering = Read(inner.OrderBy(product => product.Rank));

        ordering.Inner.Should().BeSameAs(inner.Expression);
        Columns(ordering).Should().Equal("Rank", "Id");
    }

    [TestMethod]
    public void TrackingAndThenInclude_PassThrough()
    {
        var act = () => Read(_context.Products
            .AsTracking()
            .Include(product => product.Location).ThenInclude(location => location!.Name)
            .OrderBy(product => product.Name));

        act.Should().NotThrow();
    }

    [TestMethod]
    public void MixedDirections_AreRejected()
        => Rejected(_context.Products.OrderBy(product => product.Price).ThenByDescending(product => product.Name))
            .Should().Contain("one direction");

    [TestMethod]
    public void MissingOrderBy_IsRejected()
        => Rejected(_context.Products.Where(product => product.Rank > 1))
            .Should().Contain("needs an OrderBy");

    [TestMethod]
    public void OrderingBeforeAFilter_IsRejected()
        => Rejected(_context.Products.OrderBy(product => product.Price).Where(product => product.Rank > 1))
            .Should().Contain("must be the last operators");

    [TestMethod]
    public void SecondOrderBy_IsRejected()
        => Rejected(_context.Products.OrderBy(product => product.Price).OrderBy(product => product.Name))
            .Should().Contain("one OrderBy");

    [TestMethod]
    public void UnsupportedOperators_AreRejectedByName()
    {
        Rejected(_context.Products.Select(product => product).OrderBy(product => product.Price)).Should().StartWith("Select is not supported");
        Rejected(_context.Products.OrderBy(product => product.Price).Skip(5)).Should().StartWith("Skip is not supported");
        Rejected(_context.Products.OrderBy(product => product.Price).Take(5)).Should().StartWith("Take is not supported");
        Rejected(_context.Products.Distinct().OrderBy(product => product.Price)).Should().StartWith("Distinct is not supported");
        Rejected(_context.Products.Union(_context.Products).OrderBy(product => product.Price)).Should().StartWith("Union is not supported");
        Rejected(_context.Products.Concat(_context.Products).OrderBy(product => product.Price)).Should().StartWith("Concat is not supported");
        Rejected(_context.Products.GroupBy(product => product.Rank).Select(group => group.First()).OrderBy(product => product.Price))
            .Should().StartWith("Select is not supported");
        Rejected(_context.Products
                .Join(_context.Products, left => left.Id, right => right.Id, (left, right) => left)
                .OrderBy(product => product.Price))
            .Should().StartWith("Join is not supported");
        Rejected(_context.Products.SelectMany(product => _context.Products).OrderBy(product => product.Price))
            .Should().StartWith("SelectMany is not supported");
    }

    [TestMethod]
    public void OrdersOverAnythingButAMappedScalarProperty_AreRejected()
    {
        Rejected(_context.Products.OrderBy(product => product.Price * 2)).Should().Contain("mapped scalar properties");
        Rejected(_context.Products.OrderBy(product => product.Location!.Name)).Should().Contain("mapped scalar properties");
        Rejected(_context.Products.OrderBy(product => EF.Property<int>(product, "Rank"))).Should().Contain("mapped scalar properties");
        Rejected(_context.Products.OrderBy(product => product.DeletedAt)).Should().Contain("nullable");
    }

    private static SqlOSFgaPageOrdering Read<T>(IQueryable<T> query)
        => SqlOSFgaOrderReader.Read(query.Expression);

    private static string Rejected<T>(IQueryable<T> query)
    {
        var act = () => SqlOSFgaOrderReader.Read(query.Expression);
        return act.Should().Throw<ArgumentException>().Which.Message;
    }

    private static IEnumerable<string> Columns(SqlOSFgaPageOrdering ordering)
        => ordering.Columns.Select(column => column.Name);
}
