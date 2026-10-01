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

}
