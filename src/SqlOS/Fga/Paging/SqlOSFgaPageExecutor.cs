namespace SqlOS.Fga.Paging;

internal enum SqlOSFgaStreamKind
{
    /// <summary>A granted node: its reach-indexed rows, every one visible.</summary>
    Reach = 0,

    /// <summary>A non-granted active node with grants below: its rows in order, each judged by the backend.</summary>
    Structural = 1,

    /// <summary>One (principal, role)'s rows granted directly on their own resource, from the direct index.</summary>
    Direct = 2,
}

/// <summary>Open a node, or the grant-bearing children of one, at a level, after a position. In cut mode the node is inspected (its state and counts), not streamed.</summary>
internal sealed record SqlOSFgaOpenRequest(int Id, long Node, bool Children, int Level, object?[]? After, int Fetch, bool CutMode);

/// <summary>What the backend found at a node it opened or inspected.</summary>
internal sealed record SqlOSFgaOpenedNode(int RequestId, long Node, int Level, bool Active, bool Granted, bool HasChildren, int Threshold, int CutGrants, int Fetch);

/// <summary>
/// A row of a round, in the round's global order: the stream it came from, its ordinal in that stream's batch
/// and the batch's size, the backend's verdict (always true off reach and direct streams), and the row's
/// position (its order values, the key last).
/// </summary>
internal sealed record SqlOSFgaFetchedRow(SqlOSFgaStreamKind Kind, int Level, long Node, string? Principal, string? Role, int Ordinal, int Count, bool Granted, object?[] Position);

internal sealed record SqlOSFgaRound(IReadOnlyList<SqlOSFgaOpenedNode> Opened, IReadOnlyList<SqlOSFgaFetchedRow> Rows, int Statements);

/// <summary>
/// What the executor asks of the database, per round trip: open nodes, and fetch the next rows of every open
/// stream — those of the nodes just opened included — merged into one order by the database. The executor
/// never compares two keys itself, so every column type sorts exactly as the database sorts it.
/// </summary>
internal interface ISqlOSFgaPageBackend
{
    /// <summary>Resolves the caller once: live principals, the permission's roles and type. False when nothing can be visible.</summary>
    Task<bool> BeginAsync(CancellationToken cancellationToken);

    long RootNode { get; }

    /// <summary>One direct stream per (live principal, role with the permission).</summary>
    IReadOnlyList<(string Principal, string Role)> DirectStreams { get; }

    Task<SqlOSFgaRound> RoundAsync(IReadOnlyList<SqlOSFgaOpenRequest> opens, IReadOnlyList<SqlOSFgaStream> streams, CancellationToken cancellationToken);
}

/// <summary>An open stream of the page: where it reads from, how far it has been read, and its split state.</summary>
internal sealed class SqlOSFgaStream(int id, SqlOSFgaStreamKind kind, int level, long node, string? principal, string? role, int threshold, object?[]? after)
{
    public int Id { get; } = id;
    public SqlOSFgaStreamKind Kind { get; } = kind;
    public int Level { get; } = level;
    public long Node { get; } = node;
    public string? Principal { get; } = principal;
    public string? Role { get; } = role;
    public int Threshold { get; } = threshold;
    public int Denials { get; set; }
    public bool Closed { get; set; }
    public bool Exhausted { get; set; }

    /// <summary>The last position consumed from the stream (null: nothing yet, read from the page's cursor).</summary>
    public object?[]? After { get; set; } = after;

    /// <summary>The batch size asked of the stream in the last round.</summary>
    public int Fetch { get; set; }

    public bool IsOpen => !Closed && !Exhausted;
}

/// <summary>Every operation of a page, in the units the database pays for; measured, none estimated.</summary>
internal sealed class SqlOSFgaPageCounters
{
    public int Rounds;
    public int Statements;
    public int RowsFetched;
    public int RowsJudged;
    public int Emitted;
    public int Denials;
    public int Duplicates;
    public int Discarded;
    public int StreamsOpened;
    public int ReachStreams;
    public int StructuralStreams;
    public int DirectStreams;
    public int Splits;
    public int EagerSplits;
    public int NodesExamined;
    public int CutNodes;

    /// <summary>Levels passed through on the way to an opened node: ungranted nodes with exactly one grant-bearing child.</summary>
    public int Collapsed;

    /// <summary>Principals whose grant counts had fallen behind the clock (a grant's window opened or closed) and were rebuilt before the walk.</summary>
    public int CountsRebuilt;

    /// <summary>Milliseconds spent resolving the caller's principals and the permission, before the walk.</summary>
    public double ResolveMs;

    /// <summary>Milliseconds spent in the walk: the prelude and every round.</summary>
    public double WalkMs;

    /// <summary>Milliseconds spent loading the page's rows through the application's query.</summary>
    public double LoadMs;

    public override string ToString()
        => $"rounds {Rounds} ({Statements} statements), fetched {RowsFetched}, judged {RowsJudged}, denials {Denials}, discarded {Discarded}, "
           + $"streams reach {ReachStreams} / structural {StructuralStreams} / direct {DirectStreams}, splits {Splits} (+{EagerSplits} eager), nodes {NodesExamined}, collapsed {Collapsed}, "
           + $"counts rebuilt {CountsRebuilt}, ms resolve {ResolveMs:F1} / walk {WalkMs:F1} / load {LoadMs:F1}";
}

