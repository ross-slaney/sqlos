namespace SqlOS.Fga.Paging;

/// <summary>
/// The page loop, over three reads it does not know how to run. Read 1 ("your query, checked")
/// reads the next <c>b</c> rows of the application's query in its order with a visibility flag.
/// Read 2 ("the caller's nodes, in order") reads up to <c>b</c> entries of a declared order under
/// each of the caller's granted nodes and merges them in order, up to the page size. Read 3 ("the
/// caller's rows, sorted") runs for an order no page order declares: it reads up to <c>b + 1</c> of
/// the caller's row keys from the node index and, when there are at most <c>b</c>, sorts those rows
/// through the application's query. The budget <c>b</c> starts at the page size and grows four times
/// per round until one read settles the page: read 1 first in every round, then read 2 or read 3.
/// </summary>
internal static class SqlOSFgaPageQuery
{
    /// <summary>A row of read 1: its order values (ending with the key) and whether the caller may see it.</summary>
    internal sealed record Read1Row(IReadOnlyList<object> Values, bool Visible);

    /// <summary>A row of read 2: its order values (ending with the key) and the node it was read under.</summary>
    internal sealed record Read2Row(IReadOnlyList<object> Values, string NodeId);

    /// <summary>
    /// What read 3 found in one round: how many keys it read under the caller's nodes (at most
    /// <c>b + 1</c>), and, when there were at most <c>b</c>, the caller's rows after the cursor in
    /// order (each its order values, ending with the key), at most the page size plus one.
    /// <see cref="Rows"/> is <see langword="null"/> when the caller has more than <c>b</c> rows.
    /// </summary>
    internal sealed record Read3Result(int Keys, IReadOnlyList<IReadOnlyList<object>>? Rows);

    /// <summary>The page's rows, in order, and how they were found.</summary>
    internal sealed record Selection(
        IReadOnlyList<IReadOnlyList<object>> Rows,
        bool HasMore,
        string Source,
        IReadOnlyList<int> Read1RowsPerRound,
        IReadOnlyList<IReadOnlyDictionary<string, int>> Read2EntriesPerRound,
        IReadOnlyList<int> Read3KeysPerRound)
    {
        public int Rounds => Read1RowsPerRound.Count;
    }

    public const string FromRead1 = "read 1";
    public const string FromRead2 = "read 2";
    public const string FromRead3 = "read 3";

    /// <param name="pageSize">The page size <c>P</c>.</param>
    /// <param name="read1">Reads <c>b</c> rows of the application's query from the cursor.</param>
    /// <param name="read2">
    /// Reads up to <c>b</c> entries per node from the cursor, at most <c>P</c> merged;
    /// <see langword="null"/> when the order is not declared.
    /// </param>
    /// <param name="read3">
    /// Reads up to <c>b + 1</c> of the caller's keys and, with at most <c>b</c>, their rows after the
    /// cursor, at most <c>P + 1</c>; <see langword="null"/> when the order is declared or the entity
    /// has no usable node index.
    /// </param>
    /// <param name="cancellationToken">A token that cancels the reads.</param>
    public static async Task<Selection> SelectAsync(
        int pageSize,
        Func<int, CancellationToken, Task<IReadOnlyList<Read1Row>>> read1,
        Func<int, CancellationToken, Task<IReadOnlyList<Read2Row>>>? read2,
        Func<int, CancellationToken, Task<Read3Result>>? read3,
        CancellationToken cancellationToken)
    {
        if (read2 != null && read3 != null)
        {
            throw new ArgumentException("A declared order runs read 2, never read 3.", nameof(read3));
        }

        var read1Rows = new List<int>();
        var read2Entries = new List<IReadOnlyDictionary<string, int>>();
        var read3Keys = new List<int>();
        var budget = pageSize;
        while (true)
        {
            var rows = await read1(budget, cancellationToken);
            read1Rows.Add(rows.Count);
            var visible = rows.Where(row => row.Visible).Select(row => row.Values).ToList();

            if (visible.Count >= pageSize)
            {
                // More rows exist unless read 1 ran out with nothing visible past the page.
                var hasMore = visible.Count > pageSize || rows.Count == budget;
                return new Selection(visible.Take(pageSize).ToList(), hasMore, FromRead1, read1Rows, read2Entries, read3Keys);
            }

            if (rows.Count < budget)
            {
                // The application's query is exhausted: every visible row is on this page.
                return new Selection(visible, false, FromRead1, read1Rows, read2Entries, read3Keys);
            }

            if (read2 != null)
            {
                var entries = await read2(budget, cancellationToken);
                var perNode = entries
                    .GroupBy(entry => entry.NodeId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                read2Entries.Add(perNode);

                if (entries.Count >= pageSize)
                {
                    return new Selection(entries.Take(pageSize).Select(entry => entry.Values).ToList(), true, FromRead2, read1Rows, read2Entries, read3Keys);
                }

                // With b at least P, fewer than P entries means every node returned fewer than b:
                // read 2 settles the page in the round it runs.
                if (perNode.Values.All(count => count < budget))
                {
                    // Every node ran out: these are all the rows the caller can see past the cursor.
                    return new Selection(entries.Select(entry => entry.Values).ToList(), false, FromRead2, read1Rows, read2Entries, read3Keys);
                }
            }

            if (read3 != null)
            {
                var found = await read3(budget, cancellationToken);
                read3Keys.Add(found.Keys);
                if (found.Rows is { } callerRows)
                {
                    // The caller has at most b rows, so these are all of its rows after the cursor.
                    return new Selection(callerRows.Take(pageSize).ToList(), callerRows.Count > pageSize, FromRead3, read1Rows, read2Entries, read3Keys);
                }
            }

            budget = (int)Math.Min((long)budget * 4, int.MaxValue);
        }
    }
}
