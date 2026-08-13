using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace SqlOS.Pagination;

/// <summary>
/// Shared admin cursor pagination: bounded page size, keyset filter, Take(limit + 1),
/// and opaque next-cursor encoding. Does not execute COUNT(*) or OFFSET.
/// </summary>
public static class SqlOSCursorPagination
{
    public const int DefaultPageSize = 25;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;

    public static int NormalizePageSize(int? pageSize, int defaultPageSize = DefaultPageSize)
    {
        var resolvedDefault = Math.Clamp(defaultPageSize, MinPageSize, MaxPageSize);
        return Math.Clamp(pageSize.GetValueOrDefault(resolvedDefault), MinPageSize, MaxPageSize);
    }

    /// <summary>
    /// First-window compatibility: <c>page</c> omitted or 1 is accepted.
    /// Any deeper offset is rejected so callers cannot trigger COUNT/OFFSET.
    /// </summary>
    public static void RejectLegacyOffset(int? page)
    {
        if (page is > 1)
        {
            throw new SqlOSCursorException(
                "Offset pagination is no longer supported. Omit page or pass page=1 for the first window, then follow nextCursor.");
        }
    }

    public static async Task<SqlOSCursorPage<T>> ToPageAsync<T>(
        IQueryable<T> query,
        SqlOSKeyset<T> keyset,
        string sortKey,
        string filterFingerprint,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
        where T : class
    {
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var keys = SqlOSCursorCodec.Decode(cursor, sortKey, filterFingerprint);
            query = query.Where(keyset.After(keys));
        }

        query = keyset.ApplySort(query);
        var rows = await query.Take(pageSize + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows = rows.Take(pageSize).ToList();
        }

        string? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            nextCursor = SqlOSCursorCodec.Encode(sortKey, filterFingerprint, keyset.Encode(rows[^1]));
        }

        return SqlOSCursorPage<T>.Create(rows, pageSize, nextCursor);
    }

    public static IResult BadRequest(SqlOSCursorException exception)
        => Results.Json(
            new { error = exception.Error, message = exception.Message },
            statusCode: StatusCodes.Status400BadRequest);

    public static async Task<IResult> Ok(Func<Task<object>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (SqlOSCursorException exception)
        {
            return BadRequest(exception);
        }
    }
}
