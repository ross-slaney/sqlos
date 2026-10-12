using System.Linq.Expressions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Paging;

/// <summary>What a <c>BuildFilterAsync</c> filter carries: the context it was built on and the two values it passes to <c>fn_IsResourceAccessible</c>.</summary>
/// <param name="IsEmpty">The filter is <c>entity =&gt; false</c> (unknown subject or permission).</param>
/// <param name="Context">The context <c>BuildFilterAsync</c> ran on.</param>
/// <param name="SubjectIds">The JSON array of subject ids.</param>
/// <param name="PermissionId">The permission id.</param>
/// <param name="Lambda">The filter itself.</param>
internal sealed record SqlOSFgaFilter(
    bool IsEmpty,
    ISqlOSFgaDbContext? Context,
    string SubjectIds,
    string PermissionId,
    LambdaExpression Lambda);

/// <summary>
/// Reads a filter produced by <c>ISqlOSFgaAuthService.BuildFilterAsync</c>:
/// <c>entity =&gt; context.IsResourceAccessible(entity.ResourceId, parameters.SubjectIds, parameters.PermissionId).Any()</c>,
/// or <c>entity =&gt; false</c>. Any other expression is rejected.
/// </summary>
internal static class SqlOSFgaFilterReader
{
    public const string ForeignFilterMessage = "The filter must come from ISqlOSFgaAuthService.BuildFilterAsync.";

    public static SqlOSFgaFilter Read(LambdaExpression filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Parameters.Count != 1)
        {
            throw Foreign();
        }

        if (filter.Body is ConstantExpression { Value: false })
        {
            return new SqlOSFgaFilter(true, null, string.Empty, string.Empty, filter);
        }

        if (filter.Body is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Any), DeclaringType: var anyType } anyMethod,
                Arguments: [MethodCallExpression accessible]
            }
            || anyType != typeof(Queryable)
            || anyMethod.GetGenericArguments() is not [var resultType]
            || resultType != typeof(SqlOSFgaAccessibleResource))
        {
            throw Foreign();
        }

        if (accessible is not
            {
                Method.Name: nameof(ISqlOSFgaDbContext.IsResourceAccessible),
                Object: ConstantExpression { Value: ISqlOSFgaDbContext context },
                Arguments: [MemberExpression resourceId, MemberExpression subjectIds, MemberExpression permissionId]
            }
            || resourceId.Member.Name != nameof(IHasResourceId.ResourceId)
            || resourceId.Expression != filter.Parameters[0])
        {
            throw Foreign();
        }

        var parameters = ReadParameters(subjectIds, nameof(SqlOSFgaAuthorizationFilterParameters.SubjectIds));
        if (!ReferenceEquals(parameters, ReadParameters(permissionId, nameof(SqlOSFgaAuthorizationFilterParameters.PermissionId))))
        {
            throw Foreign();
        }

        return new SqlOSFgaFilter(false, context, parameters.SubjectIds, parameters.PermissionId, filter);
    }

    private static SqlOSFgaAuthorizationFilterParameters ReadParameters(MemberExpression field, string name)
        => field is
        {
            Member.Name: var memberName,
            Expression: ConstantExpression { Value: SqlOSFgaAuthorizationFilterParameters parameters }
        }
        && memberName == name
            ? parameters
            : throw Foreign();

    private static ArgumentException Foreign()
        => new(ForeignFilterMessage, "filter");
}
