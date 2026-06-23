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

        var options = SqlOSAccessTokenValidationMiddleware.ValidateOptions(new SqlOSAccessTokenValidationOptions
        {
            ExpectedAudience = expectedAudience
        });

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
            context,
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
        EnsureParentChainDoesNotCreateCycle(context, normalizedResourceId, normalizedParentId);

        var resource = await context.Set<SqlOSFgaResource>()
            .FirstOrDefaultAsync(x => x.Id == normalizedResourceId, cancellationToken);

        if (resource == null)
        {
            resource = CreateResource(
                context,
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
        string roleKeyOrId,
        string? description = null,
        string subjectTypeId = "user",
        string? subjectDisplayName = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var roleId = ResolveRoleId(context, roleKeyOrId);
        EnsureSubject(context, normalizedSubjectId, subjectTypeId, subjectDisplayName);

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
        string subjectTypeId = "user",
        string? subjectDisplayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var roleId = await ResolveRoleIdAsync(context, roleKeyOrId, cancellationToken);
        await EnsureSubjectAsync(context, normalizedSubjectId, subjectTypeId, subjectDisplayName, cancellationToken);

        var grant = await context.Set<SqlOSFgaGrant>()
            .FirstOrDefaultAsync(
                x => x.SubjectId == normalizedSubjectId
                    && x.ResourceId == normalizedResourceId
                    && x.RoleId == roleId,
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

    private static SqlOSFgaResource CreateResource(
        ISqlOSFgaDbContext context,
        string resourceId,
        string? parentResourceId,
        string name,
        string resourceTypeId,
        string? description)
    {
        var normalizedResourceId = RequireValue(resourceId, nameof(resourceId));
        var normalizedParentId = NormalizeOptional(parentResourceId);
        EnsureParentChainDoesNotCreateCycle(context, normalizedResourceId, normalizedParentId);

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

    private static void EnsureParentChainDoesNotCreateCycle(
        ISqlOSFgaDbContext context,
        string resourceId,
        string? parentId)
    {
        if (string.IsNullOrWhiteSpace(parentId))
        {
            return;
        }

        if (string.Equals(resourceId, parentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FGA resource parent cannot be the resource itself.");
        }

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

            currentId = context.Set<SqlOSFgaResource>()
                .AsNoTracking()
                .Where(r => r.Id == currentId)
                .Select(r => r.ParentId)
                .FirstOrDefault();
            depth++;
        }
    }

    private static string ResolveRoleId(ISqlOSFgaDbContext context, string roleKeyOrId)
    {
        var normalizedRole = RequireValue(roleKeyOrId, nameof(roleKeyOrId));
        return context.Set<SqlOSFgaRole>()
            .Where(x => x.Key == normalizedRole || x.Id == normalizedRole)
            .Select(x => x.Id)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"FGA role '{normalizedRole}' was not found.");
    }

    private static async Task<string> ResolveRoleIdAsync(
        ISqlOSFgaDbContext context,
        string roleKeyOrId,
        CancellationToken cancellationToken)
    {
        var normalizedRole = RequireValue(roleKeyOrId, nameof(roleKeyOrId));
        var roleId = await context.Set<SqlOSFgaRole>()
            .Where(x => x.Key == normalizedRole || x.Id == normalizedRole)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return roleId
            ?? throw new InvalidOperationException($"FGA role '{normalizedRole}' was not found.");
    }

    private static void EnsureSubject(
        ISqlOSFgaDbContext context,
        string subjectId,
        string subjectTypeId,
        string? displayName)
    {
        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var subject = context.Set<SqlOSFgaSubject>().FirstOrDefault(x => x.Id == normalizedSubjectId);
        if (subject == null)
        {
            context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject
            {
                Id = normalizedSubjectId,
                SubjectTypeId = RequireValue(subjectTypeId, nameof(subjectTypeId)),
                DisplayName = NormalizeOptional(displayName) ?? normalizedSubjectId,
                ExternalRef = normalizedSubjectId
            });
        }
    }

    private static async Task EnsureSubjectAsync(
        ISqlOSFgaDbContext context,
        string subjectId,
        string subjectTypeId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var subject = await context.Set<SqlOSFgaSubject>()
            .FirstOrDefaultAsync(x => x.Id == normalizedSubjectId, cancellationToken);

        if (subject == null)
        {
            context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject
            {
                Id = normalizedSubjectId,
                SubjectTypeId = RequireValue(subjectTypeId, nameof(subjectTypeId)),
                DisplayName = NormalizeOptional(displayName) ?? normalizedSubjectId,
                ExternalRef = normalizedSubjectId
            });
        }
    }

    private static string BuildGrantId(string subjectId, string resourceId, string roleId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{subjectId}\n{resourceId}\n{roleId}"));
        return $"grant::{Convert.ToHexString(bytes).ToLowerInvariant()[..32]}";
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
