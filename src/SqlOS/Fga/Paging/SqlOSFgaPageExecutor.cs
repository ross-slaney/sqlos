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
/// What the executor asks of the database, per round trip: open nodes, fetch the next rows of the streams
/// that need them (those of the nodes just opened included), and merge them with the rows fetched earlier
/// and not yet consumed into one order. The executor never compares two keys itself, so every column type
/// sorts exactly as the database sorts it.
/// </summary>
internal interface ISqlOSFgaPageBackend
{
    /// <summary>Resolves the caller once: live principals, the permission's roles and type. False when nothing can be visible.</summary>
    Task<bool> BeginAsync(CancellationToken cancellationToken);

    long RootNode { get; }

    /// <summary>One direct stream per (live principal, role with the permission).</summary>
    IReadOnlyList<(string Principal, string Role)> DirectStreams { get; }

    /// <summary>
    /// One round trip. <paramref name="streams"/> are fetched past <see cref="SqlOSFgaStream.ReadFrom"/>;
    /// <paramref name="retained"/> are rows of earlier rounds the executor still holds; the result's rows are
    /// both together in the page's order.
    /// </summary>
    Task<SqlOSFgaRound> RoundAsync(IReadOnlyList<SqlOSFgaOpenRequest> opens, IReadOnlyList<SqlOSFgaStream> streams, IReadOnlyList<SqlOSFgaFetchedRow> retained, CancellationToken cancellationToken);
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

    /// <summary>The position of the last row fetched from the stream, consumed or not; null before its first batch.</summary>
    public object?[]? FetchedThrough { get; set; }

    /// <summary>Where the next batch reads from: past every row fetched so far, which the executor still holds or has consumed.</summary>
    public object?[]? ReadFrom => FetchedThrough ?? After;

    /// <summary>The batch size asked of the stream in the last round.</summary>
    public int Fetch { get; set; }

    /// <summary>Rows fetched from the stream and not yet consumed: held by the executor, merged again in every round.</summary>
    public int Buffered { get; set; }

    /// <summary>The last batch brought fewer rows than asked: the stream ends with it.</summary>
    public bool LastBatchShort { get; set; }

    public bool IsOpen => !Closed && !Exhausted;
}

/// <summary>Every operation of a page, in the units the database pays for; measured, none estimated.</summary>
internal sealed class SqlOSFgaPageCounters
{
    public int Rounds;
    public int Statements;

    /// <summary>Index rows fetched from the streams, each once: a row held between rounds is not fetched again.</summary>
    public int RowsFetched;

    /// <summary>Rows held between rounds and sent back to be merged with the next round's rows, summed over rounds.</summary>
    public int RowsRetained;
    public int RowsJudged;
    public int Emitted;
    public int Denials;
    public int Duplicates;

