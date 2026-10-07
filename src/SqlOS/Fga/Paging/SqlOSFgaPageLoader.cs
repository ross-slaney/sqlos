using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Paging;

/// <summary>
/// Loads a page's rows by key through the application's query (its filters and order removed, its
/// <c>Include</c>, tracking and tags kept), and puts them in the page's order.
/// </summary>
internal static class SqlOSFgaPageLoader
{
    private static readonly MethodInfo EFPropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;
    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);

    public static async Task<IReadOnlyList<T>> LoadAsync<T>(SqlOSFgaPageQuery<T> query, IReadOnlyList<object> keys, CancellationToken cancellationToken)
        where T : class, IHasResourceId
    {
        var key = query.Key.Property;
        var typedKeys = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(key.ClrType))!;
        foreach (var k in keys)
        {
            typedKeys.Add(k);
        }

        var e = Expression.Parameter(typeof(T), "e");
        var predicate = Expression.Lambda<Func<T, bool>>(
            Expression.Call(
                EnumerableContains.MakeGenericMethod(key.ClrType),
                Expression.Constant(typedKeys),
                Expression.Call(EFPropertyMethod.MakeGenericMethod(key.ClrType), e, Expression.Constant(key.Name))),
            e);
        var loaded = await query.Materialization.Where(predicate).ToListAsync(cancellationToken);

        var order = new Dictionary<object, int>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            order[keys[i]] = i;
        }

        var getter = key.GetGetter();
        return loaded
            .Select(row => (Row: row, Index: order.TryGetValue(getter.GetClrValue(row)!, out var index) ? index : int.MaxValue))
            .Where(x => x.Index != int.MaxValue)
            .OrderBy(x => x.Index)
            .Select(x => x.Row)
            .ToList();
    }
}
