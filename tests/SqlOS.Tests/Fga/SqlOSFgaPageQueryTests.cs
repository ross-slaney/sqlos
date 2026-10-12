using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Paging;
using Read1Row = SqlOS.Fga.Paging.SqlOSFgaPageQuery.Read1Row;
using Read2Row = SqlOS.Fga.Paging.SqlOSFgaPageQuery.Read2Row;

namespace SqlOS.Tests.Fga;

[TestClass]
public class SqlOSFgaPageQueryTests
{
    private const int PageSize = 5;

    [TestMethod]
    public async Task DenseFirstRound_IsServedByRead1()
    {
        var reads = new FakeReads(read1: budget => Rows(0, budget, visible: _ => true));

        var selection = await reads.SelectAsync(declared: true);

        selection.Source.Should().Be(SqlOSFgaPageQuery.FromRead1);
        Keys(selection).Should().Equal(0, 1, 2, 3, 4);
        selection.HasMore.Should().BeTrue();
        reads.Read1Budgets.Should().Equal(PageSize);
        reads.Read2Budgets.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Read1Exhausted_IsTheLastPage()
    {
        var reads = new FakeReads(read1: _ => Rows(0, 3, visible: key => key != 1));

        var selection = await reads.SelectAsync(declared: true);

        Keys(selection).Should().Equal(0, 2);
        selection.HasMore.Should().BeFalse();
        reads.Read2Budgets.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Read1Exhausted_WithExactlyAPageVisible_HasNoNextPage()
    {
        var reads = new FakeReads(read1: budget => budget == PageSize
            ? Rows(0, PageSize, visible: key => key < 4)
            : Rows(0, 7, visible: key => key < 5));

        var selection = await reads.SelectAsync(declared: false);

        Keys(selection).Should().Equal(0, 1, 2, 3, 4);
        selection.HasMore.Should().BeFalse();
    }

    [TestMethod]
    public async Task SparseCaller_IsServedByRead2()
    {
        var reads = new FakeReads(
            read1: budget => Rows(0, budget, visible: key => key == 2),
            read2: _ => Entries(("node-a", 10), ("node-a", 11), ("node-b", 12), ("node-a", 13), ("node-b", 14)));

        var selection = await reads.SelectAsync(declared: true);

        selection.Source.Should().Be(SqlOSFgaPageQuery.FromRead2);
        Keys(selection).Should().Equal(10, 11, 12, 13, 14);
        selection.HasMore.Should().BeTrue();
        selection.Rounds.Should().Be(1);
        selection.Read2EntriesPerRound.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new Dictionary<string, int> { ["node-a"] = 3, ["node-b"] = 2 });
    }

    [TestMethod]
    public async Task Read2Exhausted_IsAShortLastPage()
    {
        var reads = new FakeReads(
            read1: budget => Rows(0, budget, visible: _ => false),
            read2: _ => Entries(("node-a", 10), ("node-b", 12)));

        var selection = await reads.SelectAsync(declared: true);

        Keys(selection).Should().Equal(10, 12);
        selection.HasMore.Should().BeFalse();
        selection.Source.Should().Be(SqlOSFgaPageQuery.FromRead2);
    }

    [TestMethod]
    public async Task UndeclaredOrder_RunsRead1Only_GrowingTheBudgetFourTimesPerRound()
    {
        // Visible rows sit at keys 30 and above: the third round (budget 80) reaches them.
        var reads = new FakeReads(read1: budget => Rows(0, budget, visible: key => key >= 30));

        var selection = await reads.SelectAsync(declared: false);

        reads.Read1Budgets.Should().Equal(5, 20, 80);
        reads.Read2Budgets.Should().BeEmpty();
        selection.Rounds.Should().Be(3);
        selection.Read1RowsPerRound.Should().Equal(5, 20, 80);
        Keys(selection).Should().Equal(30, 31, 32, 33, 34);
        selection.HasMore.Should().BeTrue();
    }

    [TestMethod]
    public async Task Read2_SettlesThePage_InTheRoundItRuns()
    {
        // Read 2's per-node budget is never below the page size, so it either fills the page or
        // shows that every node ran out: the page never needs another round once read 2 runs.
        var reads = new FakeReads(
            read1: budget => Rows(0, budget, visible: _ => false),
            read2: budget => Enumerable.Range(100, Math.Min(budget, PageSize - 1))
                .Select(key => new Read2Row([key, key], "node-a"))
                .ToList());

        var selection = await reads.SelectAsync(declared: true);

        reads.Read1Budgets.Should().Equal(PageSize);
        reads.Read2Budgets.Should().Equal(PageSize);
        Keys(selection).Should().Equal(100, 101, 102, 103);
        selection.HasMore.Should().BeFalse();
    }

    [TestMethod]
    public async Task EmptyFilter_ReturnsAnEmptyPage_WithoutAnyRead()
    {
        // The context has no reachable database: any read would fail to connect.
        using var context = PageOrderModelContext.CreateProducts(entity => entity.HasPageOrder(product => product.Price));
        Expression<Func<PagedProduct, bool>> nothingVisible = product => false;

        var page = await context.Products.OrderBy(product => product.Price).ToPageAsync(nothingVisible, pageSize: 20);

        page.Data.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
        page.PageSize.Should().Be(20);
    }

    [TestMethod]
    public async Task PageSize_IsBetweenOneAndOneThousand()
    {
        using var context = PageOrderModelContext.CreateProducts(_ => { });
        Expression<Func<PagedProduct, bool>> nothingVisible = product => false;
        var query = context.Products.OrderBy(product => product.Price);

        await FluentActions.Awaiting(() => query.ToPageAsync(nothingVisible, 0)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => query.ToPageAsync(nothingVisible, 1001)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await query.ToPageAsync(nothingVisible, 1000)).PageSize.Should().Be(1000);
    }

    [TestMethod]
    public async Task ForeignFilter_IsRejected_BeforeAnyRead()
    {
        using var context = PageOrderModelContext.CreateProducts(_ => { });

        var act = () => context.Products.OrderBy(product => product.Price).ToPageAsync(product => product.Rank > 1, pageSize: 10);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("The filter must come from ISqlOSFgaAuthService.BuildFilterAsync.*");
    }

    private static IEnumerable<int> Keys(SqlOSFgaPageQuery.Selection selection)
        => selection.Rows.Select(row => (int)row[^1]);

    private static IReadOnlyList<Read1Row> Rows(int first, int count, Func<int, bool> visible)
        => Enumerable.Range(first, count).Select(key => new Read1Row([key, key], visible(key))).ToList();

    private static IReadOnlyList<Read2Row> Entries(params (string Node, int Key)[] entries)
        => entries.Select(entry => new Read2Row([entry.Key, entry.Key], entry.Node)).ToList();

    private sealed class FakeReads(
        Func<int, IReadOnlyList<Read1Row>> read1,
        Func<int, IReadOnlyList<Read2Row>>? read2 = null)
    {
        public List<int> Read1Budgets { get; } = [];
        public List<int> Read2Budgets { get; } = [];

        public Task<SqlOSFgaPageQuery.Selection> SelectAsync(bool declared)
            => SqlOSFgaPageQuery.SelectAsync(
                PageSize,
                (budget, _) =>
                {
                    Read1Budgets.Add(budget);
                    return Task.FromResult(read1(budget));
                },
                declared
                    ? (budget, _) =>
                    {
                        Read2Budgets.Add(budget);
                        return Task.FromResult(read2!(budget));
                    }
                    : null,
                CancellationToken.None);
    }
}
