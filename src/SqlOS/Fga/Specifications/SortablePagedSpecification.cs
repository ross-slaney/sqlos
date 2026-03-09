using System.Linq.Expressions;

namespace SqlOS.Fga.Specifications;

/// <summary>
/// A PagedSpecification that auto-generates cursor pagination logic from registered sort definitions.
/// <para>
/// Instead of manually implementing <see cref="PagedSpecification{T}.ApplySort"/>,
/// <see cref="PagedSpecification{T}.BuildCursor"/>, and <see cref="PagedSpecification{T}.GetCursorFilter"/>
/// (which must stay in sync), register sort fields in the constructor and let the base class handle the rest.
/// </para>
/// <example>
/// <code>
/// public class GetProductsSpecification : SortablePagedSpecification&lt;Product&gt;
/// {
///     public GetProductsSpecification(int pageSize, string? search, string? sortBy, bool desc)
///     {
///         PageSize = pageSize;
///         RegisterStringSort("name", p => p.Name, isDefault: true);
///         RegisterStringSort("sku", p => p.Sku);
///         RegisterSort("price", p => p.Price,
///             serialize: v => v.ToString(CultureInfo.InvariantCulture),
///             deserialize: s => decimal.Parse(s, CultureInfo.InvariantCulture));
///         SetActiveSort(sortBy, desc);
///
///         if (!string.IsNullOrWhiteSpace(search))
///             AddFilter(p => p.Name.Contains(search));
///     }
///     public override string? RequiredPermission => "PRODUCT_VIEW";
///     protected override Expression&lt;Func&lt;Product, string&gt;&gt; IdSelector => p => p.Id;
/// }
/// </code>
/// </example>
/// </summary>
public abstract class SortablePagedSpecification<T> : PagedSpecification<T> where T : class
{
    private readonly Dictionary<string, ISortField<T>> _sorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Expression<Func<T, bool>>> _filters = new();
    private string? _activeSortName;
    private bool _descending;
    private Func<T, string>? _compiledIdSelector;

    /// <summary>
    /// Expression to access the entity's unique Id property, used as the tiebreaker for cursor pagination.
    /// </summary>
    protected abstract Expression<Func<T, string>> IdSelector { get; }

    /// <summary>
    /// Registers a string sort field. The cursor triad (ApplySort, BuildCursor, GetCursorFilter)
    /// is auto-generated from the key selector.
    /// </summary>
    protected void RegisterStringSort(string name, Expression<Func<T, string>> keySelector, bool isDefault = false)
    {
        _sorts[name] = new StringSortField<T>(keySelector);
        if (isDefault || _activeSortName == null)
            _activeSortName = name;
    }

    /// <summary>
    /// Registers a sort field for any comparable type with custom serialization for cursor encoding.
    /// </summary>
    protected void RegisterSort<TKey>(
        string name,
        Expression<Func<T, TKey>> keySelector,
        Func<TKey, string> serialize,
        Func<string, TKey> deserialize,
        bool isDefault = false)
        where TKey : IComparable<TKey>
    {
        _sorts[name] = new ComparableSortField<T, TKey>(keySelector, serialize, deserialize);
        if (isDefault || _activeSortName == null)
            _activeSortName = name;
    }

    /// <summary>
    /// Sets which registered sort to use and its direction.
    /// </summary>
    protected void SetActiveSort(string? sortBy, bool descending = false)
    {
        if (sortBy != null && _sorts.ContainsKey(sortBy))
            _activeSortName = sortBy;
        _descending = descending;
    }

    /// <summary>
    /// Adds a filter expression. Multiple filters are combined with AND in <see cref="ToExpression"/>.
    /// </summary>
    protected void AddFilter(Expression<Func<T, bool>> filter)
    {
        _filters.Add(filter);
    }

    /// <summary>
    /// Adds a case-insensitive search filter across one or more string properties (OR combined).
    /// No-op if search is null or whitespace.
    /// </summary>
    protected void Search(string? search, params Expression<Func<T, string?>>[] properties)
    {
        var filter = SearchExpressionBuilder.Build(search, properties);
        if (filter != null)
            _filters.Add(filter!);
    }

    /// <summary>
    /// Returns all added filters combined with AND, or a pass-through if none were added.
    /// Override for custom filter logic.
    /// </summary>
    public override Expression<Func<T, bool>> ToExpression()
    {
        if (_filters.Count == 0)
            return _ => true;
        return _filters.Aggregate(ExpressionHelper.AndAlso);
    }

    /// <inheritdoc />
    public sealed override IOrderedQueryable<T> ApplySort(IQueryable<T> query)
    {
        var sort = GetActiveSort();
        var ordered = sort.ApplyOrderBy(query, _descending);
        return _descending
            ? ordered.ThenByDescending(IdSelector)
            : ordered.ThenBy(IdSelector);
    }

    /// <inheritdoc />
    public sealed override string BuildCursor(T entity)
    {
        var sort = GetActiveSort();
        var sortValue = sort.ExtractCursorValue(entity);
        _compiledIdSelector ??= IdSelector.Compile();
        var id = _compiledIdSelector(entity);
        return EncodeCursor(sortValue, id);
    }

