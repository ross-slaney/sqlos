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
    /// Keep the expression in the <see cref="IQueryable{T}"/> pipeline so EF Core can translate
    /// it to the configured SqlOS authorization table-valued function.
    /// </remarks>
    Task<Expression<Func<T, bool>>> GetAuthorizationFilterAsync<T>(
        string subjectId,
        string permissionKey) where T : IHasResourceId;
}
