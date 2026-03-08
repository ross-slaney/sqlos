using System.Linq.Expressions;
using Sqlzibar.Models;

namespace Sqlzibar.Interfaces;

/// <summary>
/// RBAC Authorization Service for hierarchical resource access.
/// Uses hierarchical permissions where access granted at a parent resource
/// is inherited by child resources.
/// </summary>
public interface ISqlzibarAuthService
{
    /// <summary>
    /// Check if a subject has access to a resource with a specific permission.
    /// Walks up the resource hierarchy to find grants.
    /// </summary>
    Task<SqlzibarAccessCheckResult> CheckAccessAsync(string subjectId, string permissionKey, string resourceId);

    /// <summary>
    /// Check if a subject has a specific permission capability at root level.
    /// </summary>
    Task<bool> HasCapabilityAsync(string subjectId, string permissionKey);

    /// <summary>
    /// Produce a detailed, structured trace of a resource access decision.
    /// </summary>
    Task<SqlzibarResourceAccessTrace> TraceResourceAccessAsync(string subjectId, string resourceId, string permissionKey);

    /// <summary>
    /// Get an expression filter for entities with a ResourceId property.
    /// The filter restricts results to only those resources the subject can access.
    /// </summary>
    Task<Expression<Func<T, bool>>> GetAuthorizationFilterAsync<T>(
        string subjectId,
        string permissionKey) where T : IHasResourceId;
}