    /// <inheritdoc />
    public sealed override Expression<Func<T, bool>> GetCursorFilter(string cursor)
    {
        var (sortVal, id) = DecodeCursor(cursor);
        var sort = GetActiveSort();
        return sort.BuildCursorFilter(sortVal, id, _descending, IdSelector);
    }

    private ISortField<T> GetActiveSort()
    {
        if (_activeSortName == null || !_sorts.TryGetValue(_activeSortName, out var sort))
            throw new InvalidOperationException(
                $"No sort registered with name '{_activeSortName}'. " +
                "Register at least one sort using RegisterStringSort or RegisterSort.");
        return sort;
    }
}

// ────────────────────────────────────────────────────────────────
// Internal sort field abstractions
// ────────────────────────────────────────────────────────────────

internal interface ISortField<T>
{
    IOrderedQueryable<T> ApplyOrderBy(IQueryable<T> query, bool descending);
    string ExtractCursorValue(T entity);
    Expression<Func<T, bool>> BuildCursorFilter(
        string serializedSortValue, string id, bool descending,
        Expression<Func<T, string>> idSelector);
}

internal sealed class StringSortField<T> : ISortField<T>
{
    private readonly Expression<Func<T, string>> _keySelector;
    private readonly Func<T, string> _compiled;

    public StringSortField(Expression<Func<T, string>> keySelector)
    {
        _keySelector = keySelector;
        _compiled = keySelector.Compile();
    }

    public IOrderedQueryable<T> ApplyOrderBy(IQueryable<T> query, bool descending)
        => descending ? query.OrderByDescending(_keySelector) : query.OrderBy(_keySelector);

    public string ExtractCursorValue(T entity) => _compiled(entity);

    public Expression<Func<T, bool>> BuildCursorFilter(
        string serializedSortValue, string id, bool descending,
        Expression<Func<T, string>> idSelector)
        => CursorExpressionBuilder.Build(_keySelector, idSelector, serializedSortValue, id, descending);
}

internal sealed class ComparableSortField<T, TKey> : ISortField<T> where TKey : IComparable<TKey>
{
    private readonly Expression<Func<T, TKey>> _keySelector;
    private readonly Func<T, TKey> _compiled;
    private readonly Func<TKey, string> _serialize;
    private readonly Func<string, TKey> _deserialize;

    public ComparableSortField(
        Expression<Func<T, TKey>> keySelector,
        Func<TKey, string> serialize,
        Func<string, TKey> deserialize)
    {
        _keySelector = keySelector;
        _compiled = keySelector.Compile();
        _serialize = serialize;
        _deserialize = deserialize;
    }

    public IOrderedQueryable<T> ApplyOrderBy(IQueryable<T> query, bool descending)
        => descending ? query.OrderByDescending(_keySelector) : query.OrderBy(_keySelector);

    public string ExtractCursorValue(T entity) => _serialize(_compiled(entity));

    public Expression<Func<T, bool>> BuildCursorFilter(
        string serializedSortValue, string id, bool descending,
        Expression<Func<T, string>> idSelector)
    {
        var sortValue = _deserialize(serializedSortValue);
        return CursorExpressionBuilder.Build(_keySelector, idSelector, sortValue, id, descending);
    }
}

// ────────────────────────────────────────────────────────────────
// Expression tree helpers for building cursor filters
// ────────────────────────────────────────────────────────────────

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

        // Primary: key > sortValue (ascending) or key < sortValue (descending)
        var primaryCondition = BuildComparison(keyBody, sortValConst, greaterThan: !descending);

        // Tiebreaker: key == sortValue && id > idValue (or < for descending)
        var keyEqual = Expression.Equal(keyBody, sortValConst);
        var idCondition = BuildStringComparison(idBody, idConst, greaterThan: !descending);
        var tiebreaker = Expression.AndAlso(keyEqual, idCondition);

        var combined = Expression.OrElse(primaryCondition, tiebreaker);
        return Expression.Lambda<Func<T, bool>>(combined, param);
    }

    private static Expression BuildComparison(Expression left, Expression right, bool greaterThan)
    {
        // Strings don't have > / < operators — use CompareTo
        if (left.Type == typeof(string))
            return BuildStringComparison(left, right, greaterThan);

        // Numeric and other types with comparison operators
        return greaterThan
            ? Expression.GreaterThan(left, right)
            : Expression.LessThan(left, right);
    }

    private static Expression BuildStringComparison(Expression left, Expression right, bool greaterThan)
    {
        var compareMethod = typeof(string).GetMethod(nameof(string.CompareTo), [typeof(string)])!;
        var comparison = Expression.Call(left, compareMethod, right);
        var zero = Expression.Constant(0);
        return greaterThan
            ? Expression.GreaterThan(comparison, zero)
            : Expression.LessThan(comparison, zero);
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
    private static readonly System.Reflection.MethodInfo ToLowerMethod =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    private static readonly System.Reflection.MethodInfo ContainsMethod =
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
