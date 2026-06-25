using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Extensions;

/// <summary>
/// Convenience extensions for the common SqlOS application path.
/// </summary>
public static class SqlOSErgonomicsExtensions
{
    private const int DefaultMaxResourceHierarchyDepth = 10;

    public static RouteGroupBuilder RequireSqlOSAccessToken(this RouteGroupBuilder group, string expectedAudience)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group.RequireSqlOSAccessToken(options => options.ExpectedAudience = expectedAudience);
    }

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

    public static SqlOSValidatedToken SqlOSToken(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.GetSqlOSValidatedToken()
            ?? throw new InvalidOperationException("No validated SqlOS access token is available. Protect the endpoint with RequireSqlOSAccessToken(...) or UseSqlOSAccessTokenValidation(...).");
    }

    public static string SqlOSUserId(this HttpContext context)
    {
        var token = context.SqlOSToken();
        if (string.IsNullOrWhiteSpace(token.UserId))
        {
            throw new InvalidOperationException("The validated SqlOS access token does not include a user id.");
        }

        return token.UserId;
    }

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

    public static SqlOSFgaResource AddSqlOSResource(
        this ISqlOSFgaDbContext context,
        string resourceId,
        string? parentResourceId,
        string name,
        string resourceTypeId,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resource = CreateResource(
            resourceId,
            parentResourceId,
            name,
            resourceTypeId,
            description);
        context.Set<SqlOSFgaResource>().Add(resource);
        return resource;
    }

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

    public static async Task<SqlOSFgaResource> EnsureSqlOSResourceAsync(
        this ISqlOSFgaDbContext context,
        string resourceId,
        string? parentResourceId,
        string name,
        string resourceTypeId,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var normalizedParentId = NormalizeOptional(parentResourceId);
        var normalizedResourceTypeId = RequireValue(resourceTypeId, nameof(resourceTypeId));
        EnsureResourceParentIsNotSelf(normalizedResourceId, normalizedParentId);
        await FindRequiredResourceTypeAsync(context, normalizedResourceTypeId, cancellationToken);
        if (normalizedParentId != null)
        {
            await FindRequiredResourceAsync(context, normalizedParentId, cancellationToken);
        }

        await EnsureParentChainDoesNotCreateCycleAsync(context, normalizedResourceId, normalizedParentId, cancellationToken);

        var resource = await FindResourceAsync(context, normalizedResourceId, cancellationToken);
        if (resource == null)
        {
            resource = CreateResource(
                normalizedResourceId,
                normalizedParentId,
                name,
                normalizedResourceTypeId,
                description);
            context.Set<SqlOSFgaResource>().Add(resource);
            return resource;
        }

        resource.ParentId = normalizedParentId;
        resource.Name = RequireValue(name, nameof(name));
        resource.ResourceTypeId = normalizedResourceTypeId;
        if (description != null)
        {
            resource.Description = NormalizeOptional(description);
        }

        resource.IsActive = true;
        resource.UpdatedAt = DateTime.UtcNow;
        return resource;
    }

    public static async Task<SqlOSFgaGrant> GrantSqlOSRoleAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string resourceId,
        string roleKeyOrId,
        string? description = null,
        CancellationToken cancellationToken = default)
        => await GrantRoleCoreAsync(
            context,
            subjectId,
            resourceId,
            roleKeyOrId,
            description,
            cancellationToken);

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

    public static async Task<SqlOSFgaGrant> EnsureSqlOSRoleGrantAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string resourceId,
        string roleKeyOrId,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        await FindRequiredSubjectAsync(context, normalizedSubjectId, cancellationToken);
        await FindRequiredResourceAsync(context, normalizedResourceId, cancellationToken);
        var roleId = await ResolveRoleIdAsync(context, roleKeyOrId, cancellationToken);

        var grant = await FindGrantAsync(
            context,
            normalizedSubjectId,
            normalizedResourceId,
            roleId,
            cancellationToken);

        if (grant == null)
        {
            return await GrantRoleCoreAsync(
                context,
                normalizedSubjectId,
                normalizedResourceId,
                roleId,
                description,
                cancellationToken,
                roleAlreadyResolved: true);
        }

        if (description != null)
        {
            grant.Description = NormalizeOptional(description);
        }

        grant.UpdatedAt = DateTime.UtcNow;
        return grant;
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

    public static async Task<SqlOSFgaUser> EnsureSqlOSUserSubjectAsync(
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

    public static async Task<SqlOSFgaAgent> EnsureSqlOSAgentSubjectAsync(
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

    public static async Task<SqlOSFgaServiceAccount> EnsureSqlOSServiceAccountSubjectAsync(
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
        var depth = 0;

        while (!string.IsNullOrWhiteSpace(currentId))
        {
            if (!visited.Add(currentId))
            {
                throw new InvalidOperationException("FGA resource hierarchy contains a cycle.");
            }

            if (depth > DefaultMaxResourceHierarchyDepth)
            {
                throw new InvalidOperationException($"FGA resource hierarchy exceeds the maximum depth of {DefaultMaxResourceHierarchyDepth}.");
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

        httpContext.User = validated.Principal;
        httpContext.Items[SqlOSAccessTokenValidationExtensions.ValidatedTokenItemKey] = validated;
        return await next(context);
    }

    private static IResult Unauthorized(SqlOSAccessTokenValidationOptions options, string description)
        => new SqlOSUnauthorizedTokenResult(options, description);

    private sealed class SqlOSUnauthorizedTokenResult : IResult
    {
        private readonly SqlOSAccessTokenValidationOptions _options;
        private readonly string _description;

        public SqlOSUnauthorizedTokenResult(SqlOSAccessTokenValidationOptions options, string description)
        {
            _options = options;
            _description = description;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            httpContext.Response.Headers.WWWAuthenticate = BuildChallenge();
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = "invalid_token",
                error_description = _description
            });
        }

        private string BuildChallenge()
        {
            var parts = new List<string>
            {
                $"Bearer realm=\"{EscapeHeaderValue(_options.Realm)}\"",
                "error=\"invalid_token\"",
                $"error_description=\"{EscapeHeaderValue(_description)}\""
            };

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
