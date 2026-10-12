using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace SqlOS.Fga.Paging;

/// <summary>A query split into its ordering and the rest.</summary>
/// <param name="EntityType">The entity the query reads.</param>
/// <param name="Inner">The query without its ordering: the application's filters and query options.</param>
/// <param name="Columns">The order columns, ending with the primary key.</param>
/// <param name="Descending">Whether every column is descending.</param>
internal sealed record SqlOSFgaPageOrdering(
    IEntityType EntityType,
    Expression Inner,
    IReadOnlyList<IProperty> Columns,
    bool Descending);

/// <summary>
/// Reads the query handed to <c>ToPageAsync</c>: a DbSet query with <c>Where</c> and query options
/// (<c>Include</c>, tracking, tags, query filters, split queries), ending with one
/// <c>OrderBy</c>/<c>OrderByDescending</c> and any number of <c>ThenBy</c>/<c>ThenByDescending</c>
/// in the same direction over mapped scalar properties. The primary key is appended when it is not
/// already in the order; anything after the key is dropped, since the key is unique.
/// </summary>
internal static class SqlOSFgaOrderReader
{
    private static readonly HashSet<string> QueryOptions = new(StringComparer.Ordinal)
    {
        nameof(EntityFrameworkQueryableExtensions.Include),
        nameof(EntityFrameworkQueryableExtensions.ThenInclude),
        nameof(EntityFrameworkQueryableExtensions.AsNoTracking),
        nameof(EntityFrameworkQueryableExtensions.AsNoTrackingWithIdentityResolution),
        nameof(EntityFrameworkQueryableExtensions.AsTracking),
        nameof(EntityFrameworkQueryableExtensions.TagWith),
        nameof(EntityFrameworkQueryableExtensions.TagWithCallSite),
        nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters),
        nameof(EntityFrameworkQueryableExtensions.IgnoreAutoIncludes),
        nameof(RelationalQueryableExtensions.AsSplitQuery),
        nameof(RelationalQueryableExtensions.AsSingleQuery),
    };

    public static SqlOSFgaPageOrdering Read(Expression query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var calls = new List<MethodCallExpression>();
        var current = query;
        while (current is MethodCallExpression call)
        {
            calls.Add(call);
            current = call.Arguments.Count > 0 ? call.Arguments[0] : null!;
        }

        if (current is not EntityQueryRootExpression root)
        {
            throw new ArgumentException(
                "ToPageAsync pages a query over a DbSet of the context the filter was built on.",
                nameof(query));
        }

        // Unsupported operators first: they are the most specific complaint.
        foreach (var call in calls)
        {
            if (!IsOrdering(call) && !IsWhere(call) && !IsQueryOption(call))
            {
                throw new ArgumentException(
                    call.Method.Name is nameof(Queryable.Select)
                        ? "Select is not supported before ToPageAsync. Page the entity query, then project the page: page.ToResponse(...) or page.Data.Select(...)."
                        : call.Method.Name is nameof(Queryable.Skip) or nameof(Queryable.Take)
                            ? $"{call.Method.Name} is not supported before ToPageAsync. Pass pageSize, and the cursor of the previous page."
                            : $"{call.Method.Name} is not supported before ToPageAsync. Use Where, Include, query options and one OrderBy with ThenBy.",
                    nameof(query));
            }
        }

        var trailing = calls.TakeWhile(IsOrdering).ToList();
        if (trailing.Count == 0)
        {
            throw new ArgumentException(
                calls.Any(IsOrdering)
                    ? "The ordering must be the last operators before ToPageAsync."
                    : "ToPageAsync needs an OrderBy: the order is the page order, and the cursor's position in it.",
                nameof(query));
        }

        if (!IsOrderBy(trailing[^1]) || trailing.Take(trailing.Count - 1).Any(IsOrderBy) || calls.Skip(trailing.Count).Any(IsOrdering))
        {
            throw new ArgumentException(
                "ToPageAsync needs one OrderBy followed by ThenBy as the last operators before it.",
                nameof(query));
        }

        // The innermost ordering call's source is the query without its ordering.
        var inner = trailing[^1].Arguments[0];
        var entityType = root.EntityType;
        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey is not { Properties: [var key] })
        {
            throw new ArgumentException(
                $"ToPageAsync needs {entityType.ClrType.Name} to have a single-column primary key.",
                nameof(query));
        }

        var columns = new List<IProperty>();
        bool? descending = null;
        trailing.Reverse();
        foreach (var call in trailing)
        {
            var property = ReadProperty(entityType, call);
            var callDescending = call.Method.Name is nameof(Queryable.OrderByDescending) or nameof(Queryable.ThenByDescending);
            if (descending is { } direction && direction != callDescending)
            {
                throw new ArgumentException(
                    "ToPageAsync orders every column in one direction: all ascending or all descending.",
                    nameof(query));
            }

            descending = callDescending;
            if (!columns.Contains(property))
            {
                columns.Add(property);
            }

            if (property == key)
            {
                break;
            }
        }

        if (columns[^1] != key)
        {
            columns.Add(key);
        }

        return new SqlOSFgaPageOrdering(entityType, inner, columns, descending ?? false);
    }

    private static IProperty ReadProperty(IEntityType entityType, MethodCallExpression call)
    {
        var selector = StripQuotes(call.Arguments[1]);
        var body = StripConvert(selector.Body);
        if (body is not MemberExpression { Expression: { } target, Member: var member } || target != selector.Parameters[0])
        {
            throw NotAColumn(entityType, selector);
        }

        var property = entityType.FindProperty(member.Name);
        if (property == null || property.IsShadowProperty())
        {
            throw NotAColumn(entityType, selector);
        }

        if (property.IsNullable)
        {
            throw new ArgumentException(
                $"ToPageAsync cannot order by '{property.Name}': it is nullable, and a cursor cannot position on null values. Order by non-nullable columns.",
                "query");
        }

        if (!SqlOSFgaPageCursor.IsSupported(property.ClrType))
        {
            throw new ArgumentException(
                $"ToPageAsync cannot order by '{property.Name}': a cursor cannot carry {property.ClrType.Name} values. Order by strings, numbers, Guid, bool, enums or date/time values.",
                "query");
        }

        return property;
    }

    private static ArgumentException NotAColumn(IEntityType entityType, LambdaExpression selector)
        => new(
            $"ToPageAsync orders by mapped scalar properties of {entityType.ClrType.Name}; '{selector}' is not one.",
            "query");

    private static bool IsOrdering(MethodCallExpression call)
        => call.Method.DeclaringType == typeof(Queryable)
            && call.Arguments.Count == 2
            && call.Method.Name is nameof(Queryable.OrderBy)
                or nameof(Queryable.OrderByDescending)
                or nameof(Queryable.ThenBy)
                or nameof(Queryable.ThenByDescending);

    private static bool IsOrderBy(MethodCallExpression call)
        => call.Method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending);

    private static bool IsWhere(MethodCallExpression call)
        => call.Method.DeclaringType == typeof(Queryable)
            && call.Method.Name == nameof(Queryable.Where)
            && call.Arguments.Count == 2
            && StripQuotes(call.Arguments[1]).Parameters.Count == 1;

    private static bool IsQueryOption(MethodCallExpression call)
        => (call.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                || call.Method.DeclaringType == typeof(RelationalQueryableExtensions))
            && QueryOptions.Contains(call.Method.Name);

    private static LambdaExpression StripQuotes(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            expression = quote.Operand;
        }

        return (LambdaExpression)expression;
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            expression = convert.Operand;
        }

        return expression;
    }
}
