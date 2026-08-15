namespace SqlOS.Pagination;

/// <summary>
/// Cursor-paginated admin list result. Exact <c>totalCount</c> and <c>totalPages</c>
/// are intentionally absent from the hot-path contract.
/// </summary>
public sealed class SqlOSCursorPage<T>
{
    public SqlOSCursorPage(IReadOnlyList<T> data, int pageSize, string? nextCursor)
    {
        Data = data;
        PageSize = pageSize;
        NextCursor = nextCursor;
    }

    public IReadOnlyList<T> Data { get; }
    public int PageSize { get; }
    public string? NextCursor { get; }
    public bool HasNextPage => NextCursor != null;

    public static SqlOSCursorPage<T> Create(IReadOnlyList<T> data, int pageSize, string? nextCursor)
        => new(data, pageSize, nextCursor);

    public object ToResponse()
        => new
        {
            Data,
            PageSize,
            NextCursor,
            HasNextPage
        };

    public object ToResponse<TOut>(Func<T, TOut> selector)
        => new
        {
            Data = Data.Select(selector).ToList(),
            PageSize,
            NextCursor,
            HasNextPage
        };
}
