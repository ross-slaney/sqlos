using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Paging;

/// <summary>
/// Loads a page's rows by key through the application's query (its filters, order and page removed, its
/// <c>Include</c>, tracking and tags kept), projected the way the query projects when it does, and puts them
/// in the page's order.
/// </summary>
internal static class SqlOSFgaPageLoader
{
    private static readonly MethodInfo EFPropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;
    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);
    private static readonly MethodInfo LoadProjectedMethod = typeof(SqlOSFgaPageLoader).GetMethod(nameof(LoadProjectedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// The rows with the given keys (as the database returned them), as a <c>List&lt;T&gt;</c>, or a list of
    /// what <paramref name="projection"/> projects each row to when the query has a <c>Select</c>.
    /// </summary>
    public static async Task<IList> LoadAsync<T>(SqlOSFgaPageQuery<T> query, IReadOnlyList<object> keys, LambdaExpression? projection, CancellationToken cancellationToken)
        where T : class, IHasResourceId
    {
        var key = query.Key.Property;
        var converter = key.GetValueConverter();
        var clrKeys = keys.Select(k => converter is null ? k : converter.ConvertFromProvider(k)!).ToList();
        var listType = typeof(List<>).MakeGenericType(key.ClrType);
        var typedKeys = (IList)Activator.CreateInstance(listType)!;
        foreach (var k in clrKeys)
        {
            typedKeys.Add(k);
        }

        // The keys reach the query as a member of a captured object, the way a C# closure captures a local:
        // EF Core then sends them as one parameter and compiles the query once per shape, where a constant
        // list would be compiled into the SQL and the query compiled again for every page.
        var holder = Activator.CreateInstance(typeof(KeyHolder<>).MakeGenericType(key.ClrType), typedKeys)!;
        var e = Expression.Parameter(typeof(T), "e");
        var predicate = Expression.Lambda<Func<T, bool>>(
            Expression.Call(
                EnumerableContains.MakeGenericMethod(key.ClrType),
                Expression.Field(Expression.Constant(holder), nameof(KeyHolder<int>.Keys)),
                Expression.Call(EFPropertyMethod.MakeGenericMethod(key.ClrType), e, Expression.Constant(key.Name))),
            e);

        if (projection is not null)
        {
            var task = (Task<IList>)LoadProjectedMethod
                .MakeGenericMethod(typeof(T), key.ClrType, projection.ReturnType)
                .Invoke(null, [query, predicate, clrKeys, projection, cancellationToken])!;
            return await task.ConfigureAwait(false);
        }

        var loaded = await query.Materialization.Where(predicate).ToListAsync(cancellationToken).ConfigureAwait(false);
        var getter = key.GetGetter();
        return InPageOrder(loaded, clrKeys, row => getter.GetClrValue(row)!, row => row);
    }

    private static async Task<IList> LoadProjectedAsync<T, TKey, TResult>(
        SqlOSFgaPageQuery<T> query,
        Expression<Func<T, bool>> predicate,
        IReadOnlyList<object> keys,
        LambdaExpression projection,
        CancellationToken cancellationToken)
        where T : class, IHasResourceId
    {
        // The row's key rides along with the projection, so the rows can be put in the page's order: the
        // projection itself need not include it.
        var e = Expression.Parameter(typeof(T), "e");
        var body = new ParameterReplacer(projection.Parameters[0], e).Visit(projection.Body);
        var keyed = typeof(Keyed<TKey, TResult>);
        var selector = Expression.Lambda<Func<T, Keyed<TKey, TResult>>>(
            Expression.MemberInit(
                Expression.New(keyed),
                Expression.Bind(keyed.GetProperty(nameof(Keyed<int, int>.Key))!, Expression.Call(EFPropertyMethod.MakeGenericMethod(typeof(TKey)), e, Expression.Constant(query.Key.Property.Name))),
                Expression.Bind(keyed.GetProperty(nameof(Keyed<int, int>.Value))!, body)),
            e);
        var loaded = await query.Materialization.Where(predicate).Select(selector).ToListAsync(cancellationToken).ConfigureAwait(false);
        return InPageOrder(loaded, keys, row => row.Key!, row => row.Value);
    }

    private static List<TResult> InPageOrder<TRow, TResult>(List<TRow> loaded, IReadOnlyList<object> keys, Func<TRow, object> keyOf, Func<TRow, TResult> valueOf)
    {
        var order = new Dictionary<object, int>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            order[keys[i]] = i;
        }

        return loaded
            .Select(row => (Row: row, Index: order.TryGetValue(keyOf(row), out var index) ? index : int.MaxValue))
            .Where(x => x.Index != int.MaxValue)
            .OrderBy(x => x.Index)
            .Select(x => valueOf(x.Row))
            .ToList();
    }

    /// <summary>The page's keys, held the way a closure holds a captured local.</summary>
    private sealed class KeyHolder<TKey>(List<TKey> keys)
    {
#pragma warning disable SA1401 // A field, as a closure's: EF Core parameterizes member access on a constant.
        public readonly List<TKey> Keys = keys;
#pragma warning restore SA1401
    }

    /// <summary>A projected row with its key.</summary>
    private sealed class Keyed<TKey, TValue>
    {
        public TKey? Key { get; set; }

        public TValue Value { get; set; } = default!;
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
