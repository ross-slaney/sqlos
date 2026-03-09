namespace SqlOS.Fga.Specifications;

/// <summary>
/// Cursor-based paginated result wrapper.
/// </summary>
public class PaginatedResult<T>
{
    public List<T> Data { get; set; } = new();
    public int PageSize { get; set; }
    public string? NextCursor { get; set; }
    public bool HasNextPage => NextCursor != null;

    public PaginatedResult() { }

    public PaginatedResult(List<T> data, int pageSize, string? nextCursor)
    {
        Data = data;
        PageSize = pageSize;
        NextCursor = nextCursor;
    }

    public static PaginatedResult<T> Create(List<T> data, int pageSize, string? nextCursor)
        => new(data, pageSize, nextCursor);

    public static PaginatedResult<T> Empty(int pageSize = 10)
        => new(new List<T>(), pageSize, null);
}
