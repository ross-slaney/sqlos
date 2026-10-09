using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga;

/// <summary>
/// Warns, once per query shape, when a query filtered by <c>BuildFilterAsync</c> sorts by an order no
/// declared index covers. Such a page returns the right rows, but the database reads the caller's whole
/// scope and sorts it instead of answering with one index seek per access root; declaring the index makes
/// SqlOS mirror it per level and keeps the page's cost the page's size. <c>AddSqlOS</c> registers this on
/// the application's context; a context built by hand adds it with
/// <c>optionsBuilder.AddInterceptors(new SqlOSFgaUnindexedOrderInterceptor(loggerFactory))</c>.
/// </summary>
public sealed class SqlOSFgaUnindexedOrderInterceptor : IQueryExpressionInterceptor
{
    private static readonly MethodInfo EFPropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;
    private static readonly ConditionalWeakTable<IModel, IReadOnlyList<SqlOSFgaScopeTable>> TablesByModel = new();

    /// <summary>
    /// The instance <c>UseSqlOSFga</c> registers: it logs through the logger factory each context's options
    /// carry. EF Core 10 builds an internal service provider per distinct query expression interceptor
    /// instance, so one instance per host would build one per host and fail after twenty.
    /// </summary>
    internal static readonly SqlOSFgaUnindexedOrderInterceptor FromContextOptions = new(logger: null);

    private readonly ILogger? _logger;

    /// <summary>Creates the interceptor for a context built by hand, warning through <paramref name="loggerFactory"/>.</summary>
    /// <param name="loggerFactory">Where the warnings go.</param>
    public SqlOSFgaUnindexedOrderInterceptor(ILoggerFactory loggerFactory)
        : this(loggerFactory.CreateLogger<SqlOSFgaUnindexedOrderInterceptor>())
    {
    }

    private SqlOSFgaUnindexedOrderInterceptor(ILogger? logger) => _logger = logger;

    Expression IQueryExpressionInterceptor.QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        // Compilation happens once per query shape (the roots travel as parameters, so a page's shape is
        // shared across callers); everything here, including the warning, runs that once.
        if (eventData.Context is { } context
            && (_logger ?? LoggerFromOptions(context)) is { } logger
            && logger.IsEnabled(LogLevel.Warning))
        {
            var search = new Search();
            search.Visit(queryExpression);
            foreach (var (entityClrType, properties) in search.Chains())
            {
                WarnIfUnindexed(logger, context.Model, entityClrType, properties);
            }
        }

