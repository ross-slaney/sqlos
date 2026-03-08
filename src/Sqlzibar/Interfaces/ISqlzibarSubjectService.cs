using Sqlzibar.Models;

namespace Sqlzibar.Interfaces;

/// <summary>
/// Service for managing subjects, groups, and group membership.
/// </summary>
public interface ISqlzibarSubjectService
{
    /// <summary>
    /// Create a new subject.
    /// </summary>
    Task<SqlzibarSubject> CreateSubjectAsync(
        string displayName,
        string subjectTypeId,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new user with its associated subject.
    /// </summary>
    Task<SqlzibarUser> CreateUserAsync(
        string displayName,
        string? email = null,
        bool isActive = true,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new agent with its associated subject.
    /// </summary>
    Task<SqlzibarAgent> CreateAgentAsync(
        string displayName,
        string? agentType = null,
        string? description = null,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new service account with its associated subject.
    /// </summary>
    Task<SqlzibarServiceAccount> CreateServiceAccountAsync(
        string displayName,
        string clientId,
        string clientSecretHash,
        string? description = null,
        DateTime? expiresAt = null,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new user group with its associated subject.
    /// </summary>
    Task<SqlzibarUserGroup> CreateGroupAsync(
        string name,
        string? description = null,
        string? groupType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a subject to a group.
    /// Only subjects of type 'user', 'agent', or 'service_account' can be added — groups cannot contain other groups.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the subject is a group type.</exception>
    Task AddToGroupAsync(string subjectId, string userGroupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a subject from a group.
    /// </summary>
    Task RemoveFromGroupAsync(string subjectId, string userGroupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve all subject IDs for a given subject (the subject itself plus any groups it belongs to).
    /// Single-level lookup — no recursion needed since groups can't contain groups.
    /// </summary>
    Task<List<string>> ResolveSubjectIdsAsync(string subjectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all groups a subject belongs to.
    /// </summary>
    Task<List<SqlzibarUserGroup>> GetGroupsForSubjectAsync(string subjectId, CancellationToken cancellationToken = default);
}
