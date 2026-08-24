using System.Linq.Expressions;

namespace SqlOS.Fga.Specifications;

/// <summary>
/// Entry point for the fluent specification builder. This is the supported way to define
/// authorized, cursor-paged lists.
/// <example>
/// <code>
/// var spec = PagedSpec.For&lt;Chain&gt;(c => c.Id)
///     .RequirePermission("CHAIN_VIEW")
///     .SortBy("name", c => c.Name, isDefault: true)
///     .SortBy("createdAt", c => c.CreatedAt)
///     .Search(search, c => c.Name, c => c.Description)
///     .Configure(q => q.Include(c => c.Locations))
///     .Build(pageSize, cursor);
/// </code>
/// </example>
/// </summary>
public static class PagedSpec
{
    /// <summary>
    /// Creates a specification builder for the given entity type.
    /// </summary>
    /// <param name="idSelector">Expression to access the entity's unique Id (used as cursor tiebreaker).</param>
    public static PagedSpecificationBuilder<T> For<T>(Expression<Func<T, string>> idSelector) where T : class
        => new(idSelector);
}

/// <summary>
/// Fluent builder for creating <see cref="PagedSpecification{T}"/> instances.
/// Prefer this over subclassing <see cref="PagedSpecification{T}"/>.
/// </summary>
public class PagedSpecificationBuilder<T> where T : class
{
    private readonly Expression<Func<T, string>> _idSelector;
    private string? _permission;
    private readonly Dictionary<string, ISortField<T>> _sorts = new(StringComparer.OrdinalIgnoreCase);
    private string? _defaultSort;
    private readonly List<Expression<Func<T, bool>>> _filters = new();
    private Func<IQueryable<T>, IQueryable<T>>? _queryConfigurator;

    internal PagedSpecificationBuilder(Expression<Func<T, string>> idSelector)
    {
        _idSelector = idSelector;
    }

    /// <summary>
    /// Sets the permission key required to access this data via the TVF.
    /// </summary>
    public PagedSpecificationBuilder<T> RequirePermission(string permission)
    {
        _permission = permission;
        return this;
    }

    /// <summary>
    /// Registers a string sort field. Equivalent to <see cref="SortBy(string, Expression{Func{T, string}}, bool)"/>.
    /// </summary>
    public PagedSpecificationBuilder<T> SortByString(
        string name, Expression<Func<T, string>> keySelector, bool isDefault = false)
        => SortBy(name, keySelector, isDefault);

