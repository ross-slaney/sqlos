using System.Linq.Expressions;
using System.Text;

namespace SqlOS.Fga.Specifications;

/// <summary>
/// Base class for specifications that define filtering, sorting, and cursor-based pagination criteria.
///
/// <para><b>Cursor Pagination Contract:</b></para>
/// <para>
/// Three methods work together to implement keyset pagination. They MUST stay in sync:
/// </para>
/// <list type="number">
///   <item><see cref="ApplySort"/> defines the deterministic sort order (e.g., <c>OrderBy(Name).ThenBy(Id)</c>).</item>
///   <item><see cref="BuildCursor"/> encodes the sort column value(s) + tiebreaker from the last entity on a page.</item>
///   <item><see cref="GetCursorFilter"/> decodes the cursor and returns a WHERE clause that skips all rows at or before that position, using the same column comparisons as the sort order.</item>
/// </list>
///
/// <para><b>Example:</b> If ApplySort sorts by <c>Name, Id</c>:</para>
/// <code>
/// BuildCursor(e)     => EncodeCursor(e.Name, e.Id);
/// GetCursorFilter(c) => { var (name, id) = DecodeCursor(c);
///                         return e => e.Name.CompareTo(name) > 0
///                                  || (e.Name == name &amp;&amp; e.Id.CompareTo(id) > 0); }
/// </code>
///
/// <para>
/// The tiebreaker (typically <c>Id</c>) ensures deterministic ordering when the primary sort
/// column has duplicate values. Without it, rows with the same sort value could be skipped or
/// duplicated across pages.
/// </para>
/// </summary>
public abstract class PagedSpecification<T> where T : class
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 10;

    public int PageSize { get; set; } = DefaultPageSize;
    public string? Cursor { get; set; }

    public int Take => SafePageSize;
    public int SafePageSize => Math.Clamp(PageSize, 1, MaxPageSize);

    /// <summary>
    /// Defines the user-facing filter expression (search, parent FK filters, etc.).
    /// </summary>
    public abstract Expression<Func<T, bool>> ToExpression();

    /// <summary>
    /// Applies deterministic sorting to the query. Must include a unique tiebreaker
    /// (e.g., .OrderBy(x => x.Name).ThenBy(x => x.Id)) to ensure stable cursor pagination.
    /// </summary>
    public abstract IOrderedQueryable<T> ApplySort(IQueryable<T> query);

    /// <summary>
    /// Returns a filter expression that excludes all rows at or before the cursor position.
    /// Used for keyset pagination — the expression must reproduce the same ordering logic as
    /// <see cref="ApplySort"/> so that rows are skipped in the correct order.
    /// Use <see cref="DecodeCursor"/> to extract the sort value and tiebreaker ID from the cursor.
    /// </summary>
    public abstract Expression<Func<T, bool>> GetCursorFilter(string cursor);

    /// <summary>
    /// Builds an opaque cursor string from the last entity in a page.
    /// Must encode the same sort key(s) used by ApplySort + the tiebreaker.
    /// </summary>
    public abstract string BuildCursor(T entity);

    /// <summary>
    /// The permission key required to access this data. When set, the executor
    /// uses this to apply authorization filtering via the TVF.
    /// </summary>
    public virtual string? RequiredPermission => null;

    /// <summary>
    /// Hook to configure the query before filtering/sorting (e.g., .Include() calls).
    /// </summary>
    public virtual IQueryable<T> ConfigureQuery(IQueryable<T> query) => query;

    /// <summary>
    /// Encodes a sort value and tiebreaker ID into a Base64 cursor string.
    /// </summary>
    protected static string EncodeCursor(string sortValue, string id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sortValue}\n{id}"));

    /// <summary>
    /// Decodes a Base64 cursor string into its sort value and tiebreaker ID.
    /// </summary>
    protected static (string SortValue, string Id) DecodeCursor(string cursor)
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        var parts = decoded.Split('\n', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }
}
