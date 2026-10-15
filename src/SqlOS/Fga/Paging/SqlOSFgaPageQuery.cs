namespace SqlOS.Fga.Paging;

/// <summary>
/// The page loop, over two reads it does not know how to run. Read 1 ("your query, checked")
/// reads the next <c>b</c> rows of the application's query in its order with a visibility flag.
/// Read 2 ("the caller's nodes, in order") reads up to <c>b</c> entries under each of the caller's
/// granted nodes and merges them in order, up to the page size. The budget <c>b</c> starts at the
/// page size and grows four times per round until one side fills the page or runs out.
/// </summary>
internal static class SqlOSFgaPageQuery
{
    /// <summary>A row of read 1: its order values (ending with the key) and whether the caller may see it.</summary>
    internal sealed record Read1Row(IReadOnlyList<object> Values, bool Visible);

    /// <summary>A row of read 2: its order values (ending with the key) and the node it was read under.</summary>
    internal sealed record Read2Row(IReadOnlyList<object> Values, string NodeId);

    /// <summary>The page's rows, in order, and how they were found.</summary>
    internal sealed record Selection(
        IReadOnlyList<IReadOnlyList<object>> Rows,
        bool HasMore,
        string Source,
        IReadOnlyList<int> Read1RowsPerRound,
        IReadOnlyList<IReadOnlyDictionary<string, int>> Read2EntriesPerRound)
    {
        public int Rounds => Read1RowsPerRound.Count;
    }

    public const string FromRead1 = "read 1";
    public const string FromRead2 = "read 2";

    /// <param name="pageSize">The page size <c>P</c>.</param>
    /// <param name="read1">Reads <c>b</c> rows of the application's query from the cursor.</param>
    /// <param name="read2">
    /// Reads up to <c>b</c> entries per node from the cursor, at most <c>P</c> merged;
    /// <see langword="null"/> when no index covers the order.
    /// </param>
    /// <param name="cancellationToken">A token that cancels the reads.</param>
    public static async Task<Selection> SelectAsync(
        int pageSize,
        Func<int, CancellationToken, Task<IReadOnlyList<Read1Row>>> read1,
        Func<int, CancellationToken, Task<IReadOnlyList<Read2Row>>>? read2,
        CancellationToken cancellationToken)
    {
        var read1Rows = new List<int>();
        var read2Entries = new List<IReadOnlyDictionary<string, int>>();
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
                return new Selection(visible.Take(pageSize).ToList(), hasMore, FromRead1, read1Rows, read2Entries);
            }

            if (rows.Count < budget)
            {
                // The application's query is exhausted: every visible row is on this page.
                return new Selection(visible, false, FromRead1, read1Rows, read2Entries);
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
                    return new Selection(entries.Take(pageSize).Select(entry => entry.Values).ToList(), true, FromRead2, read1Rows, read2Entries);
                }

                // With b at least P, fewer than P entries means every node returned fewer than b:
                // read 2 settles the page in the round it runs.
                if (perNode.Values.All(count => count < budget))
                {
                    // Every node ran out: these are all the rows the caller can see past the cursor.
                    return new Selection(entries.Select(entry => entry.Values).ToList(), false, FromRead2, read1Rows, read2Entries);
                }
            }

            budget = (int)Math.Min((long)budget * 4, int.MaxValue);
        }
    }
}
