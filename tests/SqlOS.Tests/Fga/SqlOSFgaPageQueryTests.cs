using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Paging;
using Read1Row = SqlOS.Fga.Paging.SqlOSFgaPageQuery.Read1Row;
using Read2Row = SqlOS.Fga.Paging.SqlOSFgaPageQuery.Read2Row;
using Read3Result = SqlOS.Fga.Paging.SqlOSFgaPageQuery.Read3Result;

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
    public async Task OrderWithoutANodeIndex_RunsRead1Only_GrowingTheBudgetFourTimesPerRound()
    {
        // Visible rows sit at keys 30 and above: the third round (budget 80) reaches them.
        var reads = new FakeReads(read1: budget => Rows(0, budget, visible: key => key >= 30));

        var selection = await reads.SelectAsync(declared: false);

        reads.Read1Budgets.Should().Equal(5, 20, 80);
        reads.Read2Budgets.Should().BeEmpty();
        reads.Read3Budgets.Should().BeEmpty();
        selection.Rounds.Should().Be(3);
        selection.Read1RowsPerRound.Should().Equal(5, 20, 80);
        Keys(selection).Should().Equal(30, 31, 32, 33, 34);
        selection.HasMore.Should().BeTrue();
    }

    [TestMethod]
    public async Task PlainOrder_DenseCaller_IsServedByRead1InRoundZero_WithoutRead3()
    {
        var reads = new FakeReads(
            read1: budget => Rows(0, budget, visible: _ => true),
            read3: CallerRows(Enumerable.Range(0, 1_000)));

        var selection = await reads.SelectAsync(declared: false);

        selection.Source.Should().Be(SqlOSFgaPageQuery.FromRead1);
        Keys(selection).Should().Equal(0, 1, 2, 3, 4);
        reads.Read1Budgets.Should().Equal(PageSize);
        reads.Read3Budgets.Should().BeEmpty("read 1 runs first in every round");
        selection.Read3KeysPerRound.Should().BeEmpty();
    }

    [TestMethod]
    public async Task PlainOrder_SparseCaller_IsServedByRead3InRoundZero()
    {
        // 3 visible rows of a large table, none of them among its first rows.
        var reads = new FakeReads(
            read1: budget => Rows(0, budget, visible: _ => false),
            read3: CallerRows([900, 901, 902]));

        var selection = await reads.SelectAsync(declared: false);

        selection.Source.Should().Be(SqlOSFgaPageQuery.FromRead3);
        Keys(selection).Should().Equal(900, 901, 902);
        selection.HasMore.Should().BeFalse();
        selection.Rounds.Should().Be(1);
        reads.Read3Budgets.Should().Equal(PageSize);
        selection.Read3KeysPerRound.Should().Equal(3);
    }

    [TestMethod]
    public async Task PlainOrder_FarEndCaller_IsServedByRead3_InTheFirstRoundItsRowsFit()
    {
        // 70 rows at the far end of the order: read 1 never reaches them; read 3 holds them at budget 80.
        var reads = new FakeReads(
            read1: budget => Rows(0, budget, visible: _ => false),
            read3: CallerRows(Enumerable.Range(10_000, 70)));

        var selection = await reads.SelectAsync(declared: false);

        selection.Source.Should().Be(SqlOSFgaPageQuery.FromRead3);
        Keys(selection).Should().Equal(10_000, 10_001, 10_002, 10_003, 10_004);
        selection.HasMore.Should().BeTrue();
        reads.Read1Budgets.Should().Equal(5, 20, 80);
        reads.Read3Budgets.Should().Equal(5, 20, 80);
        selection.Read3KeysPerRound.Should().Equal(6, 21, 70);
    }

    [TestMethod]
    public async Task PlainOrder_MidDensityCaller_StopsInTheFirstRoundEitherReadFits()
    {
        // One row in 50 is visible, 400 in all: read 1 needs 250 rows for a page of 5, read 3 needs a
        // budget of 400. Read 1 settles the page in round 3 (budget 320); read 3 ran rounds 0 to 2.
        var visibleKeys = Enumerable.Range(0, 20_000).Where(key => key % 50 == 49).ToList();
        var reads = new FakeReads(
            read1: budget => Rows(0, budget, visible: key => key % 50 == 49),
            read3: CallerRows(visibleKeys));

        var selection = await reads.SelectAsync(declared: false);

        selection.Source.Should().Be(SqlOSFgaPageQuery.FromRead1);
        Keys(selection).Should().Equal(49, 99, 149, 199, 249);
        reads.Read1Budgets.Should().Equal(5, 20, 80, 320);
        reads.Read3Budgets.Should().Equal(5, 20, 80);
        selection.Read3KeysPerRound.Should().Equal(6, 21, 81);

        // The work is a small multiple of min(rows read 1 needs, rows the caller can see) = 250.
        var work = selection.Read1RowsPerRound.Sum() + selection.Read3KeysPerRound.Sum();
        work.Should().BeLessThanOrEqualTo(4 * 250);
    }

    [TestMethod]
    public async Task PlainOrder_ExhaustedQuery_IsServedByRead1_WithoutRead3()
    {
        var reads = new FakeReads(
            read1: _ => Rows(0, 3, visible: key => key == 2),
            read3: CallerRows([2]));

        var selection = await reads.SelectAsync(declared: false);

        selection.Source.Should().Be(SqlOSFgaPageQuery.FromRead1);
        Keys(selection).Should().Equal(2);
        selection.HasMore.Should().BeFalse();
        reads.Read3Budgets.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(4, false)]
    [DataRow(5, false)]
    [DataRow(6, true)]
    public async Task Read3_HasMore_OnlyWhenARowFollowsThePage(int callerRows, bool hasMore)
    {
        var reads = new FakeReads(
            read1: budget => Rows(0, budget, visible: _ => false),
            read3: CallerRows(Enumerable.Range(100, callerRows)));

        var selection = await reads.SelectAsync(declared: false);

        selection.Source.Should().Be(SqlOSFgaPageQuery.FromRead3);
        Keys(selection).Should().Equal(Enumerable.Range(100, Math.Min(callerRows, PageSize)));
        selection.HasMore.Should().Be(hasMore);
    }

    [TestMethod]
    public async Task Read3_CallerWithNoRows_IsAnEmptyLastPage()
    {
        var reads = new FakeReads(
            read1: budget => Rows(0, budget, visible: _ => false),
            read3: CallerRows([]));

        var selection = await reads.SelectAsync(declared: false);

        selection.Source.Should().Be(SqlOSFgaPageQuery.FromRead3);
        selection.Rows.Should().BeEmpty();
        selection.HasMore.Should().BeFalse();
        selection.Read3KeysPerRound.Should().Equal(0);
    }

    [TestMethod]
    public async Task DeclaredOrder_NeverRunsRead3()
    {
        var act = () => SqlOSFgaPageQuery.SelectAsync(
            PageSize,
            (_, _) => Task.FromResult(Rows(0, 0, visible: _ => true)),
            (_, _) => Task.FromResult<IReadOnlyList<Read2Row>>([]),
            (_, _) => Task.FromResult(new Read3Result(0, [])),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("A declared order runs read 2, never read 3.*");
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
    public async Task PageSize_IsBetweenOneAndOneThousand_CheckedBeforeAnyRead()
    {
        // The context has no reachable database: any read would fail to connect.
        using var context = PageOrderModelContext.CreateProducts(_ => { });
        var query = context.Products.OrderBy(product => product.Price);

        await FluentActions.Awaiting(() => query.ToPageAsync("subj_reader", "READ", 0)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => query.ToPageAsync("subj_reader", "READ", 1001)).Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public async Task QueryNotFromASqlOSDbContext_IsRejected_BeforeAnyRead()
    {
        var query = new List<PagedProduct>().AsQueryable().OrderBy(product => product.Price);

        var act = () => query.ToPageAsync("subj_reader", "READ", 10);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("ToPageAsync needs a query that starts from a DbSet of a context deriving from SqlOSDbContext<TContext>*");
    }

    [TestMethod]
    public async Task InvalidOrdering_IsRejected_BeforeAnyRead()
    {
        using var context = PageOrderModelContext.CreateProducts(_ => { });

        var act = () => context.Products.ToPageAsync("subj_reader", "READ", 10);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("ToPageAsync needs an OrderBy*");
    }

    private static IEnumerable<int> Keys(SqlOSFgaPageQuery.Selection selection)
        => selection.Rows.Select(row => (int)row[^1]);

    private static IReadOnlyList<Read1Row> Rows(int first, int count, Func<int, bool> visible)
        => Enumerable.Range(first, count).Select(key => new Read1Row([key, key], visible(key))).ToList();

    private static IReadOnlyList<Read2Row> Entries(params (string Node, int Key)[] entries)
        => entries.Select(entry => new Read2Row([entry.Key, entry.Key], entry.Node)).ToList();

    /// <summary>
    /// Read 3 over a caller whose rows have these keys, in order: up to b + 1 keys, and with at most
    /// b of them, the first P + 1 rows.
    /// </summary>
    private static Func<int, Read3Result> CallerRows(IEnumerable<int> keys)
    {
        var rows = keys.Select(key => (IReadOnlyList<object>)[key, key]).ToList();
        return budget => rows.Count > budget
            ? new Read3Result(budget + 1, null)
            : new Read3Result(rows.Count, rows.Take(PageSize + 1).ToList());
    }

    private sealed class FakeReads(
        Func<int, IReadOnlyList<Read1Row>> read1,
        Func<int, IReadOnlyList<Read2Row>>? read2 = null,
        Func<int, Read3Result>? read3 = null)
    {
        public List<int> Read1Budgets { get; } = [];
        public List<int> Read2Budgets { get; } = [];
        public List<int> Read3Budgets { get; } = [];

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
                !declared && read3 != null
                    ? (budget, _) =>
                    {
                        Read3Budgets.Add(budget);
                        return Task.FromResult(read3(budget));
                    }
                    : null,
                CancellationToken.None);
    }
}
