using System.Linq.Expressions;

namespace SqlOS.Fga.Specifications;

/// <summary>
/// Entry point for the fluent specification builder.
/// <example>
/// <code>
/// var spec = PagedSpec.For&lt;Chain&gt;(c => c.Id)
///     .RequirePermission("CHAIN_VIEW")
///     .SortByString("name", c => c.Name, isDefault: true)
///     .Where(c => c.Name.Contains(search))
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
/// Fluent builder for creating <see cref="PagedSpecification{T}"/> instances without a dedicated class.
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
    /// Registers a string sort field.
    /// </summary>
    public PagedSpecificationBuilder<T> SortByString(
        string name, Expression<Func<T, string>> keySelector, bool isDefault = false)
    {
        _sorts[name] = new StringSortField<T>(keySelector);
        if (isDefault || _defaultSort == null)
            _defaultSort = name;
        return this;
    }

    /// <summary>
    /// Registers a sort field for any comparable type with custom serialization.
    /// </summary>
    public PagedSpecificationBuilder<T> SortBy<TKey>(
        string name,
        Expression<Func<T, TKey>> keySelector,
        Func<TKey, string> serialize,
        Func<string, TKey> deserialize,
        bool isDefault = false) where TKey : IComparable<TKey>
    {
        _sorts[name] = new ComparableSortField<T, TKey>(keySelector, serialize, deserialize);
        if (isDefault || _defaultSort == null)
            _defaultSort = name;
        return this;
    }

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
