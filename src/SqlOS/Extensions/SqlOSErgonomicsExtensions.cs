using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SqlOS.Fga;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using static SqlOS.Fga.SqlOSFgaWrites;

namespace SqlOS.Extensions;

/// <summary>
/// Convenience extensions for the common SqlOS application path.
/// </summary>
public static partial class SqlOSErgonomicsExtensions
{
    /// <summary>
    /// Checks whether a subject has a permission on a resource and returns only the allow/deny decision.
    /// </summary>
    /// <param name="authService">The SqlOS FGA authorization service.</param>
    /// <param name="subjectId">The subject to authorize.</param>
    /// <param name="permissionKey">The permission key to require.</param>
    /// <param name="resourceId">The target resource identifier.</param>
    /// <returns><see langword="true"/> when access is allowed; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authService"/> is <see langword="null"/>.</exception>
    public static async Task<bool> Allows(
        this ISqlOSFgaAuthService authService,
        string subjectId,
        string permissionKey,
        string resourceId)
    {
        ArgumentNullException.ThrowIfNull(authService);

        var result = await authService.CheckAccessAsync(subjectId, permissionKey, resourceId);
        return result.Allowed;
    }

    /// <summary>
    /// Idempotently grants a role to an existing subject on an entity-backed FGA resource.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The identifier of an explicitly provisioned subject.</param>
    /// <param name="resource">The protected application entity that identifies the target resource.</param>
    /// <param name="roleKeyOrId">The key or identifier of an existing FGA role.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The existing or newly added tracked grant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="resource"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or the subject, role, or target resource cannot be resolved.
    /// </exception>
    /// <remarks>
    /// A newly tracked <paramref name="resource"/> is supported when the context derives from
    /// <c>SqlOSDbContext&lt;TContext&gt;</c>; the backing FGA resource is synchronized during save.
    /// This method does not provision subjects or save changes.
    /// </remarks>
    public static async Task<SqlOSFgaGrant> GrantRoleAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        ISqlOSResourceEntity resource,
        string roleKeyOrId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return await GrantRoleCoreAsync(
            context,
            subjectId,
            resource.ResourceId,
            roleKeyOrId,
            description: null,
            cancellationToken);
    }

    /// <summary>
    /// Idempotently grants a role to an existing subject on an FGA resource.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The identifier of an explicitly provisioned subject.</param>
    /// <param name="resourceId">The target resource identifier.</param>
    /// <param name="roleKeyOrId">The key or identifier of an existing FGA role.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The existing or newly added tracked grant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or the subject, role, or target resource cannot be resolved.
    /// </exception>
    /// <remarks>This method does not provision subjects or save changes.</remarks>
    public static async Task<SqlOSFgaGrant> GrantRoleAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string resourceId,
        string roleKeyOrId,
        CancellationToken cancellationToken = default)
        => await GrantRoleCoreAsync(
            context,
            subjectId,
            resourceId,
            roleKeyOrId,
            description: null,
            cancellationToken);

    /// <summary>
    /// Removes a subject's role grant from an entity-backed FGA resource when the grant exists.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The identifier of an existing subject.</param>
    /// <param name="resource">The protected application entity that identifies the target resource.</param>
    /// <param name="roleKeyOrId">The key or identifier of an existing FGA role.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>A task that completes when the matching grant has been marked for deletion, if present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="resource"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or the subject, role, or target resource cannot be resolved.
    /// </exception>
    public static async Task RevokeRoleAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        ISqlOSResourceEntity resource,
        string roleKeyOrId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        await RevokeRoleAsync(context, subjectId, resource.ResourceId, roleKeyOrId, cancellationToken);
    }

    /// <summary>
    /// Removes a subject's role grant from an FGA resource when the grant exists.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The identifier of an existing subject.</param>
    /// <param name="resourceId">The target resource identifier.</param>
    /// <param name="roleKeyOrId">The key or identifier of an existing FGA role.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>A task that completes when the matching grant has been marked for deletion, if present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or the subject, role, or target resource cannot be resolved.
    /// </exception>
    /// <remarks>When no matching grant exists, this method makes no change. It does not save changes.</remarks>
    public static async Task RevokeRoleAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string resourceId,
        string roleKeyOrId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        await FindRequiredSubjectAsync(context, normalizedSubjectId, cancellationToken);
        await RequireResourceAsync(context, new SqlOSFgaResourceTree(context), normalizedResourceId, cancellationToken);
        var roleId = await ResolveRoleIdAsync(context, roleKeyOrId, cancellationToken);

        var grant = await FindGrantAsync(
            context,
            normalizedSubjectId,
            normalizedResourceId,
            roleId,
            cancellationToken);
        if (grant != null)
        {
            context.Set<SqlOSFgaGrant>().Remove(grant);
        }
    }

    private static async Task<SqlOSFgaGrant> GrantRoleCoreAsync(
        ISqlOSFgaDbContext context,
        string subjectId,
        string resourceId,
        string roleKeyOrId,
        string? description,
        CancellationToken cancellationToken,
        bool roleAlreadyResolved = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        await FindRequiredSubjectAsync(context, normalizedSubjectId, cancellationToken);
        await RequireResourceAsync(context, new SqlOSFgaResourceTree(context), normalizedResourceId, cancellationToken);
        var roleId = roleAlreadyResolved
            ? RequireValue(roleKeyOrId, nameof(roleKeyOrId))
            : await ResolveRoleIdAsync(context, roleKeyOrId, cancellationToken);

        var grant = await FindGrantAsync(
            context,
            normalizedSubjectId,
            normalizedResourceId,
            roleId,
            cancellationToken);
        if (grant == null)
        {
            grant = new SqlOSFgaGrant
            {
                Id = BuildGrantId(normalizedSubjectId, normalizedResourceId, roleId),
                SubjectId = normalizedSubjectId,
                ResourceId = normalizedResourceId,
                RoleId = roleId,
                Description = NormalizeOptional(description)
            };
            context.Set<SqlOSFgaGrant>().Add(grant);
            return grant;
        }

        if (description != null)
        {
            grant.Description = NormalizeOptional(description);
        }

        grant.UpdatedAt = DateTime.UtcNow;
        return grant;
    }

    /// <summary>
    /// Idempotently provisions an FGA subject of type <c>user</c> and its typed user record.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The stable identifier used for authorization checks and grants.</param>
    /// <param name="displayName">The subject's display name.</param>
    /// <param name="email">An optional email address. When omitted for an existing user, the current value is preserved.</param>
    /// <param name="organizationId">An optional organization identifier. When omitted for an existing subject, the current value is preserved.</param>
    /// <param name="externalRef">An optional external identifier. New subjects default it to <paramref name="subjectId"/>.</param>
    /// <param name="isActive">An optional active state. When omitted for an existing user, the current value is preserved.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The added or updated tracked user record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or <paramref name="subjectId"/> already belongs to a different subject type.
    /// </exception>
    /// <remarks>This method tracks changes but does not save them.</remarks>
    public static async Task<SqlOSFgaUser> ProvisionUserSubjectAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string displayName,
        string? email = null,
        string? organizationId = null,
        string? externalRef = null,
        bool? isActive = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = await EnsureTypedSubjectAsync(
            context,
            subjectId,
            "user",
            displayName,
            organizationId,
            externalRef,
            cancellationToken);

        var users = context.Set<SqlOSFgaUser>();
        var user = users.Local.FirstOrDefault(x => x.SubjectId == subject.Id)
            ?? await users.FirstOrDefaultAsync(x => x.SubjectId == subject.Id, cancellationToken);

        if (user == null)
        {
            user = new SqlOSFgaUser
            {
                Id = BuildTypedSubjectRowId("usr", subject.Id),
                SubjectId = subject.Id,
                Email = NormalizeOptional(email),
                IsActive = isActive ?? true
            };
            users.Add(user);
            return user;
        }

        if (email != null)
        {
            user.Email = NormalizeOptional(email);
        }

        if (isActive.HasValue)
        {
            user.IsActive = isActive.Value;
        }

        user.UpdatedAt = DateTime.UtcNow;
        return user;
    }

    /// <summary>
    /// Idempotently provisions an FGA subject of type <c>agent</c> and its typed agent record.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The stable identifier used for authorization checks and grants.</param>
    /// <param name="displayName">The subject's display name.</param>
    /// <param name="agentType">An optional application-defined agent type. When omitted for an existing agent, the current value is preserved.</param>
    /// <param name="description">An optional description. When omitted for an existing agent, the current value is preserved.</param>
    /// <param name="organizationId">An optional organization identifier. When omitted for an existing subject, the current value is preserved.</param>
    /// <param name="externalRef">An optional external identifier. New subjects default it to <paramref name="subjectId"/>.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The added or updated tracked agent record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or <paramref name="subjectId"/> already belongs to a different subject type.
    /// </exception>
    /// <remarks>This method tracks changes but does not save them.</remarks>
    public static async Task<SqlOSFgaAgent> ProvisionAgentSubjectAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string displayName,
        string? agentType = null,
        string? description = null,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = await EnsureTypedSubjectAsync(
            context,
            subjectId,
            "agent",
            displayName,
            organizationId,
            externalRef,
            cancellationToken);

        var agents = context.Set<SqlOSFgaAgent>();
        var agent = agents.Local.FirstOrDefault(x => x.SubjectId == subject.Id)
            ?? await agents.FirstOrDefaultAsync(x => x.SubjectId == subject.Id, cancellationToken);

        if (agent == null)
        {
            agent = new SqlOSFgaAgent
            {
                Id = BuildTypedSubjectRowId("agt", subject.Id),
                SubjectId = subject.Id,
                AgentType = NormalizeOptional(agentType),
                Description = NormalizeOptional(description)
            };
            agents.Add(agent);
            return agent;
        }

        if (agentType != null)
        {
            agent.AgentType = NormalizeOptional(agentType);
        }

        if (description != null)
        {
            agent.Description = NormalizeOptional(description);
        }

        agent.UpdatedAt = DateTime.UtcNow;
        return agent;
    }

    /// <summary>
    /// Idempotently provisions an FGA subject of type <c>service_account</c> and its typed service-account record.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The stable identifier used for authorization checks and grants.</param>
    /// <param name="displayName">The subject's display name.</param>
    /// <param name="clientId">The service account's client identifier.</param>
    /// <param name="clientSecretHash">The application-provided hash of the service account secret.</param>
    /// <param name="description">An optional description. When omitted for an existing account, the current value is preserved.</param>
    /// <param name="expiresAt">An optional expiration time. When omitted for an existing account, the current value is preserved.</param>
    /// <param name="organizationId">An optional organization identifier. When omitted for an existing subject, the current value is preserved.</param>
    /// <param name="externalRef">An optional external identifier. New subjects default it to <paramref name="subjectId"/>.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The added or updated tracked service-account record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or <paramref name="subjectId"/> already belongs to a different subject type.
    /// </exception>
    /// <remarks>This method tracks changes but does not save them.</remarks>
    public static async Task<SqlOSFgaServiceAccount> ProvisionServiceAccountSubjectAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string displayName,
        string clientId,
        string clientSecretHash,
        string? description = null,
        DateTime? expiresAt = null,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = await EnsureTypedSubjectAsync(
            context,
            subjectId,
            "service_account",
            displayName,
            organizationId,
            externalRef,
            cancellationToken);

        var accounts = context.Set<SqlOSFgaServiceAccount>();
        var account = accounts.Local.FirstOrDefault(x => x.SubjectId == subject.Id)
            ?? await accounts.FirstOrDefaultAsync(x => x.SubjectId == subject.Id, cancellationToken);

        if (account == null)
        {
            account = new SqlOSFgaServiceAccount
            {
                Id = BuildTypedSubjectRowId("sa", subject.Id),
                SubjectId = subject.Id,
                ClientId = RequireValue(clientId, nameof(clientId)),
                ClientSecretHash = RequireValue(clientSecretHash, nameof(clientSecretHash)),
                Description = NormalizeOptional(description),
                ExpiresAt = expiresAt
            };
            accounts.Add(account);
            return account;
        }

        account.ClientId = RequireValue(clientId, nameof(clientId));
        account.ClientSecretHash = RequireValue(clientSecretHash, nameof(clientSecretHash));
        if (description != null)
        {
            account.Description = NormalizeOptional(description);
        }

        if (expiresAt.HasValue)
        {
            account.ExpiresAt = expiresAt;
        }

        account.UpdatedAt = DateTime.UtcNow;
        return account;
    }

    private static async Task<SqlOSFgaSubject> EnsureTypedSubjectAsync(
        ISqlOSFgaDbContext context,
        string subjectId,
        string subjectTypeId,
        string displayName,
        string? organizationId,
        string? externalRef,
        CancellationToken cancellationToken)
    {
        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var normalizedSubjectTypeId = RequireValue(subjectTypeId, nameof(subjectTypeId));
        var subject = await FindSubjectAsync(context, normalizedSubjectId, cancellationToken);

        if (subject == null)
        {
            subject = new SqlOSFgaSubject
            {
                Id = normalizedSubjectId,
                SubjectTypeId = normalizedSubjectTypeId,
                DisplayName = RequireValue(displayName, nameof(displayName)),
                OrganizationId = NormalizeOptional(organizationId),
                ExternalRef = NormalizeOptional(externalRef) ?? normalizedSubjectId
            };
            context.Set<SqlOSFgaSubject>().Add(subject);
            return subject;
        }

        if (!string.Equals(subject.SubjectTypeId, normalizedSubjectTypeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"FGA subject '{normalizedSubjectId}' already exists as type '{subject.SubjectTypeId}', not '{normalizedSubjectTypeId}'.");
        }

        subject.DisplayName = RequireValue(displayName, nameof(displayName));
        if (organizationId != null)
        {
            subject.OrganizationId = NormalizeOptional(organizationId);
        }

        if (externalRef != null)
        {
            subject.ExternalRef = NormalizeOptional(externalRef);
        }

        subject.UpdatedAt = DateTime.UtcNow;
        return subject;
    }

    private static async Task<SqlOSFgaSubject?> FindSubjectAsync(
        ISqlOSFgaDbContext context,
        string subjectId,
        CancellationToken cancellationToken)
    {
        var subjects = context.Set<SqlOSFgaSubject>();
        return subjects.Local.FirstOrDefault(x => x.Id == subjectId)
            ?? await subjects.FirstOrDefaultAsync(x => x.Id == subjectId, cancellationToken);
    }

    private static async Task<SqlOSFgaSubject> FindRequiredSubjectAsync(
        ISqlOSFgaDbContext context,
        string subjectId,
        CancellationToken cancellationToken)
        => await FindSubjectAsync(context, subjectId, cancellationToken)
            ?? throw new InvalidOperationException($"FGA subject '{subjectId}' was not found. Provision the subject explicitly before granting roles.");

    private static async Task<string> ResolveRoleIdAsync(
        ISqlOSFgaDbContext context,
        string roleKeyOrId,
        CancellationToken cancellationToken)
    {
        var normalizedRole = RequireValue(roleKeyOrId, nameof(roleKeyOrId));
        var roles = context.Set<SqlOSFgaRole>();
        var localRole = roles.Local.FirstOrDefault(x => x.Key == normalizedRole || x.Id == normalizedRole);
        if (localRole != null)
        {
            return localRole.Id;
        }

        var roleId = await roles
            .Where(x => x.Key == normalizedRole || x.Id == normalizedRole)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return roleId
            ?? throw new InvalidOperationException($"FGA role '{normalizedRole}' was not found.");
    }

    private static async Task<SqlOSFgaGrant?> FindGrantAsync(
        ISqlOSFgaDbContext context,
        string subjectId,
        string resourceId,
        string roleId,
        CancellationToken cancellationToken)
    {
        var grants = context.Set<SqlOSFgaGrant>();
        return grants.Local.FirstOrDefault(x =>
                x.SubjectId == subjectId
                && x.ResourceId == resourceId
                && x.RoleId == roleId)
            ?? await grants.FirstOrDefaultAsync(
                x => x.SubjectId == subjectId
                    && x.ResourceId == resourceId
                    && x.RoleId == roleId,
                cancellationToken);
    }

    private static string BuildGrantId(string subjectId, string resourceId, string roleId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{subjectId}\n{resourceId}\n{roleId}"));
        return $"grant::{Convert.ToHexString(bytes).ToLowerInvariant()[..32]}";
    }

    private static string BuildTypedSubjectRowId(string prefix, string subjectId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{prefix}\n{subjectId}"));
        return $"{prefix}::{Convert.ToHexString(bytes).ToLowerInvariant()[..32]}";
    }

}