        return queryExpression;
    }

    private static ILogger? LoggerFromOptions(DbContext context)
        => context.GetService<IDbContextOptions>().FindExtension<SqlOSFgaOptionsExtension>()?.LoggerFactory
            ?.CreateLogger<SqlOSFgaUnindexedOrderInterceptor>();

    private static void WarnIfUnindexed(ILogger logger, IModel model, Type entityClrType, IReadOnlyList<string> properties)
    {
        if (model.FindEntityType(entityClrType) is not { } entityType
            || entityType.FindProperty(SqlOSFgaLineage.ScopeColumn) is null
            || entityType.GetTableName() is not { } table)
        {
            return;
        }

        var store = StoreObjectIdentifier.Table(table, entityType.GetSchema() ?? model.GetDefaultSchema());
        var columns = new List<string>(properties.Count);
        foreach (var property in properties)
        {
            if (entityType.FindProperty(property)?.GetColumnName(store) is not { } column)
            {
                return;
            }

            columns.Add(column);
        }

        var tables = TablesByModel.GetValue(model, SqlOSFgaScopeColumns.Tables);
        var scopeTable = tables.FirstOrDefault(t => t.Table == table && t.Schema == (entityType.GetSchema() ?? model.GetDefaultSchema()));
        if (scopeTable is null
            || IsPrefix(columns, scopeTable.KeyColumns)
            || scopeTable.Orders.Any(order => IsPrefix(columns, order.Columns)))
        {
            return;
        }

        // A mirrored index ends with the key, so the declaration to suggest is the order without its
        // trailing key columns (the whole order when that leaves nothing).
        var key = scopeTable.KeyColumns;
        var strip = 0;
        while (strip < Math.Min(columns.Count, key.Count)
               && string.Equals(columns[columns.Count - 1 - strip], key[key.Count - 1 - strip], StringComparison.Ordinal))
        {
            strip++;
        }

        var suggested = strip > 0 && strip < properties.Count ? properties.Take(properties.Count - strip).ToList() : properties;

        logger.LogWarning(
            "A filtered query over {Entity} sorts by ({Order}) and no declared index covers that order. The page "
            + "returns the right rows, but it reads the caller's whole scope instead of one index seek per access "
            + "root. Declare an index on ({Index}) — SqlOS mirrors each declared index per level — or sort by an "
            + "order an index covers.",
            entityType.DisplayName(),
            string.Join(", ", properties),
            string.Join(", ", suggested));
    }

    private static bool IsPrefix(IReadOnlyList<string> columns, IReadOnlyList<string> indexColumns)
    {
        if (columns.Count > indexColumns.Count)
        {
            return false;
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (!string.Equals(columns[i], indexColumns[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One pass over the query: the entity types whose scope column the filter reads, and every
    /// <c>OrderBy</c>/<c>ThenBy</c> chain with the properties it sorts by. A chain with a key that is not a
    /// plain property access is dropped rather than guessed at.
    /// </summary>
    private sealed class Search : ExpressionVisitor
    {
        private static readonly string[] OrderMethods =
            [nameof(Queryable.OrderBy), nameof(Queryable.OrderByDescending), nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending)];

        private readonly HashSet<Type> _filtered = [];
        private readonly List<MethodCallExpression> _orderCalls = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsGenericMethod
                && node.Method.GetGenericMethodDefinition() == EFPropertyMethod
                && node.Arguments[1] is ConstantExpression { Value: SqlOSFgaLineage.ScopeColumn }
                && typeof(IHasResourceId).IsAssignableFrom(node.Arguments[0].Type))
            {
                _filtered.Add(node.Arguments[0].Type);
            }

            if (node.Method.DeclaringType == typeof(Queryable) && node.Arguments.Count == 2 && OrderMethods.Contains(node.Method.Name))
            {
                _orderCalls.Add(node);
            }

            return base.VisitMethodCall(node);
        }

        /// <summary>The order chains over filtered entity types: (entity CLR type, properties in sort order).</summary>
        public IEnumerable<(Type EntityType, IReadOnlyList<string> Properties)> Chains()
        {
            var continued = new HashSet<Expression>(_orderCalls
                .Where(c => c.Method.Name is nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending))
                .Select(c => c.Arguments[0]));
            foreach (var last in _orderCalls.Where(c => !continued.Contains(c)))
            {
                var properties = new List<string>();
                Type? entityType = null;
                var call = last;
                while (true)
                {
                    if (KeyProperty(call) is not { } key)
                    {
                        entityType = null;
                        break;
                    }

                    properties.Insert(0, key.Property);
                    entityType = key.EntityType;
                    if (call.Method.Name is not (nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending)))
                    {
                        break;
                    }

                    if (call.Arguments[0] is not MethodCallExpression source || !_orderCalls.Contains(source))
                    {
                        // A chain whose start this pass cannot see is not judged.
                        entityType = null;
                        break;
                    }

                    call = source;
                }

                if (entityType is not null && _filtered.Contains(entityType))
                {
                    yield return (entityType, properties);
                }
            }
        }

        /// <summary>The property a key selector sorts by, when it is a plain property access on the entity.</summary>
        private static (Type EntityType, string Property)? KeyProperty(MethodCallExpression orderCall)
        {
            if (Unquote(orderCall.Arguments[1]) is not LambdaExpression lambda)
            {
                return null;
            }

            var body = lambda.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : lambda.Body;
            return body is MemberExpression { Expression: ParameterExpression parameter } member && parameter == lambda.Parameters[0]
                ? (parameter.Type, member.Member.Name)
                : null;
        }

        private static Expression Unquote(Expression expression)
            => expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;
    }
}
