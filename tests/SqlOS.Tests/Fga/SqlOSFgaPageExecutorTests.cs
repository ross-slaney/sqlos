using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Paging;

namespace SqlOS.Tests.Fga;

/// <summary>
/// The page executor over a backend in memory, with deterministic streams and the work counted: rows
/// fetched from the streams (an index seek each in the database) and rounds (a round trip each). The walk's
/// decisions (opens, splits, cuts, exhaustion, duplicates, what is held between rounds) are the production
/// code's; only the streams' rows and the merge are modeled, in key order (integers here; the database
/// orders real pages, the held rows included).
/// </summary>
[TestClass]
public class SqlOSFgaPageExecutorTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OneBranchSuppliesTheWholePage_WhileManyOthersHaveLaterRows()
    {
        // 1,000 directly granted principals; the first holds rows 0..99, the others rows past 10,000.
        var backend = new MemoryBackend(root: MemoryBackend.Ungranted());
        backend.AddDirect("p0", Enumerable.Range(0, 100));
        for (var i = 1; i < 1_000; i++)
        {
            backend.AddDirect($"p{i}", [10_000 + i, 20_000 + i]);
        }

        var executor = new SqlOSFgaPageExecutor(backend);
        var page = await executor.PageAsync(null, 20, CancellationToken.None);
        Report(executor, backend);

        page.Select(p => (int)p[^1]!).Should().Equal(Enumerable.Range(0, 20));

        // Round 1 reads two rows of every branch (2,000); the first branch's two are consumed and its buffer
        // is empty, so the round ends. Round 2 refills that branch alone with the 18 rows still needed, the
        // 1,998 rows held from round 1 are merged behind them, and the page is full: no row is fetched twice.
        executor.Counters.Rounds.Should().Be(3, "the prelude, then one round per refill of the one branch supplying the page");
        backend.RowsFetched.Should().Be(2_018, "a row fetched and not yet consumed is held, not fetched again");
        executor.Counters.RowsRetained.Should().Be(1_998);
        executor.Counters.Discarded.Should().Be(0);
    }

    [TestMethod]
    public async Task RowsInterleavedAcrossManyBranches()
    {
        // Row k belongs to branch k mod 1,000: a page of 20 is one row from each of 20 branches.
        var backend = new MemoryBackend(root: MemoryBackend.Ungranted());
        for (var i = 0; i < 1_000; i++)
        {
            backend.AddDirect($"p{i}", [i, 1_000 + i, 2_000 + i]);
        }

        var executor = new SqlOSFgaPageExecutor(backend);
        var page = await executor.PageAsync(null, 20, CancellationToken.None);
        Report(executor, backend);

        page.Select(p => (int)p[^1]!).Should().Equal(Enumerable.Range(0, 20));
        executor.Counters.Rounds.Should().Be(2, "one round: every branch's first row is in it");
        backend.RowsFetched.Should().Be(2_000, "two rows per branch, the least a round asks");
    }

    [TestMethod]
    public async Task HeldRows_AreMergedBehindTheRowsTheNextRoundFetches()
    {
        // Branch a holds two early rows and two late ones; branch b holds the rows in between. After a's early
        // rows and b's first batch, b is refilled alone: its new rows come before a's held late rows, and the
        // merge (the database's, modeled here) puts them there.
        var backend = new MemoryBackend(root: MemoryBackend.Ungranted());
        backend.AddDirect("a", [0, 1, 50, 51]);
        backend.AddDirect("b", Enumerable.Range(2, 39));

        var executor = new SqlOSFgaPageExecutor(backend);
        var page = await executor.PageAsync(null, 20, CancellationToken.None);
        Report(executor, backend);

        page.Select(p => (int)p[^1]!).Should().Equal(Enumerable.Range(0, 20));
        executor.Counters.Rounds.Should().Be(3);
        backend.RowsFetched.Should().Be(22, "a's four rows, b's first seven, then the eleven b still owed");
        executor.Counters.RowsRetained.Should().Be(2, "a's late rows, held once");
    }

    [TestMethod]
    public async Task ALaterPage_FromAFreshCursor()
    {
        var backend = new MemoryBackend(root: MemoryBackend.Ungranted());
        for (var i = 0; i < 100; i++)
        {
            backend.AddDirect($"p{i}", [i, 100 + i, 200 + i]);
        }

        var executor = new SqlOSFgaPageExecutor(backend);
        var page = await executor.PageAsync([150], 20, CancellationToken.None);
        Report(executor, backend);
        page.Select(p => (int)p[^1]!).Should().Equal(Enumerable.Range(151, 20));
    }

    [TestMethod]
    public async Task DuplicatesAcrossBranches_AreEmittedOnce()
    {
        var backend = new MemoryBackend(root: MemoryBackend.Ungranted());
        backend.AddDirect("a", [1, 2, 3, 4, 5]);
        backend.AddDirect("b", [2, 3, 6]);
        backend.AddDirect("c", [5, 6, 7]);

        var executor = new SqlOSFgaPageExecutor(backend);
        var page = await executor.PageAsync(null, 10, CancellationToken.None);
        Report(executor, backend);

        page.Select(p => (int)p[^1]!).Should().Equal(1, 2, 3, 4, 5, 6, 7);
        executor.Counters.Duplicates.Should().Be(4, "2 and 3 again from b, 5 and 6 again from c");
    }

    [TestMethod]
    public async Task ADuplicateHeldFromAnEarlierRound_IsStillSeenAsOne()
    {
        // Row 2 is in both branches. a's batch ends at 2 and the round stops there; b's copy of 2 is held and
        // consumed in the next round, right after a's was emitted in the one before.
        var backend = new MemoryBackend(root: MemoryBackend.Ungranted());
        backend.AddDirect("a", [1, 2, 3, 4, 5, 6]);
        backend.AddDirect("b", [2, 50]);

        var executor = new SqlOSFgaPageExecutor(backend);
        var page = await executor.PageAsync(null, 4, CancellationToken.None);
        Report(executor, backend);

        page.Select(p => (int)p[^1]!).Should().Equal(1, 2, 3, 4);
        executor.Counters.Duplicates.Should().Be(1);
        executor.Counters.RowsRetained.Should().Be(2, "b's two rows, held across the round boundary");
        backend.RowsFetched.Should().Be(6);
    }

    [TestMethod]
    public async Task AStreamThatEndsOnABatchBoundary_IsFoundEmptyOnRefill_WhileHeldRowsAreConsumed()
    {
        // a's two rows fill its first batch exactly, so it looks like it may have more. Its refill brings
        // nothing (exhausted), and b's held rows are consumed in that same round.
        var backend = new MemoryBackend(root: MemoryBackend.Ungranted());
        backend.AddDirect("a", [0, 1]);
        backend.AddDirect("b", Enumerable.Range(2, 99));
        for (var i = 0; i < 8; i++)
        {
            backend.AddDirect($"empty{i}", []);
        }

        var executor = new SqlOSFgaPageExecutor(backend);
        var page = await executor.PageAsync(null, 20, CancellationToken.None);
        Report(executor, backend);

        page.Select(p => (int)p[^1]!).Should().Equal(Enumerable.Range(0, 20));
        executor.Counters.Rounds.Should().Be(4, "the prelude; a and b's first two rows each; a's empty refill with b's held rows consumed; b's last sixteen");
        backend.RowsFetched.Should().Be(20, "every row once: a's two, b's two, then the sixteen b still owed");
        executor.Counters.RowsRetained.Should().Be(2, "b's first two rows, held through a's empty refill");
    }

    [TestMethod]
    public async Task UnderfullAndEmptyPages_EndWhenTheStreamsDo()
    {
        var backend = new MemoryBackend(root: MemoryBackend.Ungranted());
        backend.AddDirect("a", [1, 2]);
        backend.AddDirect("b", [3]);
        var executor = new SqlOSFgaPageExecutor(backend);
        (await executor.PageAsync(null, 20, CancellationToken.None)).Select(p => (int)p[^1]!).Should().Equal(1, 2, 3);
        Report(executor, backend);
        executor.Counters.Rounds.Should().Be(2, "every stream's batch was short: nothing is left to ask");

        var empty = new MemoryBackend(root: MemoryBackend.Ungranted());
        empty.AddDirect("a", []);
        (await new SqlOSFgaPageExecutor(empty).PageAsync(null, 20, CancellationToken.None)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task ASplit_OpensStreamsWithRowsBeforeTheOnesKept()
    {
        // The root is not granted; two of its children are. Its structural stream denies two rows, splits,
        // and the children's streams start at the split (the last row consumed), before the rows other
        // branches had already supplied, which are held. The split stream's own unconsumed rows are dropped.
        // (A node's structural stream holds every row below it, the children's rows among them.)
        var backend = new MemoryBackend(root: MemoryBackend.Structural(threshold: 2, children: [2, 3]));
        backend.AddStructuralRows(node: 1, level: 0, [(5, true), (6, true), (8, true), (9, true), (10, false), (11, false), (12, true), (13, true), (14, true)]);
        backend.Node(2, active: true, granted: true, hasChildren: true);
        backend.Node(3, active: true, granted: true, hasChildren: true);
        backend.AddReach(node: 2, level: 1, [5, 6, 12]);
        backend.AddReach(node: 3, level: 1, [8, 9, 13, 14]);
        backend.AddDirect("d", [7, 100, 101]);

        var executor = new SqlOSFgaPageExecutor(backend);
        var page = await executor.PageAsync(null, 20, CancellationToken.None);
        Report(executor, backend);

        page.Select(p => (int)p[^1]!).Should().Equal(5, 6, 7, 8, 9, 12, 13, 14, 100, 101);
        executor.Counters.Splits.Should().Be(1);
        executor.Counters.Denials.Should().Be(2);
        executor.Counters.Discarded.Should().Be(3, "the split stream's rows past the split: 12, 13, 14, which its children supply");
        executor.Counters.RowsRetained.Should().Be(2, "d's 100 and 101, held across the split");
        backend.RowsFetched.Should().Be(9 + 3 + 1 + 2, "the root's nine, d's three, then one row of child 2 and two of child 3 past the split");
    }

    [TestMethod]
    public async Task ACutBelowAnOpenedNode_IsOpenedBeforeAnythingIsConsumed()
    {
        // The root is granted and streams its reach; an inactive child holds a grant below it (a cut), whose
        // rows are not in the root's reach and come before the root's own rows.
        var backend = new MemoryBackend(root: MemoryBackend.Granted(cutGrants: 1, children: [2]));
        backend.AddReach(node: 1, level: 0, [50, 51, 52]);
        backend.Node(2, active: false, threshold: 1, children: [3]);
        backend.Node(3, active: true, granted: true, hasChildren: true);
        backend.AddReach(node: 3, level: 2, [1, 2, 3]);

        var executor = new SqlOSFgaPageExecutor(backend);
        var page = await executor.PageAsync(null, 20, CancellationToken.None);
        Report(executor, backend);

        page.Select(p => (int)p[^1]!).Should().Equal(1, 2, 3, 50, 51, 52);
        executor.Counters.EagerSplits.Should().Be(1, "the inactive child was expanded into its grant-bearing children");
        backend.RowsFetched.Should().Be(6, "the root's rows, fetched before the cut was opened, are not fetched again");
        executor.Counters.RowsRetained.Should().Be(3 + 3, "the root's three rows held while the cut was opened, and again while it was expanded");
    }

    private void Report(SqlOSFgaPageExecutor executor, MemoryBackend backend)
        => Console.WriteLine($"{TestContext.TestName}: {executor.Counters} | backend fetched {backend.RowsFetched}");

    /// <summary>A backend in memory: nodes with their state and counts, streams with their rows, a merge by integer key.</summary>
    private sealed class MemoryBackend : ISqlOSFgaPageBackend
    {
        private readonly Dictionary<long, NodeState> _nodes = [];
        private readonly Dictionary<(SqlOSFgaStreamKind Kind, int Level, long Node, string? Principal, string? Role), List<(int Key, bool Granted)>> _rows = [];
        private readonly List<(string Principal, string Role)> _direct = [];

        public MemoryBackend(NodeState root)
        {
            _nodes[1] = root;
        }

        public int RowsFetched { get; private set; }

        public long RootNode => 1;

        public IReadOnlyList<(string Principal, string Role)> DirectStreams => _direct;

        public static NodeState Ungranted() => new(Active: true, Granted: false, HasChildren: true, Threshold: 0, CutGrants: 0, Children: []);

        public static NodeState Granted(int cutGrants = 0, IReadOnlyList<long>? children = null) => new(true, true, true, 0, cutGrants, children ?? []);

        public static NodeState Structural(int threshold, IReadOnlyList<long> children) => new(true, false, true, threshold, 0, children);

        public void Node(long seq, bool active, bool granted = false, bool hasChildren = true, int threshold = 0, int cutGrants = 0, IReadOnlyList<long>? children = null)
            => _nodes[seq] = new NodeState(active, granted, hasChildren, threshold, cutGrants, children ?? []);

        public void AddDirect(string principal, IEnumerable<int> keys)
        {
            _direct.Add((principal, "r"));
            _rows[(SqlOSFgaStreamKind.Direct, -1, 0, principal, "r")] = keys.Select(k => (k, true)).ToList();
        }

        public void AddReach(long node, int level, IEnumerable<int> keys) => _rows[(SqlOSFgaStreamKind.Reach, level, node, null, null)] = keys.Select(k => (k, true)).ToList();

        public void AddStructuralRows(long node, int level, IEnumerable<(int Key, bool Granted)> rows) => _rows[(SqlOSFgaStreamKind.Structural, level, node, null, null)] = rows.ToList();

        public Task<bool> BeginAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<SqlOSFgaRound> RoundAsync(IReadOnlyList<SqlOSFgaOpenRequest> opens, IReadOnlyList<SqlOSFgaStream> streams, IReadOnlyList<SqlOSFgaFetchedRow> retained, CancellationToken cancellationToken)
        {
            var opened = new List<SqlOSFgaOpenedNode>();
            var fetches = new List<(SqlOSFgaStreamKind Kind, int Level, long Node, string? Principal, string? Role, object?[]? After, int Fetch)>();
            foreach (var open in opens)
            {
                var targets = open.Children ? _nodes[open.Node].Children : [open.Node];
                var level = open.Level;
                foreach (var seq in targets)
                {
                    var node = _nodes[seq];
                    var fetch = open.Children ? Math.Max(2, (int)Math.Ceiling(open.Fetch / (double)targets.Count)) : open.Fetch;
                    opened.Add(new SqlOSFgaOpenedNode(open.Id, seq, level, node.Active, node.Granted, node.HasChildren, node.Threshold, node.CutGrants, fetch));
                    if (node.Active && !open.CutMode && node.Granted && node.HasChildren)
                    {
                        fetches.Add((SqlOSFgaStreamKind.Reach, level, seq, null, null, open.After, fetch));
                    }
                    else if (node.Active && !open.CutMode && !node.Granted && node.Threshold > 0)
                    {
                        fetches.Add((SqlOSFgaStreamKind.Structural, level, seq, null, null, open.After, fetch));
                    }
                }
            }

            foreach (var s in streams)
            {
                fetches.Add((s.Kind, s.Level, s.Node, s.Principal, s.Role, s.ReadFrom, s.Fetch));
            }

            // The held rows come back as they were; the fresh rows join them; one order over both.
            var rows = new List<SqlOSFgaFetchedRow>(retained);
            foreach (var f in fetches)
            {
                if (!_rows.TryGetValue((f.Kind, f.Level, f.Node, f.Principal, f.Role), out var all))
                {
                    continue;
                }

                var after = f.After is null ? int.MinValue : (int)f.After[^1]!;
                var batch = all.Where(r => r.Key > after).Take(f.Fetch).ToList();
                RowsFetched += batch.Count;
                for (var i = 0; i < batch.Count; i++)
                {
                    rows.Add(new SqlOSFgaFetchedRow(f.Kind, f.Level, f.Node, f.Principal, f.Role, i + 1, batch.Count, batch[i].Granted, [batch[i].Key]));
                }
            }

            return Task.FromResult(new SqlOSFgaRound(opened, rows.OrderBy(r => (int)r.Position[^1]!).ThenBy(r => r.Kind).ThenBy(r => r.Principal).ToList(), 2));
        }

        public sealed record NodeState(bool Active, bool Granted, bool HasChildren, int Threshold, int CutGrants, IReadOnlyList<long> Children);
    }
}
