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
    /// Rebuilds the entries of every page order declared in the context's model from the
    /// application tables and the resource graph.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the rebuild.</param>
    /// <returns>A task that completes when every page order has been rebuilt.</returns>
    /// <remarks>
    /// <para>
    /// For each order: delete all entries, then insert them in one statement, inside one
    /// transaction per order. The cost is the table's rows times the tree depth.
    /// </para>
    /// <para>
    /// <c>SqlOSDbContext&lt;TContext&gt;</c> keeps entries current for every write that goes through
    /// <c>SaveChanges</c>. Call this after writes that bypass EF Core (bulk loads, raw SQL,
    /// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>) and for recovery. Until then, rows those writes
    /// added are missing from pages and rows they changed page at their old position; a row the
    /// caller may not see is never returned, because every page row passes the filter once more.
    /// </para>
    /// </remarks>
    Task RebuildPageOrdersAsync(CancellationToken cancellationToken = default);
}
