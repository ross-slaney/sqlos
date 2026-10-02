using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Services;

/// <summary>
/// Builds the predicate <c>BuildFilterAsync</c> returns: a row is visible when, at the level of one of the
/// caller's access roots, the row's scope column holds that root (it holds an ancestor only where access flows
/// down to the row from it), and the row's resource type is the permission's. One predicate for every caller,
/// every table, and any number of grants: the roots are written into it as parameters, an equality at a level
/// with one root (which lets the database answer a page with one index seek) and a membership test at a level
/// with several.
/// </summary>
internal static class SqlOSFgaFilterBuilder
{
    private static readonly MethodInfo PropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;
    private static readonly MethodInfo ParameterMethod = typeof(EF).GetMethod(nameof(EF.Parameter))!;
    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);
    private static readonly MethodInfo QueryableAnyNoPredicate = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 1);

    /// <param name="roots">The caller's access roots: every resource the caller holds a usable grant on, and its level.</param>
    /// <param name="liveQuery">The composable query over <c>fn_ActiveSubjects</c>: the caller's subjects that are alive now.</param>
    /// <param name="typeSeq">The compact key of the permission's resource type, or null when it applies to every type.</param>
    /// <param name="levels">The number of levels the scope value holds (depth + 1).</param>
    public static Expression<Func<T, bool>> Build<T>(
        IReadOnlyList<SqlOSFgaAccessRoot> roots,
        IQueryable<SqlOSFgaActiveSubject> liveQuery,
        int? typeSeq,
        int levels)
        where T : IHasResourceId
    {
        var entity = Expression.Parameter(typeof(T), "entity");
        var deepest = roots.Max(r => (int)r.Depth);
        if (deepest >= levels)
        {
            throw new InvalidOperationException(
                $"A grant sits at level {deepest} of the resource tree, but SqlOS is configured for {levels} levels. "
                + "Configure the same MaxResourceHierarchyDepth in SqlOSFgaOptions and in ApplySqlOSFgaModel.");
        }

        var reader = new ScopeReader(Property<byte[]>(entity, SqlOSFgaLineage.ScopeColumn));

        // (ancestor at level ℓ matches a root at ℓ) OR ..., one term per level that holds a root. The ancestor
        // is stored only where access flows down from that level, so matching it is the whole visibility test.
        Expression? body = null;
        foreach (var group in roots.GroupBy(r => (int)r.Depth).OrderBy(g => g.Key))
        {
            var seqs = group.Select(r => r.ResourceSeq).Distinct().ToArray();
            body = body is null ? reader.AncestorIs(group.Key, seqs) : Expression.OrElse(body, reader.AncestorIs(group.Key, seqs));
        }

        if (typeSeq is { } seq)
        {
            body = Expression.AndAlso(reader.TypeIs(seq), body!);
        }

        // The grants were read when the filter was built; the caller's own liveness is still checked when the
        // query runs, once per query (an uncorrelated EXISTS the engine evaluates as a one-time filter).
        var alive = Expression.Call(QueryableAnyNoPredicate.MakeGenericMethod(typeof(SqlOSFgaActiveSubject)), liveQuery.Expression);
        return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(alive, body!), entity);
    }

    /// <summary>
    /// How a query reads the row's scope value: the bytes of a level, read with the same expression the level's
    /// index is built on, guarded by the comparison on the depth byte that is that index's filter.
    /// </summary>
    private sealed class ScopeReader(Expression scope)
    {
        /// <summary>The row's ancestor at the level is one of the roots (an equality on a parameter for one root, a membership test for several).</summary>
        public Expression AncestorIs(int level, long[] roots)
        {
            var ancestor = Expression.Call(SqlOSFgaScope.AncestorAtMethod, scope, Expression.Constant(level));
            Expression matches = roots.Length == 1
                ? Expression.Equal(ancestor, Parameter<byte[]>(SqlOSFgaScope.Bytes(roots[0])))
                : Expression.Call(EnumerableContains.MakeGenericMethod(typeof(byte[])), Parameter<byte[][]>(roots.Select(SqlOSFgaScope.Bytes).ToArray()), ancestor);
            return Expression.AndAlso(Expression.Call(SqlOSFgaScope.ReachesMethod, scope, Expression.Constant(level)), matches);
        }

        /// <summary>The row's resource type is the permission's.</summary>
        public Expression TypeIs(int typeSeq)
            => Expression.Equal(
                Expression.Call(SqlOSFgaScope.TypeOfMethod, scope, Expression.Constant(SqlOSFgaLineage.ScopeTypeOffset)),
                Parameter<byte[]>(SqlOSFgaScope.Bytes(typeSeq)));
    }

    private static Expression Property<TValue>(Expression row, string name)
        => Expression.Call(PropertyMethod.MakeGenericMethod(typeof(TValue)), row, Expression.Constant(name));

    /// <summary>A value sent as a query parameter, so the query's shape (and its compiled plan) is shared across callers.</summary>
    private static Expression Parameter<TValue>(TValue value)
        => Expression.Call(ParameterMethod.MakeGenericMethod(typeof(TValue)), Expression.Constant(value, typeof(TValue)));
}
