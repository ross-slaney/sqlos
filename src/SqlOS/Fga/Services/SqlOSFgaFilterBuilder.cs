using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Services;

/// <summary>
/// Builds the predicate <c>BuildFilterAsync</c> returns: a row is visible when, at the level of one of the
/// caller's access roots, the row's resource has that root as its ancestor and access flows down to the row
/// from that level (every resource from the root down to the row is active), and the row's resource type is
/// the permission's. The predicate is the same for every caller and every table, and reads everything from
/// the row's own scope column. The roots are written into the predicate as parameters, one per level when
/// the caller holds one root at that level, which is what lets the database turn the page into one index
/// seek. A caller with more roots than <see cref="SqlOSFgaLineage.MaxListedRoots"/> is checked row by row by
/// <c>fn_IsResourceAccessible</c> instead, which probes the grants of each row's ancestors: the same rule, at a
/// cost that does not depend on how many grants the caller holds.
/// </summary>
internal static class SqlOSFgaFilterBuilder
{
    private static readonly MethodInfo PropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;
    private static readonly MethodInfo ParameterMethod = typeof(EF).GetMethod(nameof(EF.Parameter))!;
    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);
    private static readonly MethodInfo QueryableAnyNoPredicate = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 1);

    /// <param name="context">The context the predicate will run in.</param>
    /// <param name="roots">The caller's access roots, at most <see cref="SqlOSFgaLineage.MaxListedRoots"/> + 1 of them.</param>
    /// <param name="liveQuery">The composable query over <c>fn_ActiveSubjects</c>: the caller's subjects that are alive now.</param>
    /// <param name="subjectIdsJson">The caller's subject ids as JSON, for the row-by-row function.</param>
    /// <param name="permissionId">The permission's id.</param>
    /// <param name="typeSeq">The compact key of the permission's resource type, or null when it applies to every type.</param>
    /// <param name="levels">The number of levels the scope value holds (depth + 1).</param>
    public static Expression<Func<T, bool>> Build<T>(
        ISqlOSFgaDbContext context,
        IReadOnlyList<SqlOSFgaAccessRoot> roots,
        IQueryable<SqlOSFgaActiveSubject> liveQuery,
        string subjectIdsJson,
        string permissionId,
        int? typeSeq,
        int levels)
        where T : IHasResourceId
    {
        var entity = Expression.Parameter(typeof(T), "entity");
        if (roots.Count > SqlOSFgaLineage.MaxListedRoots)
        {
            return RowByRow<T>(context, entity, subjectIdsJson, permissionId);
        }

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
    /// <c>EXISTS (SELECT 1 FROM fn_IsResourceAccessible(entity.ResourceId, @subjects, @permission))</c>: the point
    /// check per row, through the context's registered table-valued function.
    /// </summary>
    private static Expression<Func<T, bool>> RowByRow<T>(ISqlOSFgaDbContext context, ParameterExpression entity, string subjectIdsJson, string permissionId)
    {
        var contextType = context.GetType();
        var function = contextType.GetMethod(nameof(ISqlOSFgaDbContext.IsResourceAccessible), [typeof(string), typeof(string), typeof(string)])
            ?? throw new InvalidOperationException($"{contextType.Name} does not declare IsResourceAccessible(string, string, string); see ISqlOSFgaDbContext.");
        var call = Expression.Call(
            Expression.Constant(context, contextType),
            function,
            Expression.Property(entity, nameof(IHasResourceId.ResourceId)),
            Parameter<string>(subjectIdsJson),
            Parameter<string>(permissionId));
        var any = Expression.Call(QueryableAnyNoPredicate.MakeGenericMethod(typeof(SqlOSFgaAccessibleResource)), call);
        return Expression.Lambda<Func<T, bool>>(any, entity);
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