    /// <summary>Rows dropped unconsumed: those of a stream that split.</summary>
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
        => $"rounds {Rounds} ({Statements} statements), fetched {RowsFetched}, retained {RowsRetained}, judged {RowsJudged}, denials {Denials}, discarded {Discarded}, "
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
/// <para>
/// Each round is one round trip: every node to open and every stream whose fetched rows are used up go in
/// one batch, and the database returns their rows merged, in order, with the rows of earlier rounds the
/// executor still holds. The executor consumes the merged rows in that order, judging each: a reach or
/// direct row is visible; a structural row is visible when the backend's row test says so, otherwise it is
/// a denial.
/// </para>
/// <para>
/// The invariant that keeps the order exact: a row is consumed only while every open stream (not closed by
/// a split, not exhausted) has a fetched row at or past it, and no node is waiting to be opened. Every row
/// such a stream has not fetched yet lies past its last fetched row, so past the one consumed; a stream
/// whose last batch was short has no row left at all; a node still to open (the grant-bearing children of
/// an inactive node, a cut below an opened one) would start a stream at the position it was opened at, so
/// nothing is consumed until its stream has joined the merge. Consuming the last fetched row of a stream
/// that may have more therefore ends the round, as does a split or a full page. Two positions are kept per
/// stream: where it was consumed to (<see cref="SqlOSFgaStream.After"/>, where its children start when it
/// splits) and where it was fetched to (<see cref="SqlOSFgaStream.FetchedThrough"/>, where its refill reads
/// from). Rows fetched and not consumed are held, not fetched again, and go back to the database with the
/// next round to be merged with what it fetches; only the rows of a stream that split are dropped, its
/// children starting from the position it was consumed to. A row the merge shows twice (one row in two
/// streams: a direct grant beside a reach stream, two principals granted on one resource) is adjacent to
/// its twin in the merge, whether both were fetched in one round or one was held, and the second is skipped.
/// </para>
/// <para>
/// A row held between rounds was fetched at an earlier moment of the same page: a change to it, or to the
/// grants, between that moment and the page's end is seen the way a change between two of the page's
/// statements always was; the page's rows are loaded by key at the end.
/// </para>
/// </remarks>
internal sealed class SqlOSFgaPageExecutor(ISqlOSFgaPageBackend backend)
{
    private readonly List<SqlOSFgaStream> _streams = [];
    private readonly Dictionary<(SqlOSFgaStreamKind Kind, int Level, long Node, string? Principal, string? Role), SqlOSFgaStream> _byIdentity = [];
    private readonly List<SqlOSFgaOpenRequest> _opens = [];
    private readonly HashSet<(long Node, bool Children, bool CutMode)> _requested = [];
    private readonly List<SqlOSFgaStream> _openedThisRound = [];
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
        IReadOnlyList<SqlOSFgaFetchedRow> retained = [];
        while (page.Count < pageSize)
        {
            var refill = _streams.Where(s => s.IsOpen && s.Buffered == 0).ToList();
            if (_opens.Count == 0 && refill.Count == 0 && retained.Count == 0)
            {
                break;
            }

            // Everything still needed may already be held (a split whose children were opened before): then
            // the held rows, already in order, are the round.
            var asked = new HashSet<SqlOSFgaStream>();
            var round = _opens.Count == 0 && refill.Count == 0
                ? new SqlOSFgaRound([], retained, 0)
                : await RoundAsync(refill, retained, asked, cancellationToken);

            // What each open stream now holds, and what the streams fetched this round brought.
            foreach (var stream in _streams)
            {
                stream.Buffered = 0;
            }

            var fresh = new Dictionary<SqlOSFgaStream, int>();
            foreach (var row in round.Rows)
            {
                if (TryStream(row, out var stream))
                {
                    stream.Buffered++;
                    if (asked.Contains(stream))
                    {
                        fresh[stream] = fresh.GetValueOrDefault(stream) + 1;
                        stream.FetchedThrough = row.Position;
                    }
                }
            }

            foreach (var stream in asked)
            {
                var count = fresh.GetValueOrDefault(stream);
                stream.LastBatchShort = count < stream.Fetch;
                if (count == 0 && stream.IsOpen)
                {
                    stream.Exhausted = true;
                }
            }

            // The rows, in the database's order. When the round found nodes still to open (the grant-bearing
            // children of an inactive node, a cut below an opened one), their streams start at the position
            // those nodes were opened at and may hold rows before any row held here, so nothing is consumed
            // until they have joined the merge.
            var consumedThrough = -1;
            var stop = _opens.Count > 0;
            for (var i = 0; !stop && i < round.Rows.Count; i++)
            {
                var row = round.Rows[i];
                consumedThrough = i;
                if (!TryStream(row, out var stream) || !stream.IsOpen)
                {
                    // A row of a stream that split: its children cover it.
                    Counters.Discarded++;
                    continue;
                }

                stream.After = row.Position;
                stream.Buffered--;
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

                if (stream.Buffered == 0 && stream.IsOpen)
                {
                    if (stream.LastBatchShort)
                    {
                        // Its last batch was short: there is nothing after this row.
                        stream.Exhausted = true;
                    }
                    else
                    {
                        // Its next row was not fetched and may precede every row that follows here.
                        stop = true;
                    }
                }
            }

            var held = new List<SqlOSFgaFetchedRow>();
            for (var i = consumedThrough + 1; i < round.Rows.Count; i++)
            {
                if (TryStream(round.Rows[i], out var stream) && stream.IsOpen)
                {
                    held.Add(round.Rows[i]);
                }
            }

            Counters.Discarded += round.Rows.Count - (consumedThrough + 1) - held.Count;
            retained = held;
        }

        return page;
    }

    private bool TryStream(SqlOSFgaFetchedRow row, out SqlOSFgaStream stream)
        => _byIdentity.TryGetValue((row.Kind, row.Level, row.Node, row.Principal, row.Role), out stream!);

    private void Add(SqlOSFgaStream stream)
    {
        // One stream per node: a node can be reached twice (through the cut below an ancestor and through
        // that ancestor's split); the first stream, which starts no later, is the one.
        if (!_byIdentity.TryAdd((stream.Kind, stream.Level, stream.Node, stream.Principal, stream.Role), stream))
        {
            return;
        }

        _streams.Add(stream);
        _openedThisRound.Add(stream);
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
    /// Batch sizes: a stream's share of the rows the page still needs among the streams fetched this round,
    /// at least two; a node opened on its own gets that share, the children of a split node divide the rows
    /// still needed among themselves (the backend knows their number); a structural stream may fetch up to
    /// its split threshold on top, so one batch can carry it to its split. A stream fetched alone, the one
    /// whose rows the page is consuming, gets the whole of what the page still needs.
    /// </summary>
    private async Task<SqlOSFgaRound> RoundAsync(List<SqlOSFgaStream> refill, IReadOnlyList<SqlOSFgaFetchedRow> retained, HashSet<SqlOSFgaStream> asked, CancellationToken cancellationToken)
    {
        var active = refill.Count + _opens.Count;
        var share = Math.Max(2, (int)Math.Ceiling(_remaining / (double)Math.Max(1, active)));
        var opens = _opens.Select(o => o with { Fetch = o.Children ? Math.Max(2, _remaining) : Math.Min(_remaining, share) }).ToList();
        _opens.Clear();
        foreach (var s in refill)
        {
            s.Fetch = s.Kind == SqlOSFgaStreamKind.Structural
                ? Math.Max(2, Math.Min(_remaining + Math.Max(1, s.Threshold - s.Denials), Math.Max(share, s.Threshold - s.Denials)))
                : Math.Min(_remaining, share);
            asked.Add(s);
        }

        _openedThisRound.Clear();
        var round = await backend.RoundAsync(opens, refill, retained, cancellationToken);
        Counters.Rounds++;
        Counters.Statements += round.Statements;
        Counters.RowsRetained += retained.Count;

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

        foreach (var s in _openedThisRound)
        {
            asked.Add(s);
        }

        Counters.RowsFetched += round.Rows.Count - retained.Count;
        return round;
    }
}
