using System.Globalization;
using System.Linq.Expressions;

namespace SqlOS.Pagination;

/// <summary>
/// Deterministic keyset definition: sort order, cursor encoding, and the matching
/// keyset predicate stay aligned and always end in a unique tiebreaker.
/// </summary>
public sealed class SqlOSKeyset<T>
    where T : class
{
    private readonly List<IKeysetColumn<T>> _columns = [];

    private SqlOSKeyset()
    {
    }

    public static SqlOSKeyset<T> Create() => new();

    public SqlOSKeyset<T> Ascending<TKey>(Expression<Func<T, TKey>> selector)
        where TKey : IComparable<TKey>
        => Add(selector, descending: false);

    public SqlOSKeyset<T> Descending<TKey>(Expression<Func<T, TKey>> selector)
        where TKey : IComparable<TKey>
        => Add(selector, descending: true);

    public SqlOSKeyset<T> ThenAscending<TKey>(Expression<Func<T, TKey>> selector)
        where TKey : IComparable<TKey>
        => Ascending(selector);

    public SqlOSKeyset<T> ThenDescending<TKey>(Expression<Func<T, TKey>> selector)
        where TKey : IComparable<TKey>
        => Descending(selector);

    public int ColumnCount => _columns.Count;

    public IOrderedQueryable<T> ApplySort(IQueryable<T> query)
    {
        EnsureColumns();
        IOrderedQueryable<T>? ordered = null;
        foreach (var column in _columns)
        {
            ordered = ordered == null
                ? column.ApplyOrderBy(query)
                : column.ApplyThenBy(ordered);
        }

        return ordered!;
    }

    public IReadOnlyList<string> Encode(T row)
    {
        EnsureColumns();
        return _columns.Select(column => column.Encode(row)).ToArray();
    }

    public Expression<Func<T, bool>> After(IReadOnlyList<string> encodedValues)
    {
        EnsureColumns();
        if (encodedValues.Count != _columns.Count)
        {
            throw new SqlOSCursorException("The cursor is invalid.");
        }

        var param = Expression.Parameter(typeof(T), "x");
        Expression? combined = null;
        Expression? prefixEqual = null;

        for (var index = 0; index < _columns.Count; index++)
        {
            var column = _columns[index];
            object parsed;
            try
            {
                parsed = column.Parse(encodedValues[index]);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new SqlOSCursorException("The cursor is invalid.");
            }

            var body = KeysetExpression.ReplaceParameter(column.Body, column.Parameter, param);
            var constant = Expression.Constant(parsed, column.KeyType);
            var comparison = KeysetExpression.Compare(body, constant, greaterThan: !column.Descending);
            var term = prefixEqual == null ? comparison : Expression.AndAlso(prefixEqual, comparison);
            combined = combined == null ? term : Expression.OrElse(combined, term);
            var equal = Expression.Equal(body, constant);
            prefixEqual = prefixEqual == null ? equal : Expression.AndAlso(prefixEqual, equal);
        }

        return Expression.Lambda<Func<T, bool>>(combined!, param);
    }

    private SqlOSKeyset<T> Add<TKey>(Expression<Func<T, TKey>> selector, bool descending)
        where TKey : IComparable<TKey>
    {
        _columns.Add(new KeysetColumn<T, TKey>(selector, descending));
        return this;
    }

    private void EnsureColumns()
    {
        if (_columns.Count == 0)
        {
            throw new InvalidOperationException("A keyset requires at least one column.");
        }
    }
}

internal interface IKeysetColumn<T>
{
    Type KeyType { get; }
    bool Descending { get; }
    ParameterExpression Parameter { get; }
    Expression Body { get; }
    IOrderedQueryable<T> ApplyOrderBy(IQueryable<T> query);
    IOrderedQueryable<T> ApplyThenBy(IOrderedQueryable<T> query);
    string Encode(T row);
    object Parse(string value);
}

internal sealed class KeysetColumn<T, TKey> : IKeysetColumn<T>
    where TKey : IComparable<TKey>
{
    private readonly Expression<Func<T, TKey>> _selector;
    private readonly Func<T, TKey> _compiled;

    public KeysetColumn(Expression<Func<T, TKey>> selector, bool descending)
    {
        _selector = selector;
        _compiled = selector.Compile();
        Descending = descending;
        Parameter = selector.Parameters[0];
        Body = selector.Body;
        KeyType = typeof(TKey);
    }

    public Type KeyType { get; }
    public bool Descending { get; }
    public ParameterExpression Parameter { get; }
    public Expression Body { get; }

    public IOrderedQueryable<T> ApplyOrderBy(IQueryable<T> query)
        => Descending ? query.OrderByDescending(_selector) : query.OrderBy(_selector);

    public IOrderedQueryable<T> ApplyThenBy(IOrderedQueryable<T> query)
        => Descending ? query.ThenByDescending(_selector) : query.ThenBy(_selector);

    public string Encode(T row) => Format(_compiled(row));

    public object Parse(string value) => ParseValue(value);

    private static string Format(TKey value)
    {
        if (value is DateTime dateTime)
        {
            return dateTime.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        if (value is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static TKey ParseValue(string value)
    {
        var type = typeof(TKey);
        if (type == typeof(string))
        {
            return (TKey)(object)value;
        }

        if (type == typeof(DateTime))
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                throw new FormatException();
            }

            return (TKey)(object)new DateTime(ticks, DateTimeKind.Utc);
        }

        if (type == typeof(int))
        {
            return (TKey)(object)int.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(long))
        {
            return (TKey)(object)long.Parse(value, CultureInfo.InvariantCulture);
        }

        if (type == typeof(bool))
        {
            return (TKey)(object)(value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        }

        throw new FormatException();
    }
}

internal static class KeysetExpression
{
    public static Expression ReplaceParameter(Expression body, ParameterExpression from, ParameterExpression to)
        => new ParameterReplacer(from, to).Visit(body);

    public static Expression Compare(Expression left, Expression right, bool greaterThan)
    {
        if (left.Type == typeof(string))
        {
            var compare = typeof(string).GetMethod(nameof(string.CompareTo), [typeof(string)])!;
            var comparison = Expression.Call(left, compare, right);
            var zero = Expression.Constant(0);
            return greaterThan
                ? Expression.GreaterThan(comparison, zero)
                : Expression.LessThan(comparison, zero);
        }

        return greaterThan
            ? Expression.GreaterThan(left, right)
            : Expression.LessThan(left, right);
    }

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly ParameterExpression _to;

        public ParameterReplacer(ParameterExpression from, ParameterExpression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => node == _from ? _to : base.VisitParameter(node);
    }
}
