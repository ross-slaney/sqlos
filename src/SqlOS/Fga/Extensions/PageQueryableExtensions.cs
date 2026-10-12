using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Paging;
using SqlOS.Pagination;

namespace SqlOS.Fga.Extensions;

/// <summary>
/// Runs an authorized page: the application's query, a <c>BuildFilterAsync</c> filter, a page size
/// and a cursor.
/// </summary>
public static class PageQueryableExtensions
{
    /// <summary>The largest page <see cref="ToPageAsync{T}"/> accepts.</summary>
    public const int MaxPageSize = 1000;

    // Shapes already warned about, per model: EF Core's logger factory is scoped to a context,
    // the model lives as long as the application's context type.
    private static readonly ConditionalWeakTable<IModel, ConcurrentDictionary<string, bool>> UndeclaredWarnings = new();

    /// <summary>
    /// Returns the page of <paramref name="query"/> the subject behind <paramref name="filter"/> may
    /// see, after <paramref name="cursor"/>, in the query's order.
    /// </summary>
    /// <typeparam name="T">The entity type exposing the FGA resource identifier.</typeparam>
    /// <param name="query">
    /// A DbSet query with optional <c>Where</c>, <c>Include</c>/<c>ThenInclude</c>, tracking,
    /// <c>TagWith</c>, <c>IgnoreQueryFilters</c> and <c>AsSplitQuery</c>, ending with one
    /// <c>OrderBy</c>/<c>OrderByDescending</c> and any number of <c>ThenBy</c>/<c>ThenByDescending</c>
    /// in the same direction over mapped scalar properties. The primary key is appended to the order.
    /// </param>
    /// <param name="filter">The filter <c>ISqlOSFgaAuthService.BuildFilterAsync</c> returned.</param>
    /// <param name="pageSize">The page size, 1 to 1,000.</param>
    /// <param name="cursor">The <c>NextCursor</c> of the previous page, or <see langword="null"/> for the first page.</param>
    /// <param name="cancellationToken">A token that cancels the page.</param>
    /// <returns>
    /// The page. <c>NextCursor</c> is <see langword="null"/> when the page is the last; a full page
    /// may also be followed by an empty one.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The page reads the application's query in its order and checks each row, the way
    /// <c>.Where(filter)</c> does, for as many rows as a page needs. When few of those rows are
    /// visible and the order is declared with <c>HasPageOrder</c>, it reads the order under each
    /// of the caller's granted nodes instead, so a caller who can see few rows of a large table
    /// costs about a page. The rows of the page are then loaded with the query's includes and
    /// tracking and checked once more with <paramref name="filter"/>.
    /// </para>
    /// <para>
    /// The context must derive from <c>SqlOSDbContext&lt;TContext&gt;</c>, which keeps page orders
    /// current, and must be the instance the filter was built on.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is outside 1 to 1,000.</exception>
    /// <exception cref="ArgumentException">
    /// The filter does not come from <c>BuildFilterAsync</c>; the query has no ordering, mixed
    /// directions, an ordering over anything but a mapped scalar property, or an operator such as
    /// <c>Select</c>, <c>Skip</c> or <c>Take</c>; the context does not derive from
    /// <c>SqlOSDbContext&lt;TContext&gt;</c> or is not the filter's; or the cursor belongs to another list.
    /// </exception>
    /// <exception cref="SqlOSCursorException">The cursor is not one this list issued.</exception>
    public static async Task<SqlOSCursorPage<T>> ToPageAsync<T>(
        this IQueryable<T> query,
        Expression<Func<T, bool>> filter,
        int pageSize,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        where T : class, IHasResourceId
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, $"The page size must be between 1 and {MaxPageSize}.");
        }

        var authorization = SqlOSFgaFilterReader.Read(filter);
        var ordering = SqlOSFgaOrderReader.Read(query.Expression);
        var position = string.IsNullOrWhiteSpace(cursor)
            ? null
            : SqlOSFgaPageCursor.Decode(cursor, ordering.EntityType, ordering.Columns, ordering.Descending);

        if (authorization.IsEmpty)
        {
            // An unknown subject or permission: nothing is visible, and nothing is read.
            return SqlOSCursorPage<T>.Create([], pageSize, null);
        }

        if (authorization.Context is not DbContext context || !DerivesFromSqlOSDbContext(context.GetType()))
        {
            throw new ArgumentException(
                "ToPageAsync needs a context that derives from SqlOSDbContext<TContext>, which keeps page orders current.",
                nameof(query));
        }

        if (!ReferenceEquals(query.Provider, context.GetService<IAsyncQueryProvider>()))
        {
            throw new ArgumentException(
                "Build the filter and run the page on the same DbContext instance.",
                nameof(query));
        }

        var page = new SqlOSFgaPage<T>(context, query.Provider, ordering, authorization, position, pageSize);
        return await page.RunAsync(cancellationToken);
    }

    private static bool DerivesFromSqlOSDbContext(Type? type)
    {
        for (; type != null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SqlOSDbContext<>))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One page: the two reads, the load, the cursor and the log line.</summary>
    private sealed class SqlOSFgaPage<T>(
        DbContext context,
        IQueryProvider provider,
        SqlOSFgaPageOrdering ordering,
        SqlOSFgaFilter authorization,
        object[]? position,
        int pageSize)
        where T : class, IHasResourceId
    {
        private readonly IReadOnlyList<IProperty> _columns = ordering.Columns;
        private readonly IProperty _key = ordering.Columns[^1];
        private Func<T, object>? _keyOf;

        public async Task<SqlOSCursorPage<T>> RunAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var declared = SqlOSFgaPageOrders.For(ordering.EntityType)
                .FirstOrDefault(order => order.Matches(_columns));
            var usable = declared != null && !SqlOSFgaPageOrders.IsTableMissing(context.Model, declared);

            var selection = await SqlOSFgaPageQuery.SelectAsync(
                pageSize,
                Read1Async,
                usable ? (budget, token) => Read2Async(declared!, budget, token) : null,
                cancellationToken);

            var keys = selection.Rows.Select(row => row[^1]).ToList();
            var loaded = keys.Count == 0 ? [] : await LoadAsync(keys, cancellationToken);
            var dropped = keys.Count - loaded.Count;
            var nextCursor = selection.HasMore && selection.Rows.Count > 0
                ? SqlOSFgaPageCursor.Encode(ordering.EntityType, _columns, ordering.Descending, selection.Rows[^1])
                : null;

            Log(selection, declared, usable, keys, loaded, stopwatch.Elapsed);
            return SqlOSCursorPage<T>.Create(loaded, pageSize, nextCursor);
        }

        /// <summary>
        /// Read 1: <c>inner.Where(keyset).OrderBy(order).Take(b).Select(e =&gt; new object[] { order values..., visible })</c>.
        /// </summary>
        private async Task<IReadOnlyList<SqlOSFgaPageQuery.Read1Row>> Read1Async(int budget, CancellationToken cancellationToken)
        {
            var entity = Expression.Parameter(typeof(T), "e");
            var source = ordering.Inner;
            if (position != null)
            {
                source = Where(source, typeof(T), Expression.Lambda(SqlOSFgaPageCursor.Keyset(EntityColumns(entity).ToList(), position, ordering.Descending), entity));
            }

            source = Order(source, typeof(T), parameter => EntityColumns(parameter), ordering.Descending);
            source = Take(source, typeof(T), budget);

            var visible = new ParameterReplacer(authorization.Lambda.Parameters[0], entity).Visit(authorization.Lambda.Body);
            var projection = Expression.Lambda(
                Expression.NewArrayInit(
                    typeof(object),
                    EntityColumns(entity).Append(visible).Select(value => Expression.Convert(value, typeof(object)))),
                entity);
            source = Select(source, typeof(T), typeof(object[]), projection);

            var rows = await provider.CreateQuery<object[]>(source).ToListAsync(cancellationToken);
            return rows
                .Select(row => new SqlOSFgaPageQuery.Read1Row(row[..^1], (bool)row[^1]))
                .ToList();
        }

        /// <summary>
        /// Read 2: under each of the caller's nodes (<c>fn_PageNodes</c>), the next <c>b</c> entries
        /// that pass the application's filters, merged in order, at most a page of them.
        /// </summary>
        private async Task<IReadOnlyList<SqlOSFgaPageQuery.Read2Row>> Read2Async(
            SqlOSFgaPageOrder order,
            int budget,
            CancellationToken cancellationToken)
        {
            var nodes = ((ISqlOSFgaDbContext)context).PageNodes(authorization.SubjectIds, authorization.PermissionId);
            IQueryable<Dictionary<string, object>> entries = context.Set<Dictionary<string, object>>(order.EntryType.Name);
            var node = Expression.Parameter(typeof(SqlOSFgaPageNode), "n");
            var nodeId = Expression.Property(node, nameof(SqlOSFgaPageNode.NodeId));
            var entryType = typeof(Dictionary<string, object>);

            // entries.Where(x => x.NodeId == n.NodeId)
            var entry = Expression.Parameter(entryType, "x");
            var perNode = Where(
                entries.Expression,
                entryType,
                Expression.Lambda(Expression.Equal(EntryProperty(entry, SqlOSFgaPageOrders.NodeIdProperty, typeof(string)), nodeId), entry));

            if (position != null)
            {
                perNode = Where(perNode, entryType, Expression.Lambda(SqlOSFgaPageCursor.Keyset(EntryColumns(entry).ToList(), position, ordering.Descending), entry));
            }

            // The application's filters, before the per-node limit: x => inner.Any(e => e.Key == x.RowKey)
            var entity = Expression.Parameter(typeof(T), "e");
            var sameRow = Expression.Lambda(
                Expression.Equal(Member(entity, _key), EntryProperty(entry, SqlOSFgaPageOrders.RowKeyProperty, _key.ClrType)),
                entity);
            var exists = Expression.Call(
                AnyWithPredicate.MakeGenericMethod(typeof(T)),
                ordering.Inner,
                Expression.Quote(sameRow));
            perNode = Where(perNode, entryType, Expression.Lambda(exists, entry));
            perNode = Order(perNode, entryType, parameter => EntryColumns(parameter), ordering.Descending);
            perNode = Take(perNode, entryType, budget);

            // Projecting the node keeps the subquery correlated, so it runs once per node
            // (CROSS APPLY / JOIN LATERAL) instead of being rewritten into a join over every entry.
            var row = Expression.MemberInit(
                Expression.New(typeof(SqlOSFgaPageEntryRow)),
                Expression.Bind(typeof(SqlOSFgaPageEntryRow).GetProperty(nameof(SqlOSFgaPageEntryRow.NodeId))!, nodeId),
                Expression.Bind(typeof(SqlOSFgaPageEntryRow).GetProperty(nameof(SqlOSFgaPageEntryRow.Entry))!, entry));
            perNode = Select(perNode, entryType, typeof(SqlOSFgaPageEntryRow), Expression.Lambda(row, entry));

            var merged = (Expression)Expression.Call(
                SelectManyMethod.MakeGenericMethod(typeof(SqlOSFgaPageNode), typeof(SqlOSFgaPageEntryRow)),
                nodes.Expression,
                Expression.Quote(Expression.Lambda(
                    typeof(Func<,>).MakeGenericType(typeof(SqlOSFgaPageNode), typeof(IEnumerable<SqlOSFgaPageEntryRow>)),
                    perNode,
                    node)));
            merged = Order(merged, typeof(SqlOSFgaPageEntryRow), parameter => EntryColumns(EntryOf(parameter)), ordering.Descending);
            merged = Take(merged, typeof(SqlOSFgaPageEntryRow), pageSize);

            var result = Expression.Parameter(typeof(SqlOSFgaPageEntryRow), "r");
            var projection = Expression.Lambda(
                Expression.NewArrayInit(
                    typeof(object),
                    EntryColumns(EntryOf(result))
                        .Append(Expression.Property(result, nameof(SqlOSFgaPageEntryRow.NodeId)))
                        .Select(value => Expression.Convert(value, typeof(object)))),
                result);
            merged = Select(merged, typeof(SqlOSFgaPageEntryRow), typeof(object[]), projection);

            var rows = await provider.CreateQuery<object[]>(merged).ToListAsync(cancellationToken);
            return rows
                .Select(values => new SqlOSFgaPageQuery.Read2Row(values[..^1], (string)values[^1]))
                .ToList();
        }

        /// <summary>
        /// Load: <c>inner.Where(e =&gt; keys.Contains(e.Key)).Where(filter)</c>, ordered by the keys'
        /// position. The filter is the final check: a stale entry can only drop a row, never add one.
        /// </summary>
        private async Task<List<T>> LoadAsync(IReadOnlyList<object> keys, CancellationToken cancellationToken)
        {
            var keyList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_key.ClrType), keys.Count)!;
            foreach (var key in keys)
            {
                keyList.Add(key);
            }

            var entity = Expression.Parameter(typeof(T), "e");
            var contains = Expression.Call(
                ContainsMethod.MakeGenericMethod(_key.ClrType),
                SqlOSFgaPageCursor.QueryParameter(keyList, keyList.GetType()),
                Member(entity, _key));
            var source = Where(ordering.Inner, typeof(T), Expression.Lambda(contains, entity));
            source = Where(source, typeof(T), authorization.Lambda);

            var rows = await provider.CreateQuery<T>(source).ToListAsync(cancellationToken);
            var positions = keys.Select((key, index) => (key, index)).ToDictionary(item => item.key, item => item.index);
            return rows
                .Where(row => positions.ContainsKey(KeyOf(row)))
                .OrderBy(row => positions[KeyOf(row)])
                .ToList();
        }

        private void Log(
            SqlOSFgaPageQuery.Selection selection,
            SqlOSFgaPageOrder? declared,
            bool usable,
            IReadOnlyList<object> keys,
            IReadOnlyList<T> loaded,
            TimeSpan elapsed)
        {
            var loggerFactory = context.GetService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("SqlOS.Fga");
            var orderText = $"{string.Join(", ", _columns.Select(column => column.Name))} {(ordering.Descending ? "desc" : "asc")}";

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var loadedKeys = new HashSet<object>(loaded.Select(KeyOf));
                var droppedKeys = keys.Where(key => !loadedKeys.Contains(key)).ToList();
                logger.LogDebug(
                    "ToPageAsync {Entity} by {Order} (page order {Declared}): {Rounds} round(s); read 1 rows per round [{Read1}]; read 2 entries per node per round [{Read2}]; page from {Source}; {Dropped} row(s) dropped by the final check{DroppedKeys}; {ElapsedMs} ms.",
                    ordering.EntityType.ClrType.Name,
                    orderText,
                    declared == null ? "not declared" : usable ? "declared" : "declared, table missing",
                    selection.Rounds,
                    string.Join(", ", selection.Read1RowsPerRound),
                    string.Join("; ", selection.Read2EntriesPerRound.Select(round => string.Join(", ", round.Select(node => $"{node.Key}: {node.Value}")))),
                    selection.Source,
                    droppedKeys.Count,
                    droppedKeys.Count == 0 ? string.Empty : $" ({string.Join(", ", droppedKeys)})",
                    (long)elapsed.TotalMilliseconds);
            }

            if (declared == null && selection.Rounds > 1)
            {
                var warned = UndeclaredWarnings.GetValue(context.Model, static _ => new ConcurrentDictionary<string, bool>(StringComparer.Ordinal));
                if (warned.TryAdd($"{ordering.EntityType.Name}|{orderText}", true))
                {
                    var entityName = ordering.EntityType.ClrType.Name;
                    var sortColumns = _columns.Take(_columns.Count - 1).Select(column => $"e.{column.Name}").ToList();
                    var declaration = sortColumns.Count switch
                    {
                        0 => $"e => e.{_key.Name}",
                        1 => $"e => {sortColumns[0]}",
                        _ => $"e => new {{ {string.Join(", ", sortColumns)} }}",
                    };
                    logger.LogWarning(
                        "ToPageAsync scanned {Rows} rows of {Entity} ordered by {Order} to fill a page of {PageSize}. No page order is declared for it, so pages for callers who can see few rows read the table. Declare modelBuilder.Entity<{Entity}>().HasPageOrder({Declaration}) and add a migration.",
                        selection.Read1RowsPerRound.Sum(),
                        entityName,
                        orderText,
                        pageSize,
                        entityName,
                        declaration);
                }
            }
        }

        private object KeyOf(T row)
        {
            if (_keyOf == null)
            {
                var entity = Expression.Parameter(typeof(T), "e");
                _keyOf = Expression.Lambda<Func<T, object>>(Expression.Convert(Member(entity, _key), typeof(object)), entity).Compile();
            }

            return _keyOf(row);
        }

        private IEnumerable<Expression> EntityColumns(ParameterExpression entity)
            => _columns.Select(column => Member(entity, column));

        private IEnumerable<Expression> EntryColumns(Expression entry)
            => _columns.Select((column, index) => (Expression)EntryProperty(
                entry,
                index == _columns.Count - 1 ? SqlOSFgaPageOrders.RowKeyProperty : column.Name,
                column.ClrType));

        private static Expression EntryOf(ParameterExpression row)
            => Expression.Property(row, nameof(SqlOSFgaPageEntryRow.Entry));
    }

    /// <summary>A per-node row of read 2: the node it was read under and the entry.</summary>
    internal sealed class SqlOSFgaPageEntryRow
    {
        public string NodeId { get; set; } = string.Empty;
        public Dictionary<string, object> Entry { get; set; } = null!;
    }

    private static readonly MethodInfo WhereMethod = QueryableMethod(nameof(Queryable.Where), 2, 2);
    private static readonly MethodInfo TakeMethod = typeof(Queryable).GetMethods()
        .Single(method => method.Name == nameof(Queryable.Take)
            && method.GetParameters() is [_, { ParameterType: var count }]
            && count == typeof(int));
    private static readonly MethodInfo SelectMethod = QueryableMethod(nameof(Queryable.Select), 2, 2);
    private static readonly MethodInfo SelectManyMethod = typeof(Queryable).GetMethods()
        .Single(method => method.Name == nameof(Queryable.SelectMany)
            && method.GetParameters().Length == 2
            && method.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);
    private static readonly MethodInfo AnyWithPredicate = QueryableMethod(nameof(Queryable.Any), 2, 2);
    private static readonly MethodInfo ContainsMethod = typeof(Enumerable).GetMethods()
        .Single(method => method.Name == nameof(Enumerable.Contains) && method.GetParameters().Length == 2);

    private static MethodInfo QueryableMethod(string name, int parameters, int funcArity)
        => typeof(Queryable).GetMethods().Single(method =>
            method.Name == name
            && method.GetParameters().Length == parameters
            && (parameters == 1
                || method.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == funcArity));

    private static Expression Where(Expression source, Type elementType, LambdaExpression predicate)
        => Expression.Call(WhereMethod.MakeGenericMethod(elementType), source, Expression.Quote(predicate));

    private static Expression Take(Expression source, Type elementType, int count)
        => Expression.Call(TakeMethod.MakeGenericMethod(elementType), source, SqlOSFgaPageCursor.QueryParameter(count, typeof(int)));

    private static Expression Select(Expression source, Type elementType, Type resultType, LambdaExpression selector)
        => Expression.Call(SelectMethod.MakeGenericMethod(elementType, resultType), source, Expression.Quote(selector));

    private static Expression Order(
        Expression source,
        Type elementType,
        Func<ParameterExpression, IEnumerable<Expression>> columns,
        bool descending)
    {
        var parameter = Expression.Parameter(elementType, "o");
        var first = true;
        foreach (var column in columns(parameter))
        {
            var name = (first, descending) switch
            {
                (true, false) => nameof(Queryable.OrderBy),
                (true, true) => nameof(Queryable.OrderByDescending),
                (false, false) => nameof(Queryable.ThenBy),
                _ => nameof(Queryable.ThenByDescending),
            };
            source = Expression.Call(
                typeof(Queryable),
                name,
                [elementType, column.Type],
                source,
                Expression.Quote(Expression.Lambda(column, parameter)));
            first = false;
        }

        return source;
    }

    private static Expression Member(Expression entity, IProperty property)
        => Expression.MakeMemberAccess(entity, (MemberInfo?)property.PropertyInfo ?? property.FieldInfo!);

    private static Expression EntryProperty(Expression entry, string name, Type type)
        => Expression.Call(
            typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(type),
            entry,
            Expression.Constant(name));

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }
}