/// <summary>
/// The cost-based adaptive walk that answers a page (see paper/adaptive-walk.md): one stream at the root;
/// rows merged in page order; a structural stream splits into its grant-bearing children after as many
/// denials as it has such children (the denials pay for the seeks); a granted node streams its reach-indexed
/// rows and never splits; an inactive node is expanded straight into its grant-bearing children, and the
/// inactive descendants holding grants below an opened node are expanded as well (cut discovery), because a
/// level's index holds a row only where access flows down to it from that level. Rows granted directly on
/// their own resource come from the direct index as one more stream per principal and role.
/// </summary>
/// <remarks>
/// Each round is one round trip: every node to open and every stream to refill goes in one batch, and the
/// database returns the rows of all streams merged in order. The executor consumes them in that order up to
/// the first row that ends a full batch (a stream whose next row the round did not fetch may hold the next
/// row of the page), judging each: a reach or direct row is visible; a structural row is visible when the
/// backend's row test says so, otherwise it is a denial. A split, or a full page, ends the round early; rows
/// not consumed are fetched again in the next round from their streams' unchanged positions.
/// </remarks>
internal sealed class SqlOSFgaPageExecutor(ISqlOSFgaPageBackend backend)
{
    private readonly List<SqlOSFgaStream> _streams = [];
    private readonly Dictionary<(SqlOSFgaStreamKind Kind, int Level, long Node, string? Principal, string? Role), SqlOSFgaStream> _byIdentity = [];
    private readonly List<SqlOSFgaOpenRequest> _opens = [];
    private readonly HashSet<(long Node, bool Children, bool CutMode)> _requested = [];
    private int _nextRequest;
    private int _remaining;

    public SqlOSFgaPageCounters Counters { get; } = new();

    /// <summary>The positions (order values, key last) of the next <paramref name="pageSize"/> visible rows after <paramref name="cursor"/>.</summary>
    public async Task<List<object?[]>> PageAsync(object?[]? cursor, int pageSize, CancellationToken cancellationToken)
    {
        var page = new List<object?[]>(pageSize);
        if (pageSize <= 0)
        {
            return page;
        }

        var begun = await backend.BeginAsync(cancellationToken);
        Counters.Rounds++;
        Counters.Statements++;
        if (!begun)
        {
            return page;
        }

        _remaining = pageSize;
        Request(backend.RootNode, children: false, level: 0, cursor, cutMode: false);
        foreach (var (principal, role) in backend.DirectStreams)
        {
            Add(new SqlOSFgaStream(_streams.Count, SqlOSFgaStreamKind.Direct, -1, 0, principal, role, threshold: 0, cursor));
            Counters.DirectStreams++;
        }

        object? lastEmittedKey = null;
        while (page.Count < pageSize)
        {
            var open = _streams.Where(s => s.IsOpen).ToList();
            if (_opens.Count == 0 && open.Count == 0)
            {
                break;
            }

            var round = await RoundAsync(open, cancellationToken);

            // The rows, in the database's order, up to the first row that ends a full batch. When the round
            // found nodes still to open (the grant-bearing children of an inactive node, a cut below an opened
            // one), their streams start at the position those nodes were opened at and may hold rows before
            // any row fetched here, so nothing is consumed until they have joined the merge.
            var consumed = new Dictionary<SqlOSFgaStream, int>();
            var stop = _opens.Count > 0;
            foreach (var row in stop ? [] : round.Rows)
            {
                if (!_byIdentity.TryGetValue((row.Kind, row.Level, row.Node, row.Principal, row.Role), out var stream) || !stream.IsOpen)
                {
                    continue;
                }

                stream.After = row.Position;
                consumed[stream] = consumed.GetValueOrDefault(stream) + 1;
                Counters.RowsJudged++;

                var key = row.Position[^1];
                if (lastEmittedKey is not null && Equals(key, lastEmittedKey))
                {
                    // The same row from a second stream: a direct grant beside a reach stream, or two principals
                    // granted on one resource.
                    Counters.Duplicates++;
                }
                else if (row.Kind == SqlOSFgaStreamKind.Structural && !row.Granted)
                {
                    Counters.Denials++;
                    if (++stream.Denials >= stream.Threshold)
                    {
                        Split(stream, row.Position);
                        stop = true;
                    }
                }
                else
                {
                    page.Add(row.Position);
                    lastEmittedKey = key;
                    Counters.Emitted++;
                    _remaining--;
                    if (page.Count == pageSize)
                    {
                        stop = true;
                    }
                }

                if (row.Ordinal == row.Count && row.Count >= stream.Fetch)
                {
                    // The last row of a full batch: the stream's next row was not fetched and may precede
                    // every row that follows here.
                    stop = true;
                }

                if (stop)
                {
                    break;
                }
            }

            // A stream whose batch was short and fully consumed has no more rows, and so has one the round
            // fetched nothing for (every open stream is fetched in every round, the ones opened in it included).
            var fetched = new HashSet<SqlOSFgaStream>();
            foreach (var row in round.Rows)
            {
                if (_byIdentity.TryGetValue((row.Kind, row.Level, row.Node, row.Principal, row.Role), out var stream))
                {
                    fetched.Add(stream);
                    if (stream.IsOpen && row.Count < stream.Fetch && consumed.GetValueOrDefault(stream) == row.Count)
                    {
                        stream.Exhausted = true;
                    }
                }
            }

            foreach (var stream in _streams)
            {
                if (stream.IsOpen && !fetched.Contains(stream))
                {
                    stream.Exhausted = true;
                }
            }

            Counters.Discarded += round.Rows.Count - consumed.Values.Sum();
        }

        return page;
    }

