using SqlOS.Fga.Interfaces;
using SqlOS.Pagination;

namespace SqlOS.Fga.Extensions;

public static class SqlOSFgaPagingExtensions
{
    /// <summary>
    /// The next page of the rows the subject may see, in the query's order, after a cursor. The query declares
    /// the filters (<c>Where</c>), the order (<c>OrderBy</c>/<c>ThenBy</c>, one the entity has an index for;
    /// the key is appended when it is not the last sort column), and how rows load (<c>Include</c>,
    /// <c>AsNoTracking</c>…); SqlOS decides how to find them. Equivalent to <see cref="ISqlOSFgaAuthService.PageAsync{T}"/>.
    /// </summary>
    public static Task<SqlOSCursorPage<T>> ToAccessiblePageAsync<T>(
        this IQueryable<T> query,
        ISqlOSFgaAuthService authorization,
        string subjectId,
        string permissionKey,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
        where T : class, IHasResourceId
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return authorization.PageAsync(query, subjectId, permissionKey, cursor, pageSize, cancellationToken);
    }
}
