using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Services;
using SqlOS.Fga;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Extensions;

/// <summary>
/// Convenience extensions for the common SqlOS application path.
/// </summary>
public static class SqlOSErgonomicsExtensions
{
    /// <summary>
    /// Requires every endpoint in a route group to present a valid SqlOS bearer access token
    /// containing the specified audience.
    /// </summary>
    /// <param name="group">The Minimal API route group to protect.</param>
    /// <param name="expectedAudience">The exact audience required in a valid access token.</param>
    /// <returns>The same <paramref name="group"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="expectedAudience"/> is empty or contains only whitespace.</exception>
    public static RouteGroupBuilder RequireSqlOSAccessToken(this RouteGroupBuilder group, string expectedAudience)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group.RequireSqlOSAccessToken(options => options.ExpectedAudience = expectedAudience);
    }

    /// <summary>
    /// Requires endpoints in a route group to satisfy the configured SqlOS bearer access-token validation policy.
    /// </summary>
    /// <param name="group">The Minimal API route group to protect.</param>
    /// <param name="configure">A callback that configures token validation.</param>
    /// <returns>The same <paramref name="group"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The configured <see cref="SqlOSAccessTokenValidationOptions.ExpectedAudience"/> is empty.
    /// </exception>
    /// <remarks>
    /// Successful validation sets <see cref="HttpContext.User"/> and stores the validated token for
    /// <see cref="SqlOSAccessTokenValidationExtensions.GetSqlOSValidatedToken(HttpContext)"/>.
    /// Failed validation returns HTTP 401 with a Bearer challenge without invoking the endpoint.
    /// </remarks>
    public static RouteGroupBuilder RequireSqlOSAccessToken(
        this RouteGroupBuilder group,
        Action<SqlOSAccessTokenValidationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SqlOSAccessTokenValidationOptions();
        configure(options);
        SqlOSAccessTokenValidationMiddleware.ValidateOptions(options);

        group.AddEndpointFilter((context, next) =>
            SqlOSAccessTokenEndpointFilter.InvokeAsync(context, next, options));

        return group;
    }

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
    /// Creates a new manually managed FGA resource with a generated identifier and adds it to the context.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="resourceTypeId">The identifier of an existing FGA resource type.</param>
    /// <param name="name">The resource display name.</param>
    /// <param name="parentResourceId">The optional identifier of the resource's parent.</param>
    /// <param name="description">An optional resource description.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The new tracked resource. Call <see cref="ISqlOSFgaDbContext.SaveChangesAsync(CancellationToken)"/> to persist it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, the resource type or parent does not exist, or the requested hierarchy is invalid.
    /// </exception>
    public static Task<SqlOSFgaResource> CreateResourceAsync(
        this ISqlOSFgaDbContext context,
        string resourceTypeId,
        string name,
        string? parentResourceId = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedResourceTypeId = RequireValue(resourceTypeId, nameof(resourceTypeId));
        return context.CreateResourceWithIdAsync(
            $"{normalizedResourceTypeId}::{Guid.NewGuid():N}",
            normalizedResourceTypeId,
            name,
            parentResourceId,
            description,
            cancellationToken);
    }

    /// <summary>
    /// Creates a new manually managed FGA resource with an explicit identifier and adds it to the context.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="resourceId">The stable identifier for the new resource.</param>
    /// <param name="resourceTypeId">The identifier of an existing FGA resource type.</param>
    /// <param name="name">The resource display name.</param>
    /// <param name="parentResourceId">The optional identifier of the resource's parent.</param>
    /// <param name="description">An optional resource description.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The new tracked resource. Call <see cref="ISqlOSFgaDbContext.SaveChangesAsync(CancellationToken)"/> to persist it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A resource with the same identifier already exists, a required value is empty, the resource type
    /// or parent does not exist, or the requested hierarchy is invalid.
    /// </exception>
    public static async Task<SqlOSFgaResource> CreateResourceWithIdAsync(
        this ISqlOSFgaDbContext context,
        string resourceId,
        string resourceTypeId,
        string name,
        string? parentResourceId = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        if (await FindResourceAsync(context, normalizedResourceId, cancellationToken) != null)
        {
            throw new InvalidOperationException($"FGA resource '{normalizedResourceId}' already exists.");
        }

        var normalizedParentId = NormalizeOptional(parentResourceId);
        var normalizedResourceTypeId = RequireValue(resourceTypeId, nameof(resourceTypeId));
        EnsureResourceParentIsNotSelf(normalizedResourceId, normalizedParentId);
        await FindRequiredResourceTypeAsync(context, normalizedResourceTypeId, cancellationToken);
        if (normalizedParentId != null)
        {
            await FindRequiredResourceOrPendingEntityAsync(context, normalizedParentId, cancellationToken);
        }

        await EnsureParentChainDoesNotCreateCycleAsync(context, normalizedResourceId, normalizedParentId, cancellationToken);

        var resource = CreateResource(
            normalizedResourceId,
            normalizedParentId,
            name,
            normalizedResourceTypeId,
            description);
        context.Set<SqlOSFgaResource>().Add(resource);
        return resource;
    }

    /// <summary>
    /// Idempotently creates or updates a manually managed FGA resource with an explicit identifier.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="resourceId">The stable resource identifier.</param>
    /// <param name="resourceTypeId">The identifier of an existing FGA resource type.</param>
    /// <param name="name">The resource display name.</param>
    /// <param name="parentResourceId">
    /// The optional parent identifier. For an existing resource, <see langword="null"/> preserves its current parent.
    /// </param>
    /// <param name="description">
    /// The optional description. For an existing resource, <see langword="null"/> preserves its current description.
    /// </param>
    /// <param name="isActive">
    /// The optional active state. For an existing resource, <see langword="null"/> preserves its current state.
    /// </param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The added or updated tracked resource. Call <see cref="ISqlOSFgaDbContext.SaveChangesAsync(CancellationToken)"/> to persist it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, the resource type or parent does not exist, or the requested hierarchy is invalid.
    /// </exception>
    public static async Task<SqlOSFgaResource> ProvisionResourceWithIdAsync(
        this ISqlOSFgaDbContext context,
        string resourceId,
        string resourceTypeId,
        string name,
        string? parentResourceId = null,
        string? description = null,
        bool? isActive = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var parentWasProvided = parentResourceId != null;
        var normalizedParentId = NormalizeOptional(parentResourceId);
        var normalizedResourceTypeId = RequireValue(resourceTypeId, nameof(resourceTypeId));
        await FindRequiredResourceTypeAsync(context, normalizedResourceTypeId, cancellationToken);
        var resource = await FindResourceAsync(context, normalizedResourceId, cancellationToken);
        var effectiveParentId = resource == null || parentWasProvided
            ? normalizedParentId
            : resource.ParentId;

        EnsureResourceParentIsNotSelf(normalizedResourceId, effectiveParentId);
        if (effectiveParentId != null)
        {
            await FindRequiredResourceOrPendingEntityAsync(context, effectiveParentId, cancellationToken);
        }

        await EnsureParentChainDoesNotCreateCycleAsync(context, normalizedResourceId, effectiveParentId, cancellationToken);

        if (resource == null)
        {
            resource = CreateResource(
                normalizedResourceId,
                effectiveParentId,
                name,
                normalizedResourceTypeId,
                description);
            resource.IsActive = isActive ?? true;
            context.Set<SqlOSFgaResource>().Add(resource);
            return resource;
        }

        resource.ParentId = effectiveParentId;
        resource.Name = RequireValue(name, nameof(name));
        resource.ResourceTypeId = normalizedResourceTypeId;
        if (description != null)
        {
            resource.Description = NormalizeOptional(description);
        }

        if (isActive.HasValue)
        {
            resource.IsActive = isActive.Value;
        }

        resource.UpdatedAt = DateTime.UtcNow;
        return resource;
    }

    /// <summary>
    /// Marks a manually managed FGA resource and all of its direct grants for deletion.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="resourceId">The identifier of the resource to delete.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>A task that completes when the resource and grants have been marked for deletion.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The resource does not exist, has child resources, or <paramref name="resourceId"/> is empty.
    /// </exception>
    /// <remarks>
    /// Child resources are not deleted or reparented. Call
    /// <see cref="ISqlOSFgaDbContext.SaveChangesAsync(CancellationToken)"/> to persist the deletion.
    /// </remarks>
    public static async Task DeleteResourceAsync(
        this ISqlOSFgaDbContext context,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var resource = await FindRequiredResourceAsync(context, normalizedResourceId, cancellationToken);
        await EnsureResourceHasNoChildrenAsync(context, normalizedResourceId, cancellationToken);

        var grants = await context.Set<SqlOSFgaGrant>()
            .Where(grant => grant.ResourceId == normalizedResourceId)
            .ToListAsync(cancellationToken);
        context.Set<SqlOSFgaGrant>().RemoveRange(grants);
        context.Set<SqlOSFgaResource>().Remove(resource);
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
        await FindRequiredResourceOrPendingEntityAsync(context, normalizedResourceId, cancellationToken);
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
        await FindRequiredResourceOrPendingEntityAsync(context, normalizedResourceId, cancellationToken);
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

    private static SqlOSFgaResource CreateResource(
        string resourceId,
        string? parentResourceId,
        string name,
        string resourceTypeId,
        string? description)
    {
        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var normalizedParentId = NormalizeOptional(parentResourceId);
        EnsureResourceParentIsNotSelf(normalizedResourceId, normalizedParentId);

        return new SqlOSFgaResource
        {
            Id = normalizedResourceId,
            ParentId = normalizedParentId,
            Name = RequireValue(name, nameof(name)),
            ResourceTypeId = RequireValue(resourceTypeId, nameof(resourceTypeId)),
            Description = NormalizeOptional(description),
            IsActive = true
        };
    }

    private static async Task EnsureParentChainDoesNotCreateCycleAsync(
        ISqlOSFgaDbContext context,
        string resourceId,
        string? parentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parentId))
        {
            return;
        }

        EnsureResourceParentIsNotSelf(resourceId, parentId);

        var visited = new HashSet<string>(StringComparer.Ordinal) { resourceId };
        var currentId = parentId;
        var depth = 1;
        var maxDepth = SqlOSFgaHierarchyDepth.Resolve(context);

        while (!string.IsNullOrWhiteSpace(currentId))
        {
            if (!visited.Add(currentId))
            {
                throw new InvalidOperationException("FGA resource hierarchy contains a cycle.");
            }

            if (depth > maxDepth)
            {
                throw new InvalidOperationException($"FGA resource hierarchy exceeds the configured maximum depth of {maxDepth}.");
            }

            var localParent = context.Set<SqlOSFgaResource>().Local.FirstOrDefault(r => r.Id == currentId);
            currentId = localParent != null
                ? localParent.ParentId
                : await context.Set<SqlOSFgaResource>()
                    .AsNoTracking()
                    .Where(r => r.Id == currentId)
                    .Select(r => r.ParentId)
                    .FirstOrDefaultAsync(cancellationToken);
            depth++;
        }
    }

    private static void EnsureResourceParentIsNotSelf(string resourceId, string? parentId)
    {
        if (string.Equals(resourceId, parentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FGA resource parent cannot be the resource itself.");
        }
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

    private static async Task<SqlOSFgaResource?> FindResourceAsync(
        ISqlOSFgaDbContext context,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var resources = context.Set<SqlOSFgaResource>();
        return resources.Local.FirstOrDefault(x => x.Id == resourceId)
            ?? await resources.FirstOrDefaultAsync(x => x.Id == resourceId, cancellationToken);
    }

    private static async Task<SqlOSFgaResource> FindRequiredResourceAsync(
        ISqlOSFgaDbContext context,
        string resourceId,
        CancellationToken cancellationToken)
        => await FindResourceAsync(context, resourceId, cancellationToken)
            ?? throw new InvalidOperationException($"FGA resource '{resourceId}' was not found.");

    private static async Task FindRequiredResourceOrPendingEntityAsync(
        ISqlOSFgaDbContext context,
        string resourceId,
        CancellationToken cancellationToken)
    {
        if (await FindResourceAsync(context, resourceId, cancellationToken) != null)
        {
            return;
        }

        if (context is DbContext dbContext
            && IsSqlOSResourceEntitySyncContext(dbContext)
            && dbContext.ChangeTracker.Entries().Any(entry =>
                entry.Entity is ISqlOSResourceEntity resourceEntity
                && entry.State is EntityState.Added or EntityState.Modified
                && string.Equals(NormalizeOptional(resourceEntity.ResourceId), resourceId, StringComparison.Ordinal)))
        {
            return;
        }

        throw new InvalidOperationException($"FGA resource '{resourceId}' was not found.");
    }

    private static bool IsSqlOSResourceEntitySyncContext(DbContext context)
    {
        for (var type = context.GetType(); type != null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SqlOSDbContext<>))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task EnsureResourceHasNoChildrenAsync(
        ISqlOSFgaDbContext context,
        string resourceId,
        CancellationToken cancellationToken)
    {
        if (context is DbContext dbContext)
        {
            var hasLocalChild = dbContext.ChangeTracker
                .Entries<SqlOSFgaResource>()
                .Any(entry => entry.Entity.ParentId == resourceId && entry.State != EntityState.Deleted);
            if (hasLocalChild)
            {
                throw new InvalidOperationException($"FGA resource '{resourceId}' has child resources. Delete or reparent child resources before deleting this resource.");
            }
        }

        var hasChild = await context.Set<SqlOSFgaResource>()
            .AsNoTracking()
            .AnyAsync(resource => resource.ParentId == resourceId, cancellationToken);
        if (hasChild)
        {
            throw new InvalidOperationException($"FGA resource '{resourceId}' has child resources. Delete or reparent child resources before deleting this resource.");
        }
    }

    private static async Task<SqlOSFgaResourceType?> FindResourceTypeAsync(
        ISqlOSFgaDbContext context,
        string resourceTypeId,
        CancellationToken cancellationToken)
    {
        var resourceTypes = context.Set<SqlOSFgaResourceType>();
        return resourceTypes.Local.FirstOrDefault(x => x.Id == resourceTypeId)
            ?? await resourceTypes.FirstOrDefaultAsync(x => x.Id == resourceTypeId, cancellationToken);
    }

    private static async Task<SqlOSFgaResourceType> FindRequiredResourceTypeAsync(
        ISqlOSFgaDbContext context,
        string resourceTypeId,
        CancellationToken cancellationToken)
        => await FindResourceTypeAsync(context, resourceTypeId, cancellationToken)
            ?? throw new InvalidOperationException($"FGA resource type '{resourceTypeId}' was not found. Seed or create the resource type before provisioning resources.");

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

    private static string RequireValue(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{paramName} is required.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class SqlOSAccessTokenEndpointFilter
{
    public static async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        SqlOSAccessTokenValidationOptions options)
    {
        var httpContext = context.HttpContext;
        if (options.ShouldValidate is { } shouldValidate && !shouldValidate(httpContext))
        {
            return await next(context);
        }

        var authService = httpContext.RequestServices.GetRequiredService<SqlOSAuthService>();
        var authorization = httpContext.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(options, "A bearer access token is required.");
        }

        var rawToken = authorization["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return Unauthorized(options, "A bearer access token is required.");
        }

        var validated = await authService.ValidateAccessTokenAsync(
            rawToken,
            options.ExpectedAudience,
            httpContext.RequestAborted);

        if (validated == null)
        {
            return Unauthorized(options, "The bearer access token is invalid, expired, revoked, or was not minted for this resource.");
        }

        if (SqlOSScopeRequirementPolicy.DescribeUnsatisfied(options.RequiredScopes, validated.Scope) is { } scopeFailure)
        {
            return new SqlOSTokenChallengeResult(options, "insufficient_scope", scopeFailure);
        }

        httpContext.User = validated.Principal;
        httpContext.Items[SqlOSAccessTokenValidationExtensions.ValidatedTokenItemKey] = validated;
        return await next(context);
    }

    private static IResult Unauthorized(SqlOSAccessTokenValidationOptions options, string description)
        => new SqlOSTokenChallengeResult(options, "invalid_token", description);

    private sealed class SqlOSTokenChallengeResult : IResult
    {
        private readonly SqlOSAccessTokenValidationOptions _options;
        private readonly string _error;
        private readonly string _description;

        public SqlOSTokenChallengeResult(SqlOSAccessTokenValidationOptions options, string error, string description)
        {
            _options = options;
            _error = error;
            _description = description;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            // RFC 6750 §3.1: invalid_token answers 401; insufficient_scope answers 403.
            httpContext.Response.StatusCode = string.Equals(_error, "insufficient_scope", StringComparison.Ordinal)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            httpContext.Response.Headers.WWWAuthenticate = BuildChallenge();
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = _error,
                error_description = _description
            });
        }

        private string BuildChallenge()
        {
            var parts = new List<string>
            {
                $"Bearer realm=\"{EscapeHeaderValue(_options.Realm)}\"",
                $"error=\"{_error}\"",
                $"error_description=\"{EscapeHeaderValue(_description)}\""
            };

            if (string.Equals(_error, "insufficient_scope", StringComparison.Ordinal) && _options.RequiredScopes.Count > 0)
            {
                parts.Add($"scope=\"{EscapeHeaderValue(string.Join(' ', _options.RequiredScopes))}\"");
            }

            if (!string.IsNullOrWhiteSpace(_options.ResourceMetadataUrl))
            {
                parts.Add($"resource_metadata=\"{EscapeHeaderValue(_options.ResourceMetadataUrl!)}\"");
            }

            return string.Join(", ", parts);
        }

        private static string EscapeHeaderValue(string value)
            => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