    private void Add(SqlOSFgaStream stream)
    {
        // One stream per node: a node can be reached twice (through the cut below an ancestor and through
        // that ancestor's split); the first stream, which starts no later, is the one.
        if (!_byIdentity.TryAdd((stream.Kind, stream.Level, stream.Node, stream.Principal, stream.Role), stream))
        {
            return;
        }

        _streams.Add(stream);
        Counters.StreamsOpened++;
    }

    /// <summary>Queues a node to open in the next round, once per node and mode for the whole page.</summary>
    private void Request(long node, bool children, int level, object?[]? after, bool cutMode)
    {
        if (_requested.Add((node, children, cutMode)))
        {
            _opens.Add(new SqlOSFgaOpenRequest(_nextRequest++, node, children, level, after, Fetch: 0, cutMode));
        }
    }

    private void Split(SqlOSFgaStream stream, object?[] lastPopped)
    {
        Counters.Splits++;
        stream.Closed = true;
        Request(stream.Node, children: true, stream.Level + 1, lastPopped, cutMode: false);
    }

    /// <summary>
    /// Batch sizes: a stream's share of the rows the page still needs, at least two; a node opened on its own
    /// gets that share, the children of a split node divide the rows still needed among themselves (the
    /// backend knows their number); a structural stream may fetch up to its split threshold on top, so one
    /// batch can carry it to its split. Rows a batch brings that the merge does not reach are fetched again.
    /// </summary>
    private async Task<SqlOSFgaRound> RoundAsync(List<SqlOSFgaStream> open, CancellationToken cancellationToken)
    {
        var active = open.Count + _opens.Count;
        var share = Math.Max(2, (int)Math.Ceiling(_remaining / (double)Math.Max(1, active)));
        var opens = _opens.Select(o => o with { Fetch = o.Children ? Math.Max(2, _remaining) : Math.Min(_remaining, share) }).ToList();
        _opens.Clear();
        foreach (var s in open)
        {
            s.Fetch = s.Kind == SqlOSFgaStreamKind.Structural
                ? Math.Max(2, Math.Min(_remaining + Math.Max(1, s.Threshold - s.Denials), Math.Max(share, s.Threshold - s.Denials)))
                : Math.Min(_remaining, share);
        }

        var round = await backend.RoundAsync(opens, open, cancellationToken);
        Counters.Rounds++;
        Counters.Statements += round.Statements;

        var byRequest = opens.ToDictionary(o => o.Id);
        foreach (var node in round.Opened)
        {
            var request = byRequest[node.RequestId];
            Counters.NodesExamined++;
            Counters.Collapsed += Math.Max(0, node.Level - request.Level);
            if (!node.Active)
            {
                // Every row of an inactive node is cut below it; the grants under it are what can still reach them.
                if (node.Threshold > 0)
                {
                    Counters.EagerSplits++;
                    Request(node.Node, children: true, node.Level + 1, request.After, cutMode: false);
                }

                continue;
            }

            if (request.CutMode)
            {
                // An active node below an opened one: its rows are in that node's stream already; only what
                // lies behind an inactive descendant is not.
                Counters.CutNodes++;
                if (node.CutGrants > 0)
                {
                    Request(node.Node, children: true, node.Level + 1, request.After, cutMode: true);
                }

                continue;
            }

            if (node.Granted && node.HasChildren)
            {
                var s = new SqlOSFgaStream(_streams.Count, SqlOSFgaStreamKind.Reach, node.Level, node.Node, null, null, 0, request.After) { Fetch = node.Fetch };
                Add(s);
                Counters.ReachStreams++;
            }
            else if (node.Granted)
            {
                // Granted, without children: its rows are its own, and the direct index streams them.
                continue;
            }
            else if (node.Threshold > 0)
            {
                var s = new SqlOSFgaStream(_streams.Count, SqlOSFgaStreamKind.Structural, node.Level, node.Node, null, null, node.Threshold, request.After) { Fetch = node.Fetch };
                Add(s);
                Counters.StructuralStreams++;
            }
            else
            {
                continue;
            }

            if (node.CutGrants > 0)
            {
                Request(node.Node, children: true, node.Level + 1, request.After, cutMode: true);
            }
        }

        Counters.RowsFetched += round.Rows.Count;
        return round;
    }
}