    /// <summary>
    /// Registers a string sort field. Cursor values are the raw strings.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy(
        string name, Expression<Func<T, string>> keySelector, bool isDefault = false)
        => Register(name, new StringSortField<T>(keySelector), isDefault);

    /// <summary>
    /// Registers an <see cref="int"/> sort field with a culture-invariant cursor serializer.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy(
        string name, Expression<Func<T, int>> keySelector, bool isDefault = false)
        => RegisterComparable(name, keySelector, CursorSerializers.Serialize, CursorSerializers.DeserializeInt, isDefault);

    /// <summary>
    /// Registers a <see cref="long"/> sort field with a culture-invariant cursor serializer.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy(
        string name, Expression<Func<T, long>> keySelector, bool isDefault = false)
        => RegisterComparable(name, keySelector, CursorSerializers.Serialize, CursorSerializers.DeserializeLong, isDefault);

    /// <summary>
    /// Registers a <see cref="decimal"/> sort field with a culture-invariant round-trip cursor serializer.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy(
        string name, Expression<Func<T, decimal>> keySelector, bool isDefault = false)
        => RegisterComparable(name, keySelector, CursorSerializers.Serialize, CursorSerializers.DeserializeDecimal, isDefault);

    /// <summary>
    /// Registers a <see cref="double"/> sort field with a culture-invariant round-trip cursor serializer.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy(
        string name, Expression<Func<T, double>> keySelector, bool isDefault = false)
        => RegisterComparable(name, keySelector, CursorSerializers.Serialize, CursorSerializers.DeserializeDouble, isDefault);

    /// <summary>
    /// Registers a <see cref="DateTime"/> sort field. Uses the round-trip ("O") format and
    /// <see cref="System.Globalization.DateTimeStyles.RoundtripKind"/> so <see cref="DateTime.Kind"/> is preserved.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy(
        string name, Expression<Func<T, DateTime>> keySelector, bool isDefault = false)
        => RegisterComparable(name, keySelector, CursorSerializers.Serialize, CursorSerializers.DeserializeDateTime, isDefault);

    /// <summary>
    /// Registers a <see cref="DateTimeOffset"/> sort field with a round-trip ("O") cursor serializer.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy(
        string name, Expression<Func<T, DateTimeOffset>> keySelector, bool isDefault = false)
        => RegisterComparable(name, keySelector, CursorSerializers.Serialize, CursorSerializers.DeserializeDateTimeOffset, isDefault);

    /// <summary>
    /// Registers a <see cref="DateOnly"/> sort field with an invariant ISO date cursor serializer.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy(
        string name, Expression<Func<T, DateOnly>> keySelector, bool isDefault = false)
        => RegisterComparable(name, keySelector, CursorSerializers.Serialize, CursorSerializers.DeserializeDateOnly, isDefault);

    /// <summary>
    /// Registers a <see cref="Guid"/> sort field with a standard "D" cursor serializer.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy(
        string name, Expression<Func<T, Guid>> keySelector, bool isDefault = false)
        => RegisterComparable(name, keySelector, CursorSerializers.Serialize, CursorSerializers.DeserializeGuid, isDefault);

    /// <summary>
    /// Registers a <see cref="bool"/> sort field with an invariant True/False cursor serializer.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy(
        string name, Expression<Func<T, bool>> keySelector, bool isDefault = false)
        => RegisterComparable(name, keySelector, CursorSerializers.Serialize, CursorSerializers.DeserializeBool, isDefault);

    /// <summary>
    /// Registers a sort field for any comparable type with custom serialization.
    /// Use the typed <c>SortBy</c> overloads for common CLR keys; this is the escape hatch
    /// for types without a built-in serializer.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy<TKey>(
        string name,
        Expression<Func<T, TKey>> keySelector,
        Func<TKey, string> serialize,
        Func<string, TKey> deserialize,
        bool isDefault = false) where TKey : IComparable<TKey>
        => RegisterComparable(name, keySelector, serialize, deserialize, isDefault);

    /// <summary>
    /// Adds a filter expression. Multiple Where calls are combined with AND.
    /// </summary>
    public PagedSpecificationBuilder<T> Where(Expression<Func<T, bool>> filter)
    {
        _filters.Add(filter);
        return this;
    }

    /// <summary>
    /// Adds a case-insensitive search filter across one or more string properties (OR combined).
    /// No-op if search is null or whitespace.
    /// </summary>
    public PagedSpecificationBuilder<T> Search(
        string? search, params Expression<Func<T, string?>>[] properties)
    {
        var filter = SearchExpressionBuilder.Build(search, properties);
        if (filter != null)
            _filters.Add(filter!);
        return this;
    }

    /// <summary>
    /// Hook to configure the query before filtering/sorting (e.g., .Include() calls).
    /// </summary>
    public PagedSpecificationBuilder<T> Configure(Func<IQueryable<T>, IQueryable<T>> configurator)
    {
        _queryConfigurator = configurator;
        return this;
    }

    /// <summary>
    /// Builds the specification with the given pagination parameters.
    /// </summary>
    public PagedSpecification<T> Build(
        int pageSize, string? cursor = null, string? sortBy = null, string? sortDir = null)
        => Build(pageSize, cursor, sortBy,
            string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds the specification with the given pagination parameters.
    /// </summary>
    public PagedSpecification<T> Build(
        int pageSize, string? cursor = null, string? sortBy = null, bool descending = false)
    {
        var activeSortName = sortBy != null && _sorts.ContainsKey(sortBy) ? sortBy : _defaultSort;

        if (activeSortName == null)
            throw new InvalidOperationException(
                "No sort registered. Call SortByString or SortBy at least once before Build.");

        return new BuiltPagedSpecification<T>(
            _idSelector, _permission,
            new Dictionary<string, ISortField<T>>(_sorts, StringComparer.OrdinalIgnoreCase),
            activeSortName, descending,
            new List<Expression<Func<T, bool>>>(_filters),
            _queryConfigurator,
            pageSize, cursor);
    }

    private PagedSpecificationBuilder<T> RegisterComparable<TKey>(
        string name,
        Expression<Func<T, TKey>> keySelector,
        Func<TKey, string> serialize,
        Func<string, TKey> deserialize,
        bool isDefault) where TKey : IComparable<TKey>
        => Register(name, new ComparableSortField<T, TKey>(keySelector, serialize, deserialize), isDefault);

    private PagedSpecificationBuilder<T> Register(string name, ISortField<T> field, bool isDefault)
    {
        _sorts[name] = field;
        if (isDefault || _defaultSort == null)
            _defaultSort = name;
        return this;
    }
}

