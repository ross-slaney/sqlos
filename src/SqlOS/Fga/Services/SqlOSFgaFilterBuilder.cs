using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Services;

/// <summary>
/// Builds the predicate <c>BuildFilterAsync</c> returns: a row is visible when, at the level of one of the
/// caller's access roots, the row's resource has that root as its ancestor and the row's reach includes that
/// level (every resource from the root down to the row is active), and the row's resource type is the
/// permission's. The predicate is the same for every caller and every table. It reads the row's lineage from
/// the resources table by the row's resource id, or, on a table that carries the scope columns, from the row
/// itself. The roots are written into the predicate as parameters, one per level when the caller holds one
/// root at that level, which is what lets the database turn the page into one index seek. A caller with more
/// roots than <see cref="SqlOSFgaLineage.MaxListedRoots"/> is checked row by row by <c>fn_IsResourceAccessible</c>
/// instead, which probes the grants of each row's ancestors: the same rule, at a cost that does not depend on
/// how many grants the caller holds.
/// </summary>
internal static class SqlOSFgaFilterBuilder
{
    private static readonly MethodInfo PropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;
    private static readonly MethodInfo ParameterMethod = typeof(EF).GetMethod(nameof(EF.Parameter))!;
    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);
    private static readonly MethodInfo QueryableAny = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 2);
    private static readonly MethodInfo QueryableAnyNoPredicate = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 1);

    /// <param name="context">The context the predicate will run in.</param>
    /// <param name="roots">The caller's access roots, at most <see cref="SqlOSFgaLineage.MaxListedRoots"/> + 1 of them.</param>
    /// <param name="liveQuery">The composable query over <c>fn_ActiveSubjects</c>: the caller's subjects that are alive now.</param>
    /// <param name="subjectIdsJson">The caller's subject ids as JSON, for the row-by-row function.</param>
    /// <param name="permissionId">The permission's id.</param>
    /// <param name="resourceTypeId">The permission's resource type, or null when it applies to every type.</param>
    /// <param name="typeSeq">The compact key of that type, for tables with scope columns.</param>
    /// <param name="scoped">Whether <typeparamref name="T"/>'s table carries the scope columns.</param>
    /// <param name="levels">The number of ancestor columns the model declares (depth + 1).</param>
    public static Expression<Func<T, bool>> Build<T>(
        ISqlOSFgaDbContext context,
        IReadOnlyList<SqlOSFgaAccessRoot> roots,
        IQueryable<SqlOSFgaActiveSubject> liveQuery,
        string subjectIdsJson,
        string permissionId,
        string? resourceTypeId,
        int? typeSeq,
        bool scoped,
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
                $"A grant sits at level {deepest} of the resource tree, but the EF model declares {levels} levels. "
                + "Configure the same MaxResourceHierarchyDepth in SqlOSFgaOptions and in ApplySqlOSFgaModel.");
        }

        Expression body;
        if (scoped)
        {
            body = Levels(entity, SqlOSFgaLineage.ScopeAncestorColumn, SqlOSFgaLineage.ScopeReachColumn, roots);
            if (typeSeq is { } seq)
            {
                body = Expression.AndAlso(
                    Expression.Equal(Property<int?>(entity, SqlOSFgaLineage.ScopeTypeSeqColumn), Parameter<int?>(seq)),
                    body);
            }
        }
        else
        {
            // EXISTS (SELECT 1 FROM resources r WHERE r.Id = entity.ResourceId AND type AND levels)
            var resource = Expression.Parameter(typeof(SqlOSFgaResource), "r");
            Expression inner = Expression.Equal(
                Expression.Property(resource, nameof(SqlOSFgaResource.Id)),
                Expression.Property(entity, nameof(IHasResourceId.ResourceId)));
            if (resourceTypeId is not null)
            {
                inner = Expression.AndAlso(
                    inner,
                    Expression.Equal(Expression.Property(resource, nameof(SqlOSFgaResource.ResourceTypeId)), Parameter<string>(resourceTypeId)));
            }

            inner = Expression.AndAlso(inner, Levels(resource, SqlOSFgaLineage.AncestorColumn, SqlOSFgaLineage.ReachColumn, roots));
            body = Expression.Call(
                QueryableAny.MakeGenericMethod(typeof(SqlOSFgaResource)),
                context.Set<SqlOSFgaResource>().AsQueryable().Expression,
                Expression.Lambda<Func<SqlOSFgaResource, bool>>(inner, resource));
        }

        // The grants were read when the filter was built; the caller's own liveness is still checked when the
        // query runs, once per query (an uncorrelated EXISTS the engine evaluates as a one-time filter).
        var alive = Expression.Call(QueryableAnyNoPredicate.MakeGenericMethod(typeof(SqlOSFgaActiveSubject)), liveQuery.Expression);
        return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(alive, body), entity);
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

    /// <summary>The disjunction over levels: (ancestor at level ℓ matches a root at ℓ AND reach ≤ ℓ) OR ..., one term per level that holds a root.</summary>
    private static Expression Levels(Expression row, Func<int, string> ancestorColumn, string reachColumn, IReadOnlyList<SqlOSFgaAccessRoot> roots)
    {
        Expression? disjunction = null;
        var reach = Property<short?>(row, reachColumn);
        foreach (var group in roots.GroupBy(r => (int)r.Depth).OrderBy(g => g.Key))
        {
            var ancestor = Property<long?>(row, ancestorColumn(group.Key));
            var seqs = group.Select(r => (long?)r.ResourceSeq).Distinct().ToArray();
            Expression matches = seqs.Length == 1
                ? Expression.Equal(ancestor, Parameter<long?>(seqs[0]))
                : Expression.Call(EnumerableContains.MakeGenericMethod(typeof(long?)), Parameter<long?[]>(seqs), ancestor);
            disjunction = Or(disjunction, Expression.AndAlso(matches, ReachCovers(reach, group.Key)));
        }

        return disjunction ?? Expression.Constant(false);
    }

    private static Expression ReachCovers(Expression reach, int level)
        => Expression.LessThanOrEqual(reach, Expression.Constant((short?)level, typeof(short?)));

    private static Expression Or(Expression? left, Expression right)
        => left is null ? right : Expression.OrElse(left, right);

    private static Expression Property<TValue>(Expression row, string name)
        => Expression.Call(PropertyMethod.MakeGenericMethod(typeof(TValue)), row, Expression.Constant(name));

    /// <summary>A value sent as a query parameter, so the query's shape (and its compiled plan) is shared across callers.</summary>
    private static Expression Parameter<TValue>(TValue value)
        => Expression.Call(ParameterMethod.MakeGenericMethod(typeof(TValue)), Expression.Constant(value, typeof(TValue)));
}
