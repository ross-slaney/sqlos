using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SqlOS.Domain;
using SqlOS.Fga;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using static SqlOS.Fga.SqlOSFgaWrites;

namespace SqlOS.Extensions;

public static partial class SqlOSErgonomicsExtensions
{
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
    public static Task<SqlOSFgaGrant> GrantRoleAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        ISqlOSResourceEntity resource,
        string roleKeyOrId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return context.GrantRoleAsync(subjectId, resource.ResourceId, roleKeyOrId, new SqlOSFgaGrantOptions(), cancellationToken);
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
    public static Task<SqlOSFgaGrant> GrantRoleAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string resourceId,
        string roleKeyOrId,
        CancellationToken cancellationToken = default)
        => context.GrantRoleAsync(subjectId, resourceId, roleKeyOrId, new SqlOSFgaGrantOptions(), cancellationToken);

    /// <summary>
    /// Idempotently grants a role to an existing subject on an FGA resource, in effect during a
    /// window and with a description: the one validated way host code creates a grant.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The identifier of an explicitly provisioned subject.</param>
    /// <param name="resourceId">The target resource identifier.</param>
    /// <param name="roleKeyOrId">The key or identifier of an existing FGA role.</param>
    /// <param name="options">The grant's window (both bounds included, an absent bound is open) and description.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>
    /// The tracked grant: the existing one when the subject already has the role on the resource for
    /// the same window (its description updated when one is given), or a new one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or the subject, role, or target resource cannot be resolved.
    /// </exception>
    /// <remarks>
    /// A grant is unique per subject, role, resource and window, so a different window adds another
    /// grant. This method does not provision subjects or save changes.
    /// </remarks>
    public static async Task<SqlOSFgaGrant> GrantRoleAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string resourceId,
        string roleKeyOrId,
        SqlOSFgaGrantOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var subject = await FindRequiredSubjectAsync(context, normalizedSubjectId, cancellationToken);
        await RequireResourceAsync(context, new SqlOSFgaResourceTree(context), normalizedResourceId, cancellationToken);
        var role = await ResolveRoleAsync(context, roleKeyOrId, cancellationToken);
        var window = TimeWindow.Between(options.EffectiveFrom, options.EffectiveTo);
        var now = Now(context);

        var grant = await SqlOSFgaGrants.FindEquivalentAsync((DbContext)context, subject.Id, role.Id, normalizedResourceId, window, cancellationToken);
        if (grant == null)
        {
            grant = SqlOSFgaGrant.Create(
                GrantId(subject.Id, normalizedResourceId, role.Id, window),
                subject,
                role,
                normalizedResourceId,
                window,
                NormalizeOptional(options.Description),
                new GrantAuthority(FgaActor.Host),
                now);
            context.Set<SqlOSFgaGrant>().Add(grant);
            return grant;
        }

        grant.Describe(options.Description != null ? NormalizeOptional(options.Description) : grant.Description, now);
        return grant;
    }

    /// <summary>
    /// Removes a subject's role grant from an entity-backed FGA resource when the grant exists.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The identifier of an existing subject.</param>
    /// <param name="resource">The protected application entity that identifies the target resource.</param>
    /// <param name="roleKeyOrId">The key or identifier of an existing FGA role.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>A task that completes when the matching grants have been marked for deletion, if present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="resource"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or the subject, role, or target resource cannot be resolved.
    /// </exception>
    public static Task RevokeRoleAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        ISqlOSResourceEntity resource,
        string roleKeyOrId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return context.RevokeRoleAsync(subjectId, resource.ResourceId, roleKeyOrId, cancellationToken);
    }

    /// <summary>
    /// Removes a subject's role grants from an FGA resource, whatever their windows.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The identifier of an existing subject.</param>
    /// <param name="resourceId">The target resource identifier.</param>
    /// <param name="roleKeyOrId">The key or identifier of an existing FGA role.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>A task that completes when the matching grants have been marked for deletion, if present.</returns>
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
        var role = await ResolveRoleAsync(context, roleKeyOrId, cancellationToken);

        var dbContext = (DbContext)context;
        var stored = await context.Set<SqlOSFgaGrant>()
            .Where(grant => grant.SubjectId == normalizedSubjectId && grant.ResourceId == normalizedResourceId && grant.RoleId == role.Id)
            .ToListAsync(cancellationToken);
        var grants = context.Set<SqlOSFgaGrant>().Local
            .Where(grant => grant.SubjectId == normalizedSubjectId && grant.ResourceId == normalizedResourceId && grant.RoleId == role.Id)
            .Union(stored)
            .Where(grant => dbContext.Entry(grant).State != EntityState.Deleted)
            .ToList();
        SqlOSFgaGrants.Revoke(dbContext, grants, FgaActor.Host);
    }

    private static async Task<SqlOSFgaSubject> FindRequiredSubjectAsync(
        ISqlOSFgaDbContext context,
        string subjectId,
        CancellationToken cancellationToken)
        => await FindSubjectAsync(context, subjectId, cancellationToken)
            ?? throw new InvalidOperationException($"FGA subject '{subjectId}' was not found. Provision the subject explicitly before granting roles.");

    private static async Task<SqlOSFgaRole> ResolveRoleAsync(
        ISqlOSFgaDbContext context,
        string roleKeyOrId,
        CancellationToken cancellationToken)
    {
        var normalizedRole = RequireValue(roleKeyOrId, nameof(roleKeyOrId));
        var roles = context.Set<SqlOSFgaRole>();
        return roles.Local.FirstOrDefault(x => x.Key == normalizedRole || x.Id == normalizedRole)
            ?? await roles.FirstOrDefaultAsync(x => x.Key == normalizedRole || x.Id == normalizedRole, cancellationToken)
            ?? throw new InvalidOperationException($"FGA role '{normalizedRole}' was not found.");
    }

    /// <summary>
    /// The grant's deterministic identifier: 7.x's for a grant without a window, the window added
    /// otherwise, so replays converge on one grant.
    /// </summary>
    private static string GrantId(string subjectId, string resourceId, string roleId, TimeWindow window)
    {
        var key = $"{subjectId}\n{resourceId}\n{roleId}";
        if (window != TimeWindow.Always)
        {
            key += $"\n{window.EffectiveFrom?.Ticks}\n{window.EffectiveTo?.Ticks}";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"grant::{Convert.ToHexString(bytes).ToLowerInvariant()[..32]}";
    }
}

/// <summary>The window and description of a grant <c>GrantRoleAsync</c> creates.</summary>
public sealed record SqlOSFgaGrantOptions
{
    /// <summary>The first instant the grant is in effect, or <see langword="null"/> for no start.</summary>
    public DateTime? EffectiveFrom { get; init; }

    /// <summary>The last instant the grant is in effect, or <see langword="null"/> for no end.</summary>
    public DateTime? EffectiveTo { get; init; }

    /// <summary>An optional description of why the subject holds the role.</summary>
    public string? Description { get; init; }
}
