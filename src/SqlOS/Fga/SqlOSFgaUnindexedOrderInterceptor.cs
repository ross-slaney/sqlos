using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace SqlOS.Fga;

/// <summary>
/// Warns, once per query shape, when a query filtered by <c>BuildFilterAsync</c> sorts by an order that no
/// index on the entity covers. Such a page returns the right rows, but for a caller who sees most of the
/// table the database cannot read the order from an index, so it checks every row and sorts them all. The
/// warning names the entity, the order, and the <c>HasIndex</c> to declare. <c>UseSqlOS</c> on the context's
/// options attaches it; it logs through the context's logger.
/// </summary>
public sealed class SqlOSFgaUnindexedOrderInterceptor : IQueryExpressionInterceptor
{
    /// <summary>The one instance a context's options hold: EF Core builds an internal service provider per distinct interceptor instance.</summary>
    public static SqlOSFgaUnindexedOrderInterceptor Instance { get; } = new();

    private SqlOSFgaUnindexedOrderInterceptor()
    {
    }

    Expression IQueryExpressionInterceptor.QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        // Compilation happens once per query shape; everything here, including the warning, runs that once.
        if (eventData.Context is { } context
            && context.GetService<ILoggerFactory>().CreateLogger<SqlOSFgaUnindexedOrderInterceptor>() is { } logger
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

    private static void WarnIfUnindexed(ILogger logger, IModel model, Type entityClrType, IReadOnlyList<string> properties)
    {
        if (model.FindEntityType(entityClrType) is not { } entityType || entityType.FindPrimaryKey() is not { } key)
        {
            return;
        }

        // An index on the order without its trailing key columns serves the page too: both engines read the
        // ties in key order from the index's row locator or with a small sort per tie.
        var keyNames = key.Properties.Select(p => p.Name).ToList();
        var strip = 0;
        while (strip < Math.Min(properties.Count, keyNames.Count)
               && string.Equals(properties[properties.Count - 1 - strip], keyNames[keyNames.Count - 1 - strip], StringComparison.Ordinal))
        {
            strip++;
        }

        var stripped = strip > 0 && strip < properties.Count ? properties.Take(properties.Count - strip).ToList() : properties;
        var indexes = entityType.GetIndexes().Select(i => i.Properties.Select(p => p.Name).ToList()).Append(keyNames).ToList();
        if (indexes.Any(index => IsPrefix(properties, index) || IsPrefix(stripped, index)))
        {
            return;
        }

        logger.LogWarning(
            "A filtered query over {Entity} sorts by ({Order}) and no index on it covers that order. The page returns "
            + "the right rows, but for a caller who sees most of the table the database checks every row and sorts "
            + "them all, because it cannot read the order from an index. Declare HasIndex({Index}), or sort by an "
            + "order an index covers.",
            entityType.DisplayName(),
            string.Join(", ", properties),
            string.Join(", ", stripped));
    }

    private static bool IsPrefix(IReadOnlyList<string> columns, IReadOnlyList<string> indexColumns)
    {
        if (columns.Count == 0 || columns.Count > indexColumns.Count)
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
    /// One pass over the query: the entity types a SqlOS filter is applied to (a <c>Where</c> whose predicate
    /// reads the visible set or checks the row), and every <c>OrderBy</c>/<c>ThenBy</c> chain with the
    /// properties it sorts by. A chain with a key that is not a plain property access is not judged.
    /// </summary>
    private sealed class Search : ExpressionVisitor
    {
        private static readonly string[] OrderMethods =
            [nameof(Queryable.OrderBy), nameof(Queryable.OrderByDescending), nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending)];

        private readonly HashSet<Type> _filtered = [];
        private readonly List<MethodCallExpression> _orderCalls = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Queryable) && node.Arguments.Count == 2)
            {
                if (node.Method.Name == nameof(Queryable.Where)
                    && Unquote(node.Arguments[1]) is LambdaExpression predicate
                    && FilterCall.In(predicate.Body))
                {
                    _filtered.Add(predicate.Parameters[0].Type);
                }

                if (OrderMethods.Contains(node.Method.Name))
                {
                    _orderCalls.Add(node);
                }
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

    /// <summary>Whether an expression calls one of the filter's functions (the visible set, or the row check).</summary>
    private sealed class FilterCall : ExpressionVisitor
    {
        private bool _found;

        public static bool In(Expression expression)
        {
            var visitor = new FilterCall();
            visitor.Visit(expression);
            return visitor._found;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method == SqlOSFgaFunctions.VisibleSetMethod || node.Method == SqlOSFgaFunctions.CheckRowMethod)
            {
                _found = true;
                return node;
            }

            return base.VisitMethodCall(node);
        }
    }
}
