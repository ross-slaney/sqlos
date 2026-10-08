namespace SqlOS.Benchmarks.Data;

/// <summary>
/// Loads the dataset into the shipped SqlOS tables with each engine's bulk path. Secondary indexes are set
/// aside during a load and rebuilt from their own definitions afterwards, so the schema under test is exactly
/// the one SqlOS created. The lineage of every resource and the scope columns of every product are generated
/// by the harness and bulk copied with the rows; the first scale verifies them against SqlOS's own rebuild.
/// </summary>
internal interface IDatasetLoader
{
    Task ConfigureDatabaseAsync(CancellationToken cancellationToken);

    /// <summary>The sequence numbers of the resource types SqlOS seeded.</summary>
    Task<IReadOnlyDictionary<string, int>> ReadTypeSeqsAsync(CancellationToken cancellationToken);

    /// <summary>The root resource's sequence number.</summary>
    Task<long> ReadRootSeqAsync(string rootId, CancellationToken cancellationToken);

    /// <summary>Loads every organizational node with its lineage, and the stores table.</summary>
    Task LoadHierarchyAsync(RetailTree tree, CancellationToken cancellationToken);

    /// <summary>
    /// Adds products <c>(from, to]</c> and their FGA resources, lineage and scope columns included, brings
    /// indexes and statistics current, and moves the resource sequence past the loaded numbers.
    /// </summary>
    Task<LoadTiming> GrowProductsAsync(RetailTree tree, IReadOnlyDictionary<string, int> typeSeq, long from, long to, CancellationToken cancellationToken);

    /// <summary>
    /// Row counts and order-independent hashes of the lineage (every resource's depth, reach and ancestors)
    /// and of the products' scope columns, for comparing a rebuild with the load.
    /// </summary>
    Task<LineageChecksum> LineageChecksumAsync(CancellationToken cancellationToken);

    /// <summary>Data plus index size of the benchmark database, in bytes.</summary>
    Task<long> DatabaseSizeBytesAsync(CancellationToken cancellationToken);

    Task<string> EngineVersionAsync(CancellationToken cancellationToken);
}

internal sealed record LoadTiming(TimeSpan Rows, TimeSpan Indexes, TimeSpan Maintenance)
{
    public TimeSpan Total => Rows + Indexes + Maintenance;
}

/// <param name="Resources">Resources with a lineage (a depth), and a hash over every lineage column.</param>
/// <param name="Products">Products with scope columns (a reach), and a hash over every scope column.</param>
internal sealed record LineageChecksum((long Rows, decimal Hash) Resources, (long Rows, decimal Hash) Products)
{
    public bool Equals(LineageChecksum? other)
        => other is not null && Resources == other.Resources && Products == other.Products;

    public override int GetHashCode() => HashCode.Combine(Resources, Products);

    public override string ToString() => $"{Resources.Rows:N0} resources / {Products.Rows:N0} products";
}