/// <summary>
/// Concrete specification produced by <see cref="PagedSpecificationBuilder{T}"/>.
/// </summary>
internal sealed class BuiltPagedSpecification<T> : PagedSpecification<T> where T : class
{
    private readonly Expression<Func<T, string>> _idSelector;
    private readonly Dictionary<string, ISortField<T>> _sorts;
    private readonly string _activeSortName;
    private readonly bool _descending;
    private readonly List<Expression<Func<T, bool>>> _filters;
    private readonly Func<IQueryable<T>, IQueryable<T>>? _queryConfigurator;
    private Func<T, string>? _compiledIdSelector;

    public override string? RequiredPermission { get; }

    internal BuiltPagedSpecification(
        Expression<Func<T, string>> idSelector,
        string? permission,
        Dictionary<string, ISortField<T>> sorts,
        string activeSortName,
        bool descending,
        List<Expression<Func<T, bool>>> filters,
        Func<IQueryable<T>, IQueryable<T>>? queryConfigurator,
        int pageSize,
        string? cursor)
    {
        _idSelector = idSelector;
        RequiredPermission = permission;
        _sorts = sorts;
        _activeSortName = activeSortName;
        _descending = descending;
        _filters = filters;
        _queryConfigurator = queryConfigurator;
        PageSize = pageSize;
        Cursor = cursor;
    }

    public override Expression<Func<T, bool>> ToExpression()
    {
        if (_filters.Count == 0)
            return _ => true;
        return _filters.Aggregate(ExpressionHelper.AndAlso);
    }

    public override IOrderedQueryable<T> ApplySort(IQueryable<T> query)
    {
        var sort = _sorts[_activeSortName];
        var ordered = sort.ApplyOrderBy(query, _descending);
        return _descending
            ? ordered.ThenByDescending(_idSelector)
            : ordered.ThenBy(_idSelector);
    }

    public override string BuildCursor(T entity)
    {
        var sort = _sorts[_activeSortName];
        var sortValue = sort.ExtractCursorValue(entity);
        _compiledIdSelector ??= _idSelector.Compile();
        var id = _compiledIdSelector(entity);
        return EncodeCursor(sortValue, id);
    }

    public override Expression<Func<T, bool>> GetCursorFilter(string cursor)
    {
        var (sortVal, id) = DecodeCursor(cursor);
        var sort = _sorts[_activeSortName];
        return sort.BuildCursorFilter(sortVal, id, _descending, _idSelector);
    }

    public override IQueryable<T> ConfigureQuery(IQueryable<T> query)
        => _queryConfigurator != null ? _queryConfigurator(query) : query;
}
