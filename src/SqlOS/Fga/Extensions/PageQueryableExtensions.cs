using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Paging;
using SqlOS.Pagination;

namespace SqlOS.Fga.Extensions;

/// <summary>
/// Reads an authorized list: the page of an application's query that a subject may see.
/// </summary>
public static class PageQueryableExtensions
{
    /// <summary>The largest page <see cref="ToPageAsync{T}"/> accepts.</summary>
    public const int MaxPageSize = 1000;

    /// <summary>
    /// Returns the page of <paramref name="query"/> that <paramref name="subjectId"/> may see with
    /// <paramref name="permissionKey"/>, after <paramref name="cursor"/>, in the query's order.
    /// </summary>
    /// <typeparam name="T">The entity type exposing the FGA resource identifier.</typeparam>
    /// <param name="query">
    /// A query that starts from a DbSet of a context deriving from <c>SqlOSDbContext&lt;TContext&gt;</c>,
    /// with optional <c>Where</c>, <c>Include</c>/<c>ThenInclude</c>, tracking, <c>TagWith</c>,
    /// <c>IgnoreQueryFilters</c> and <c>AsSplitQuery</c>, ending with one
    /// <c>OrderBy</c>/<c>OrderByDescending</c> and any number of <c>ThenBy</c>/<c>ThenByDescending</c>
    /// in the same direction over mapped scalar properties. The primary key is appended to the order.
    /// </param>
    /// <param name="subjectId">The caller. Its active group memberships are included.</param>
    /// <param name="permissionKey">The permission the caller needs on each row's resource.</param>
    /// <param name="pageSize">The page size, 1 to 1,000.</param>
    /// <param name="cursor">
    /// The <c>NextCursor</c> of the previous page, or <see langword="null"/> for the first page. Pass
    /// it back as it came; do not filter on the order's columns to continue a list yourself.
    /// </param>
    /// <param name="cancellationToken">A token that cancels the page.</param>
    /// <returns>
    /// The page. <c>NextCursor</c> is <see langword="null"/> when the page is the last; a full page
    /// may also be followed by an empty one. An unknown subject or permission gets an empty page.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The page reads the application's query in its order and checks each row with
    /// <c>fn_IsResourceAccessible</c>, in growing batches. In the same rounds it reads the caller's own
    /// rows from the node index SqlOS keeps for the entity (which rows sit under which of the caller's
    /// granted nodes) and, once they fit the batch, sorts them through the query. Whichever finishes
    /// first is the page, so any order costs at most a small multiple of the smaller of "rows the
    /// query must read to find a page" and "rows the caller can see". An order declared with
    /// <c>HasPageOrder</c> reads that order under each of the caller's nodes instead, about a page for
    /// any caller. The rows of the page are then loaded with the query's includes and tracking and
    /// checked once more.
    /// </para>
    /// <para>
    /// The caller's subjects and the permission are resolved each time the page runs, so grant,
    /// membership and lifecycle changes apply from the next page.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is outside 1 to 1,000.</exception>
    /// <exception cref="ArgumentException">
    /// The query does not start from a DbSet of a context deriving from
    /// <c>SqlOSDbContext&lt;TContext&gt;</c>; it has no ordering, mixed directions, an ordering over
    /// anything but a mapped scalar property, or an operator such as <c>Select</c>, <c>Skip</c> or
    /// <c>Take</c>; or the cursor belongs to another list.
    /// </exception>
    /// <exception cref="SqlOSCursorException">The cursor is not one this list issued.</exception>
    public static async Task<SqlOSCursorPage<T>> ToPageAsync<T>(
        this IQueryable<T> query,
        string subjectId,
        string permissionKey,
        int pageSize,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        where T : class, IHasResourceId
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(subjectId);
        ArgumentNullException.ThrowIfNull(permissionKey);
        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, $"The page size must be between 1 and {MaxPageSize}.");
        }

        var context = SqlOSFgaQueryContexts.Find(query.Provider)
            ?? throw new ArgumentException(
                "ToPageAsync needs a query that starts from a DbSet of a context deriving from SqlOSDbContext<TContext>, which keeps page orders current.",
                nameof(query));

        var ordering = SqlOSFgaOrderReader.Read(query.Expression);
        var position = string.IsNullOrWhiteSpace(cursor)
            ? null
            : SqlOSFgaPageCursor.Decode(cursor, ordering.EntityType, ordering.Columns, ordering.Descending);

        var caller = await SqlOSFgaPrincipals.ResolveCallerAsync(
            context,
            subjectId,
            permissionKey,
            Logger(context),
            cancellationToken);
        if (caller == null)
        {
            // An unknown subject or permission: nothing is visible, and no page statement runs.
            return SqlOSCursorPage<T>.Create([], pageSize, null);
        }

        var page = new SqlOSFgaPage<T>(context, query.Provider, ordering, caller, position, pageSize);
        return await page.RunAsync(cancellationToken);
    }

    private static ILogger Logger(DbContext context)
        => context.GetService<ILoggerFactory>().CreateLogger("SqlOS.Fga");

    /// <summary>One page: the reads, the load, the cursor and the log line.</summary>
    private sealed class SqlOSFgaPage<T>(
        DbContext context,
        IQueryProvider provider,
        SqlOSFgaPageOrdering ordering,
        SqlOSFgaCaller caller,
        object[]? position,
        int pageSize)
        where T : class, IHasResourceId
    {
        private readonly IReadOnlyList<IProperty> _columns = ordering.Columns;
        private readonly IProperty _key = ordering.Columns[^1];
        private readonly Expression<Func<T, bool>> _visible = SqlOSFgaVisibility.Predicate<T>(context, caller.SubjectIds, caller.PermissionId);
        private Func<T, object>? _keyOf;

        public async Task<SqlOSCursorPage<T>> RunAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var model = context.Model;
            var declared = SqlOSFgaPageOrders.For(ordering.EntityType)
                .FirstOrDefault(order => order.Matches(_columns));
            var usable = declared != null && !SqlOSFgaPageOrders.IsTableMissing(model, declared);

            // Every other order reads the caller's rows through the entity's node index.
            var nodeIndex = usable ? null : SqlOSFgaPageOrders.NodeIndex(ordering.EntityType);
            var nodeIndexUsable = nodeIndex != null && !SqlOSFgaPageOrders.IsTableMissing(model, nodeIndex);

            var selection = await SqlOSFgaPageQuery.SelectAsync(
                pageSize,
                Read1Async,
                usable ? (budget, token) => Read2Async(declared!, budget, token) : null,
                nodeIndexUsable ? (budget, token) => Read3Async(nodeIndex!, budget, token) : null,
                cancellationToken);

            // A stale entry can list a row under two of the caller's nodes; the page has it once.
            var keys = selection.Rows.Select(row => row[^1]).Distinct().ToList();
            var loaded = keys.Count == 0 ? [] : await LoadAsync(keys, cancellationToken);
            var nextCursor = selection.HasMore && selection.Rows.Count > 0
                ? SqlOSFgaPageCursor.Encode(ordering.EntityType, _columns, ordering.Descending, selection.Rows[^1])
                : null;

            Log(selection, OrderState(declared, usable, nodeIndex, nodeIndexUsable), keys, loaded, stopwatch.Elapsed);
            return SqlOSCursorPage<T>.Create(loaded, pageSize, nextCursor);
        }

        /// <summary>How the order is served, for the log line: "declared", or why it is not and whether its node index is there.</summary>
        private static string OrderState(SqlOSFgaPageOrder? declared, bool usable, SqlOSFgaPageOrder? nodeIndex, bool nodeIndexUsable)
        {
            if (usable)
            {
                return "declared";
            }

            var order = declared == null ? "not declared" : "declared, table missing";
            return nodeIndex == null
                ? $"{order}, no node index"
                : nodeIndexUsable ? order : $"{order}, node index missing";
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

            var visible = new ParameterReplacer(_visible.Parameters[0], entity).Visit(_visible.Body);
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
            var nodes = ((ISqlOSFgaDbContext)context).PageNodes(caller.SubjectIds, caller.PermissionId);
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
        /// Read 3: the caller's row keys from the node index (3a), at most <c>b + 1</c>. More than
        /// <c>b</c> is not this round. Otherwise those rows through the application's query (3b).
        /// </summary>
        private async Task<SqlOSFgaPageQuery.Read3Result> Read3Async(
            SqlOSFgaPageOrder nodeIndex,
            int budget,
            CancellationToken cancellationToken)
        {
            var keys = await CallerKeysAsync(nodeIndex, budget == int.MaxValue ? budget : budget + 1, cancellationToken);
            if (keys.Count > budget)
            {
                return new SqlOSFgaPageQuery.Read3Result(keys.Count, null);
            }

            // Minimal nodes are disjoint; a stale entry can still list a row under two of them.
            var distinct = keys.Distinct().ToList();
            var rows = distinct.Count == 0 ? [] : await CallerRowsAsync(distinct, cancellationToken);
            return new SqlOSFgaPageQuery.Read3Result(keys.Count, rows);
        }

        /// <summary>
        /// Read 3a: under each of the caller's nodes (<c>fn_PageNodes</c>), the keys of the rows the
        /// node index lists, at most <paramref name="cap"/> of them in all.
        /// </summary>
        /// <remarks>
        /// The nodes are sorted by the length of their id first, an order no index provides, so the
        /// engine produces all of them before the cap applies. Without that sort, SQL Server lets the
        /// cap's row goal into <c>fn_PageNodes</c>, whose minimality check then rescans the caller's
        /// grants for every ancestor of every node it reads: in the page-order benchmark a caller with
        /// 10,000 single-row grants took seconds per round, growing with the cap.
        /// </remarks>
        private async Task<IReadOnlyList<object>> CallerKeysAsync(
            SqlOSFgaPageOrder nodeIndex,
            int cap,
            CancellationToken cancellationToken)
        {
            var nodes = (Expression)((ISqlOSFgaDbContext)context).PageNodes(caller.SubjectIds, caller.PermissionId).Expression;
            nodes = Order(
                nodes,
                typeof(SqlOSFgaPageNode),
                parameter => [Expression.Property(Expression.Property(parameter, nameof(SqlOSFgaPageNode.NodeId)), nameof(string.Length))],
                descending: false);
            nodes = Take(nodes, typeof(SqlOSFgaPageNode), int.MaxValue);

            IQueryable<Dictionary<string, object>> entries = context.Set<Dictionary<string, object>>(nodeIndex.EntryType.Name);
            var node = Expression.Parameter(typeof(SqlOSFgaPageNode), "n");
            var nodeId = Expression.Property(node, nameof(SqlOSFgaPageNode.NodeId));
            var entryType = typeof(Dictionary<string, object>);

            // entries.Where(x => x.NodeId == n.NodeId).OrderBy(x => x.RowKey).Take(cap): a range of the node
            // index's primary key, in its own order, so the plan that reads the range and stops at the
            // cap is the one the engines choose. Without the order, PostgreSQL can scan the whole node
            // index for the first matches of a node it expects to be large.
            var entry = Expression.Parameter(entryType, "x");
            var perNode = Where(
                entries.Expression,
                entryType,
                Expression.Lambda(Expression.Equal(EntryProperty(entry, SqlOSFgaPageOrders.NodeIdProperty, typeof(string)), nodeId), entry));
            perNode = Order(perNode, entryType, parameter => [EntryProperty(parameter, SqlOSFgaPageOrders.RowKeyProperty, _key.ClrType)], descending: false);
            perNode = Take(perNode, entryType, cap);

            // As in read 2, projecting the node keeps the subquery correlated, so each node is a seek
            // that stops at the cap (CROSS APPLY / JOIN LATERAL).
            var row = Expression.MemberInit(
                Expression.New(typeof(SqlOSFgaPageEntryRow)),
                Expression.Bind(typeof(SqlOSFgaPageEntryRow).GetProperty(nameof(SqlOSFgaPageEntryRow.NodeId))!, nodeId),
                Expression.Bind(typeof(SqlOSFgaPageEntryRow).GetProperty(nameof(SqlOSFgaPageEntryRow.Entry))!, entry));
            perNode = Select(perNode, entryType, typeof(SqlOSFgaPageEntryRow), Expression.Lambda(row, entry));

            var merged = (Expression)Expression.Call(
                SelectManyMethod.MakeGenericMethod(typeof(SqlOSFgaPageNode), typeof(SqlOSFgaPageEntryRow)),
                nodes,
                Expression.Quote(Expression.Lambda(
                    typeof(Func<,>).MakeGenericType(typeof(SqlOSFgaPageNode), typeof(IEnumerable<SqlOSFgaPageEntryRow>)),
                    perNode,
                    node)));

            // Any cap keys will do, so the merge is not sorted: ordering by a constant (ORDER BY (SELECT 1),
            // which neither engine sorts by) tells EF Core the limit is meant, without its warning
            // about a limit with no order.
            merged = Order(merged, typeof(SqlOSFgaPageEntryRow), _ => [Expression.Constant(1)], descending: false);
            merged = Take(merged, typeof(SqlOSFgaPageEntryRow), cap);

            var result = Expression.Parameter(typeof(SqlOSFgaPageEntryRow), "r");
            var projection = Expression.Lambda(
                Expression.NewArrayInit(
                    typeof(object),
                    Expression.Convert(EntryProperty(EntryOf(result), SqlOSFgaPageOrders.RowKeyProperty, _key.ClrType), typeof(object))),
                result);
            merged = Select(merged, typeof(SqlOSFgaPageEntryRow), typeof(object[]), projection);

            var rows = await provider.CreateQuery<object[]>(merged).ToListAsync(cancellationToken);
            return rows.Select(values => values[0]).ToList();
        }

        /// <summary>
        /// Read 3b: <c>inner.Where(e =&gt; EF.Parameter(keys).Contains(e.Key)).OrderBy(e =&gt; 1).Skip(0)</c>, then
        /// <c>.Where(keyset).OrderBy(order).Take(P + 1)</c>, projected to the order values. The keys go as one
        /// parameter (<c>OPENJSON</c> on SQL Server, an array on PostgreSQL) whatever their number, past
        /// the 2,100 parameters SQL Server allows.
        /// </summary>
        /// <remarks>
        /// The <c>Skip(0)</c> keeps the caller's rows in a subquery of their own (ordered by a constant
        /// so that EF Core sees the offset is meant), which is looked up by key and then sorted. Without
        /// it, PostgreSQL can walk the order's index and test each row against the keys, expecting to
        /// find a page early; when the caller's rows sit at the far end of the order, that reads every
        /// row before them, and EXPLAIN does not show it.
        /// </remarks>
        private async Task<IReadOnlyList<IReadOnlyList<object>>> CallerRowsAsync(
            IReadOnlyList<object> keys,
            CancellationToken cancellationToken)
        {
            var keyList = KeyList(keys);
            var entity = Expression.Parameter(typeof(T), "e");
            var contains = Expression.Call(
                ContainsMethod.MakeGenericMethod(_key.ClrType),
                Expression.Call(
                    ParameterMethod.MakeGenericMethod(keyList.GetType()),
                    SqlOSFgaPageCursor.QueryParameter(keyList, keyList.GetType())),
                Member(entity, _key));
            var source = Where(ordering.Inner, typeof(T), Expression.Lambda(contains, entity));
            source = Order(source, typeof(T), _ => [Expression.Constant(1)], descending: false);
            source = Expression.Call(SkipMethod.MakeGenericMethod(typeof(T)), source, SqlOSFgaPageCursor.QueryParameter(0, typeof(int)));
            if (position != null)
            {
                source = Where(source, typeof(T), Expression.Lambda(SqlOSFgaPageCursor.Keyset(EntityColumns(entity).ToList(), position, ordering.Descending), entity));
            }

            source = Order(source, typeof(T), parameter => EntityColumns(parameter), ordering.Descending);
            source = Take(source, typeof(T), pageSize + 1);
            var projection = Expression.Lambda(
                Expression.NewArrayInit(
                    typeof(object),
                    EntityColumns(entity).Select(value => Expression.Convert(value, typeof(object)))),
                entity);
            source = Select(source, typeof(T), typeof(object[]), projection);

            return await provider.CreateQuery<object[]>(source).ToListAsync(cancellationToken);
        }

        /// <summary>
        /// Load: <c>inner.Where(e =&gt; keys.Contains(e.Key)).Where(filter)</c>, ordered by the keys'
        /// position. The filter is the final check: a stale entry can only drop a row, never add one.
        /// </summary>
        private async Task<List<T>> LoadAsync(IReadOnlyList<object> keys, CancellationToken cancellationToken)
        {
            var keyList = KeyList(keys);
            var entity = Expression.Parameter(typeof(T), "e");
            var contains = Expression.Call(
                ContainsMethod.MakeGenericMethod(_key.ClrType),
                SqlOSFgaPageCursor.QueryParameter(keyList, keyList.GetType()),
                Member(entity, _key));
            var source = Where(ordering.Inner, typeof(T), Expression.Lambda(contains, entity));
            source = Where(source, typeof(T), _visible);

            var rows = await provider.CreateQuery<T>(source).ToListAsync(cancellationToken);
            var positions = keys.Select((key, index) => (key, index)).ToDictionary(item => item.key, item => item.index);
            return rows
                .Where(row => positions.ContainsKey(KeyOf(row)))
                .OrderBy(row => positions[KeyOf(row)])
                .ToList();
        }

        /// <summary>
        /// One Debug line per page: the order and how it is served, the rows each read touched per
        /// round, which read produced the page, the rows the final check dropped, and the time.
        /// </summary>
        private void Log(
            SqlOSFgaPageQuery.Selection selection,
            string orderState,
            IReadOnlyList<object> keys,
            IReadOnlyList<T> loaded,
            TimeSpan elapsed)
        {
            var logger = Logger(context);
            if (!logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            var loadedKeys = new HashSet<object>(loaded.Select(KeyOf));
            var droppedKeys = keys.Where(key => !loadedKeys.Contains(key)).ToList();
            logger.LogDebug(
                "ToPageAsync {Entity} by {Order} (page order {PageOrder}): {Rounds} round(s); read 1 rows per round [{Read1}]; read 2 entries per node per round [{Read2}]; read 3 keys per round [{Read3}]; page from {Source}; {Dropped} row(s) dropped by the final check{DroppedKeys}; {ElapsedMs} ms.",
                ordering.EntityType.ClrType.Name,
                $"{string.Join(", ", _columns.Select(column => column.Name))} {(ordering.Descending ? "desc" : "asc")}",
                orderState,
                selection.Rounds,
                string.Join(", ", selection.Read1RowsPerRound),
                string.Join("; ", selection.Read2EntriesPerRound.Select(round => string.Join(", ", round.Select(node => $"{node.Key}: {node.Value}")))),
                string.Join(", ", selection.Read3KeysPerRound),
                selection.Source,
                droppedKeys.Count,
                droppedKeys.Count == 0 ? string.Empty : $" ({string.Join(", ", droppedKeys)})",
                (long)elapsed.TotalMilliseconds);
        }

        private IList KeyList(IReadOnlyList<object> keys)
        {
            var keyList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_key.ClrType), keys.Count)!;
            foreach (var key in keys)
            {
                keyList.Add(key);
            }

            return keyList;
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
    private static readonly MethodInfo SkipMethod = typeof(Queryable).GetMethods()
        .Single(method => method.Name == nameof(Queryable.Skip)
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
    private static readonly MethodInfo ParameterMethod = typeof(EF).GetMethods()
        .Single(method => method.Name == nameof(EF.Parameter) && method.IsGenericMethodDefinition && method.GetParameters().Length == 1);

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
