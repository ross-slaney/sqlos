namespace SqlOS.Benchmarks.Data;

/// <summary>
/// Loads the dataset into the shipped SqlOS tables with each engine's bulk path. Secondary indexes are set
/// aside during a load and rebuilt from their own definitions afterwards, so the schema under test is exactly
/// the one SqlOS created.
/// </summary>
internal interface IDatasetLoader
{
    Task ConfigureDatabaseAsync(CancellationToken cancellationToken);

    /// <summary>Loads every organizational node and the stores table.</summary>
    Task LoadHierarchyAsync(RetailTree tree, CancellationToken cancellationToken);

    /// <summary>Adds products <c>(from, to]</c>, their FGA resources, and brings indexes and statistics current.</summary>
    Task<LoadTiming> GrowProductsAsync(RetailTree tree, long from, long to, CancellationToken cancellationToken);

    /// <summary>Data plus index size of the benchmark database, in bytes.</summary>
    Task<long> DatabaseSizeBytesAsync(CancellationToken cancellationToken);

    Task<string> EngineVersionAsync(CancellationToken cancellationToken);
}

internal sealed record LoadTiming(TimeSpan Rows, TimeSpan Indexes, TimeSpan Maintenance)
{
    public TimeSpan Total => Rows + Indexes + Maintenance;
}
