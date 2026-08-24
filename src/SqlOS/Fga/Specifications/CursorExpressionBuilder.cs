using System.Linq.Expressions;
using System.Reflection;

namespace SqlOS.Fga.Specifications;

internal static class CursorExpressionBuilder
{
    /// <summary>
    /// Builds a cursor filter expression equivalent to:
    ///   x => x.Key > sortValue || (x.Key == sortValue &amp;&amp; x.Id > id)
    /// for ascending, or with &lt; for descending.
    /// </summary>
    public static Expression<Func<T, bool>> Build<T, TKey>(
        Expression<Func<T, TKey>> keySelector,
        Expression<Func<T, string>> idSelector,
        TKey sortValue,
        string id,
        bool descending)
    {
        var param = Expression.Parameter(typeof(T), "x");

        var keyBody = ParameterReplacer.Replace(keySelector.Body, keySelector.Parameters[0], param);
        var idBody = ParameterReplacer.Replace(idSelector.Body, idSelector.Parameters[0], param);

        var sortValConst = Expression.Constant(sortValue, typeof(TKey));
        var idConst = Expression.Constant(id, typeof(string));

        var primaryCondition = BuildComparison(keyBody, sortValConst, greaterThan: !descending);

        var keyEqual = Expression.Equal(keyBody, sortValConst);
        var idCondition = BuildComparison(idBody, idConst, greaterThan: !descending);
        var tiebreaker = Expression.AndAlso(keyEqual, idCondition);

        var combined = Expression.OrElse(primaryCondition, tiebreaker);
        return Expression.Lambda<Func<T, bool>>(combined, param);
    }

    private static Expression BuildComparison(Expression left, Expression right, bool greaterThan)
    {
        if (HasRelationalOperators(left.Type))
        {
            return greaterThan
                ? Expression.GreaterThan(left, right)
                : Expression.LessThan(left, right);
        }

        return BuildCompareToComparison(left, right, greaterThan);
    }

    private static bool HasRelationalOperators(Type type)
    {
        if (type == typeof(string) || type == typeof(bool) || type == typeof(Guid))
            return false;

        if (type == typeof(byte) || type == typeof(sbyte)
            || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint)
            || type == typeof(long) || type == typeof(ulong)
            || type == typeof(float) || type == typeof(double) || type == typeof(decimal)
            || type == typeof(char)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly) || type == typeof(TimeOnly)
            || type == typeof(TimeSpan))
        {
            return true;
        }

        return type.GetMethod(
            "op_GreaterThan",
            BindingFlags.Public | BindingFlags.Static,
            [type, type]) is not null;
    }

    private static Expression BuildCompareToComparison(Expression left, Expression right, bool greaterThan)
    {
        var typedCompare = left.Type.GetMethod(nameof(IComparable.CompareTo), [left.Type]);
        var comparison = typedCompare != null
            ? Expression.Call(left, typedCompare, right)
            : BuildObjectCompareTo(left, right);

        var zero = Expression.Constant(0);
        return greaterThan
            ? Expression.GreaterThan(comparison, zero)
            : Expression.LessThan(comparison, zero);
    }

    private static MethodCallExpression BuildObjectCompareTo(Expression left, Expression right)
    {
        var objectCompare = left.Type.GetMethod(nameof(IComparable.CompareTo), [typeof(object)])
            ?? throw new InvalidOperationException(
                $"Type {left.Type} does not implement CompareTo for cursor comparison.");
        return Expression.Call(left, objectCompare, Expression.Convert(right, typeof(object)));
    }
}

internal sealed class ParameterReplacer : ExpressionVisitor
{
    private readonly ParameterExpression _old;
    private readonly ParameterExpression _new;

    private ParameterReplacer(ParameterExpression old, ParameterExpression @new)
    {
        _old = old;
        _new = @new;
    }

    public static Expression Replace(Expression body, ParameterExpression oldParam, ParameterExpression newParam)
        => new ParameterReplacer(oldParam, newParam).Visit(body);

    protected override Expression VisitParameter(ParameterExpression node)
        => node == _old ? _new : base.VisitParameter(node);
}

internal static class ExpressionHelper
{
    public static Expression<Func<T, bool>> AndAlso<T>(
        Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var param = Expression.Parameter(typeof(T), "x");
        var leftBody = ParameterReplacer.Replace(left.Body, left.Parameters[0], param);
        var rightBody = ParameterReplacer.Replace(right.Body, right.Parameters[0], param);
        return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(leftBody, rightBody), param);
    }
}

internal static class SearchExpressionBuilder
{
    private static readonly MethodInfo ToLowerMethod =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    private static readonly MethodInfo ContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

    /// <summary>
    /// Builds: x => (prop1 != null &amp;&amp; prop1.ToLower().Contains(s))
    ///           || (prop2 != null &amp;&amp; prop2.ToLower().Contains(s)) || ...
    /// Returns null if search is null/whitespace or no properties given.
    /// </summary>
    public static Expression<Func<T, bool>>? Build<T>(
        string? search, Expression<Func<T, string?>>[] properties)
    {
        if (string.IsNullOrWhiteSpace(search) || properties.Length == 0)
            return null;

        var searchLower = Expression.Constant(search.ToLower());
        var param = Expression.Parameter(typeof(T), "x");
        var nullConst = Expression.Constant(null, typeof(string));
        Expression? combined = null;

        foreach (var propSelector in properties)
        {
            var propBody = ParameterReplacer.Replace(
                propSelector.Body, propSelector.Parameters[0], param);

            var notNull = Expression.NotEqual(propBody, nullConst);
            var toLower = Expression.Call(propBody, ToLowerMethod);
            var contains = Expression.Call(toLower, ContainsMethod, searchLower);
            var condition = Expression.AndAlso(notNull, contains);

            combined = combined == null ? condition : Expression.OrElse(combined, condition);
        }

        return Expression.Lambda<Func<T, bool>>(combined!, param);
    }
}
