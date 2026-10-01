using System.Linq.Expressions;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Interfaces;

/// <summary>
/// Checks hierarchical SqlOS FGA permissions and creates EF Core authorization filters.
/// </summary>
/// <remarks>
/// Role grants on a parent resource are inherited by its descendants. A subject's group
/// memberships are included when SqlOS evaluates a decision.
/// </remarks>
public interface ISqlOSFgaAuthService
{
    /// <summary>
    /// Checks whether a subject has a permission on a resource or one of its ancestors.
    /// </summary>
    /// <param name="subjectId">The subject to authorize.</param>
    /// <param name="permissionKey">The permission key to require.</param>
    /// <param name="resourceId">The target resource identifier.</param>
    /// <returns>
    /// A result containing the allow/deny decision, evaluation trace, and an error description
    /// when the subject, permission, or resource cannot be resolved.
    /// </returns>
    Task<SqlOSFgaAccessCheckResult> CheckAccessAsync(string subjectId, string permissionKey, string resourceId);

    /// <summary>
    /// Checks whether a subject has a permission on the configured FGA root resource.
    /// </summary>
    /// <param name="subjectId">The subject to authorize.</param>
    /// <param name="permissionKey">The permission key to require at the root resource.</param>
    /// <returns><see langword="true"/> when the root-resource access check succeeds; otherwise, <see langword="false"/>.</returns>
    /// <remarks>This method does not search descendants for any resource on which the subject has the permission.</remarks>
    Task<bool> HasCapabilityAsync(string subjectId, string permissionKey);

    /// <summary>
    /// Produces a detailed, structured trace of a hierarchical resource access decision.
    /// </summary>
    /// <param name="subjectId">The subject to authorize.</param>
    /// <param name="resourceId">The target resource identifier.</param>
    /// <param name="permissionKey">The permission key to require.</param>
    /// <returns>The decision trace, including the resource path, subjects, grants, roles, and denial guidance.</returns>
    Task<SqlOSFgaResourceAccessTrace> TraceResourceAccessAsync(string subjectId, string resourceId, string permissionKey);

    /// <summary>
    /// Creates an EF Core-compatible expression that includes only entities whose resources
    /// the subject can access with a permission.
    /// </summary>
    /// <typeparam name="T">The entity type exposing the FGA resource identifier.</typeparam>
    /// <param name="subjectId">The subject whose accessible resources should be included.</param>
    /// <param name="permissionKey">The permission key required for each resource.</param>
    /// <returns>
    /// An expression for use with <see cref="Queryable.Where{TSource}(IQueryable{TSource},Expression{Func{TSource,bool}})"/>.
    /// The expression always evaluates to <see langword="false"/> when the subject or permission cannot be resolved.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Keep the expression in the <see cref="IQueryable{T}"/> pipeline so EF Core can translate
    /// it to the configured SqlOS authorization table-valued function.
    /// </para>
    /// <para>
    /// Filter lifetime: the subject's principal set (the subject plus its active group
    /// memberships) is resolved when this task is awaited. Build the filter once per request
    /// and compose it into that request's queries; do not cache it in statics or reuse it
    /// across requests, because grant and membership changes are not reflected in an
    /// already-built filter's principal set.
    /// </para>
    /// </remarks>
    Task<Expression<Func<T, bool>>> BuildFilterAsync<T>(
        string subjectId,
        string permissionKey) where T : IHasResourceId;

    /// <summary>
    /// Lists one page of the entities of one resource type that the subject may see, in resource creation
    /// order, reading only what the page returns.
    /// </summary>
    /// <typeparam name="T">The entity type exposing the FGA resource identifier.</typeparam>
    /// <param name="subjectId">The subject whose accessible entities should be listed.</param>
    /// <param name="permissionKey">The permission key required for each resource.</param>
    /// <param name="resourceTypeId">The FGA resource type of <typeparamref name="T"/>'s rows.</param>
    /// <param name="pageSize">The number of entities to return, from 1 to 1000.</param>
    /// <param name="cursor">The <see cref="SqlOSFgaVisiblePage{T}.NextCursor"/> of the previous page, or <see langword="null"/> for the first page.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The page. It is empty when the subject, permission, or resource type cannot be resolved.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="BuildFilterAsync{T}"/> checks each row the query scans, so a page costs about
    /// <c>pageSize / selectivity</c> row checks: a subject who may see one row in ten thousand pays ten
    /// thousand checks per page. This method instead starts from the subject's grants and reads each granted
    /// resource's descendants from the resource closure, an index kept by the resource table's triggers. A
    /// page reads at most <c>pageSize</c> index entries per grant, whatever the table's size or the subject's
    /// share of it.
    /// </para>
    /// <para>
    /// The order is fixed: the resource's creation order. For another order, or for further predicates,
    /// compose <see cref="BuildFilterAsync{T}"/> into your own query.
    /// </para>
    /// </remarks>
    Task<SqlOSFgaVisiblePage<T>> ListVisibleAsync<T>(
        string subjectId,
        string permissionKey,
        string resourceTypeId,
        int pageSize,
        string? cursor = null,
        CancellationToken cancellationToken = default) where T : class, IHasResourceId
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(ListVisibleAsync)}.");
}
