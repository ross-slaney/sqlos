using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Paging;

/// <summary>
/// The authorization predicate of a page: read 1's visibility flag and the load's final check.
/// </summary>
internal static class SqlOSFgaVisibility
{
    private static readonly MethodInfo AnyMethod = typeof(Queryable).GetMethods()
        .Single(method => method.Name == nameof(Queryable.Any) && method.GetParameters().Length == 1)
        .MakeGenericMethod(typeof(SqlOSFgaAccessibleResource));

    /// <summary>
    /// <c>e =&gt; context.IsResourceAccessible(e.ResourceId, subjectIds, permissionId).Any()</c>:
    /// <c>fn_IsResourceAccessible</c> lets the caller reach the row's resource. The subject ids and
    /// the permission id are query parameters.
    /// </summary>
    public static Expression<Func<T, bool>> Predicate<T>(DbContext context, string subjectIdsJson, string permissionId)
        where T : IHasResourceId
    {
        // The concrete context's method: EF Core maps the function on the DbContext type, not the interface.
        var contextType = context.GetType();
        var function = contextType.GetMethod(
                nameof(ISqlOSFgaDbContext.IsResourceAccessible),
                [typeof(string), typeof(string), typeof(string)])
            ?? throw new InvalidOperationException($"{contextType.Name} does not declare IsResourceAccessible.");

        // The context is captured like a closure variable, as in C# query code: EF Core makes it a
        // query parameter. A constant would make every context instance a new compiled query.
        var entity = Expression.Parameter(typeof(T), "e");
        var accessible = Expression.Call(
            SqlOSFgaPageCursor.QueryParameter(context, contextType),
            function,
            Expression.Property(entity, nameof(IHasResourceId.ResourceId)),
            SqlOSFgaPageCursor.QueryParameter(subjectIdsJson, typeof(string)),
            SqlOSFgaPageCursor.QueryParameter(permissionId, typeof(string)));
        return Expression.Lambda<Func<T, bool>>(Expression.Call(AnyMethod, accessible), entity);
    }
}
