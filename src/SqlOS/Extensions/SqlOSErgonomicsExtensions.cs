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
        await EnsureParentChainDoesNotCreateCycleAsync(context, normalizedResourceId, normalizedParentId, cancellationToken);

        var resource = await FindResourceAsync(context, normalizedResourceId, cancellationToken);
        if (resource == null)
        {
            resource = CreateResource(
                normalizedResourceId,
                normalizedParentId,
                name,
                resourceTypeId,
                description);
            context.Set<SqlOSFgaResource>().Add(resource);
            return resource;
        }

        resource.ParentId = normalizedParentId;
        resource.Name = RequireValue(name, nameof(name));
        resource.ResourceTypeId = RequireValue(resourceTypeId, nameof(resourceTypeId));
        resource.Description = NormalizeOptional(description);
        resource.IsActive = true;
        resource.UpdatedAt = DateTime.UtcNow;
        return resource;
    }

    public static SqlOSFgaGrant GrantSqlOSRole(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string resourceId,
        string roleId,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var normalizedRoleId = RequireValue(roleId, nameof(roleId));

        var grant = new SqlOSFgaGrant
        {
            Id = BuildGrantId(normalizedSubjectId, normalizedResourceId, normalizedRoleId),
            SubjectId = normalizedSubjectId,
            ResourceId = normalizedResourceId,
            RoleId = normalizedRoleId,
            Description = NormalizeOptional(description)
        };
        context.Set<SqlOSFgaGrant>().Add(grant);
        return grant;
    }

    public static async Task<SqlOSFgaGrant> GrantSqlOSRoleAsync(
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

        var grant = new SqlOSFgaGrant
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

        grant.Description = NormalizeOptional(description);
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
        bool isActive = true,
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
                IsActive = isActive
            };
            users.Add(user);
            return user;
        }

        user.Email = NormalizeOptional(email);
        user.IsActive = isActive;
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

        agent.AgentType = NormalizeOptional(agentType);
        agent.Description = NormalizeOptional(description);
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
        account.Description = NormalizeOptional(description);
        account.ExpiresAt = expiresAt;
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
        subject.OrganizationId = NormalizeOptional(organizationId);
        subject.ExternalRef = NormalizeOptional(externalRef) ?? normalizedSubjectId;
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
            ?? throw new InvalidOperationException($"FGA resource '{resourceId}' was not found. Provision the resource before granting roles.");

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
