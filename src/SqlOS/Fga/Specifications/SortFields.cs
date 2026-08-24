using System.Linq.Expressions;

namespace SqlOS.Fga.Specifications;

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
