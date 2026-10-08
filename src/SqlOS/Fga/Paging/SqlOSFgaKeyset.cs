using System.Linq.Expressions;
using System.Reflection;

namespace SqlOS.Fga.Paging;

/// <summary>
/// Reads a keyset cursor out of an application filter: a strict comparison on the order's only column
/// (<c>e.Id &gt; last</c>), or the usual tiebreaking form over the whole order (<c>e.Name &gt; a || (e.Name == a
/// &amp;&amp; e.Id &gt; b)</c>, nested further for more columns; the <c>&gt;=</c>-then-<c>&gt;</c> form too),
/// with <c>&lt;</c> for a descending order. A comparison may be written as <c>string.Compare(e.Name, a) &gt; 0</c>
/// or <c>e.Name.CompareTo(a) &gt; 0</c>, the way EF Core keysets over strings are written. Such a filter becomes
/// the position every stream of the page seeks to, instead of a predicate tested row by row.
/// </summary>
internal static class SqlOSFgaKeyset
{
    public static bool TryRead(LambdaExpression filter, IReadOnlyList<string> orderProperties, bool descending, out object?[] after)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(orderProperties);
        after = [];
        if (filter.Parameters.Count != 1 || orderProperties.Count == 0)
        {
            return false;
        }

        var values = new object?[orderProperties.Count];
        if (ReadRest(Strip(filter.Body), 0, filter.Parameters[0], orderProperties, descending, values))
        {
            after = values;
            return true;
        }

        return false;
    }

    // Gt(P_i, v_i)                                        — the last column
    // Gt(P_i, v_i) || (Eq(P_i, v_i) && Rest(i + 1))        — the tiebreaking form
    // Ge(P_i, v_i) && (Gt(P_i, v_i) || Rest(i + 1))        — the nested form
    private static bool ReadRest(Expression body, int i, ParameterExpression parameter, IReadOnlyList<string> order, bool descending, object?[] values)
    {
        if (i >= order.Count)
        {
            return false;
        }

        var strict = descending ? ExpressionType.LessThan : ExpressionType.GreaterThan;
        var orEqual = descending ? ExpressionType.LessThanOrEqual : ExpressionType.GreaterThanOrEqual;
        if (i == order.Count - 1)
        {
            return Comparison(body, strict, order[i], parameter, out values[i]);
        }

        if (body is BinaryExpression { NodeType: ExpressionType.OrElse } or)
        {
            return Comparison(Strip(or.Left), strict, order[i], parameter, out values[i])
                   && Strip(or.Right) is BinaryExpression { NodeType: ExpressionType.AndAlso } and
                   && Comparison(Strip(and.Left), ExpressionType.Equal, order[i], parameter, out var again)
                   && Equals(again, values[i])
                   && ReadRest(Strip(and.Right), i + 1, parameter, order, descending, values);
        }

        if (body is BinaryExpression { NodeType: ExpressionType.AndAlso } nested)
        {
            return Comparison(Strip(nested.Left), orEqual, order[i], parameter, out values[i])
                   && Strip(nested.Right) is BinaryExpression { NodeType: ExpressionType.OrElse } inner
                   && Comparison(Strip(inner.Left), strict, order[i], parameter, out var again)
                   && Equals(again, values[i])
                   && ReadRest(Strip(inner.Right), i + 1, parameter, order, descending, values);
        }

        return false;
    }

    /// <summary><c>e.Property op value</c>, the value on the left with the mirrored operator, or either written through <c>Compare</c>/<c>CompareTo</c> against zero.</summary>
    private static bool Comparison(Expression body, ExpressionType op, string property, ParameterExpression parameter, out object? value)
    {
        value = null;
        if (!TryNormalize(body, out var left, out var actual, out var right))
        {
            return false;
        }

        if (actual == op && IsProperty(left, property, parameter) && TryEvaluate(right, parameter, out value))
        {
            return true;
        }

        return actual == Mirror(op) && IsProperty(right, property, parameter) && TryEvaluate(left, parameter, out value);
    }

    /// <summary>A comparison as (left, operator, right), with <c>Compare(a, b) op 0</c> and <c>0 op Compare(a, b)</c> read as <c>a op b</c>.</summary>
    private static bool TryNormalize(Expression body, out Expression left, out ExpressionType op, out Expression right)
    {
        left = right = null!;
        op = default;
        if (body is not BinaryExpression { NodeType: ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual or ExpressionType.Equal } binary)
        {
            return false;
        }

        left = Strip(binary.Left);
        right = Strip(binary.Right);
        op = binary.NodeType;
        if (IsZero(right) && TryCompareCall(left, out var a, out var b))
        {
            left = a;
            right = b;
        }
        else if (IsZero(left) && TryCompareCall(right, out a, out b))
        {
            left = a;
            right = b;
            op = Mirror(op);
        }

        return true;
    }

    private static bool TryCompareCall(Expression expression, out Expression a, out Expression b)
    {
        a = b = null!;
        if (expression is not MethodCallExpression call)
        {
            return false;
        }

        if (call.Method.Name == nameof(string.Compare) && call.Method.IsStatic && call.Method.DeclaringType == typeof(string) && call.Arguments.Count == 2)
        {
            a = Strip(call.Arguments[0]);
            b = Strip(call.Arguments[1]);
            return true;
        }

        if (call.Method.Name == nameof(IComparable.CompareTo) && call.Object is not null && call.Arguments.Count == 1)
        {
            a = Strip(call.Object);
            b = Strip(call.Arguments[0]);
            return true;
        }

        return false;
    }

    private static bool IsZero(Expression expression) => expression is ConstantExpression { Value: 0 };

    private static ExpressionType Mirror(ExpressionType op)
        => op switch
        {
            ExpressionType.GreaterThan => ExpressionType.LessThan,
            ExpressionType.LessThan => ExpressionType.GreaterThan,
            ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
            ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
            _ => op,
        };

    private static bool IsProperty(Expression expression, string property, ParameterExpression parameter)
        => expression is MemberExpression { Expression: ParameterExpression p } member && p == parameter && member.Member.Name == property;

    /// <summary>A non-null value that does not depend on the row: a constant, or a captured variable, evaluated.</summary>
    private static bool TryEvaluate(Expression expression, ParameterExpression parameter, out object? value)
    {
        value = null;
        if (new ParameterSearch(parameter).Found(expression))
        {
            return false;
        }

        try
        {
            value = expression is ConstantExpression constant ? constant.Value : Expression.Lambda(expression).Compile().DynamicInvoke();
            return value is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Without the conversions a comparison lifts its sides through (a nullable, an enum's number): they do not change what is compared.</summary>
    private static Expression Strip(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            expression = convert.Operand;
        }

        return expression;
    }

    private sealed class ParameterSearch(ParameterExpression parameter) : ExpressionVisitor
    {
        private bool _found;

        public bool Found(Expression expression)
        {
            Visit(expression);
            return _found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter)
            {
                _found = true;
            }

            return node;
        }
    }
}
