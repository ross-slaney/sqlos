using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Fga;
using SqlOS.Fga.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// The one domain rule for SCIM grant boundaries. A mapped grant may target only the connection's
/// grant boundary resource or one of its descendants in the FGA tree. The admin service applies it
/// when the seed, service API, or dashboard saves a boundary or mapping; the SCIM service applies it
/// again, authoritatively, before creating or keeping any mapped grant.
/// </summary>
internal static class SqlOSScimGrantBoundaryPolicy
{
    public const int MaxResourceIdLength = 256;

    // Connection-level status projected to admin responses.
    public const string StatusConfigured = "configured";
    public const string StatusMissing = "missing";
    public const string StatusNotFound = "not_found";

    // Mapping-level status projected to admin responses.
    public const string MappingWithin = "within";
    public const string MappingOutside = "outside";
    public const string MappingResourceNotFound = "resource_not_found";
    public const string MappingBoundaryMissing = "boundary_missing";
    public const string MappingBoundaryNotFound = "boundary_not_found";
    public const string MappingHierarchyInvalid = "hierarchy_invalid";
    public const string MappingCheckedAtGrantTime = "checked_at_grant_time";

    public static SqlOSFgaSubtreeResolver CreateResolver(ISqlOSAuthServerDbContext context)
        => new(
            context.Set<SqlOSFgaResource>(),
            SqlOSFgaHierarchyDepth.Resolve(context.Database, context as DbContext));

    /// <summary>
    /// The authority for the connection's mapping to grant <paramref name="groupSubjectId"/> a role
    /// on <paramref name="resourceId"/>, produced only when the resource is the connection's grant
    /// boundary or one of its descendants, and covering only that group and resource (#421).
    /// </summary>
    public static async Task<(GrantAuthority? Authority, SqlOSFgaSubtreeMembership Membership)> AuthorizeMappedGrantAsync(
        SqlOSFgaSubtreeResolver resolver,
        SqlOSScimConnection connection,
        string groupSubjectId,
        string resourceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connection.GrantBoundaryResourceId))
        {
            return (null, SqlOSFgaSubtreeMembership.BoundaryNotFound);
        }

        var membership = await resolver.CheckAsync(resourceId, connection.GrantBoundaryResourceId, cancellationToken);
        return membership == SqlOSFgaSubtreeMembership.Within
            ? (new GrantAuthority(FgaActor.Directory(connection.Id), groupSubjectId, resourceId, connection.GrantBoundaryResourceId), membership)
            : (null, membership);
    }

    /// <summary>Trims a boundary ID; <c>null</c> means "not provided".</summary>
    public static string? Normalize(string? grantBoundaryResourceId)
    {
        if (string.IsNullOrWhiteSpace(grantBoundaryResourceId))
        {
            return null;
        }

        var normalized = grantBoundaryResourceId.Trim();
        if (normalized.Length > MaxResourceIdLength)
        {
            throw new SqlOSScimGrantBoundaryException(
                SqlOSScimGrantBoundaryErrors.BoundaryInvalid,
                $"A SCIM grant boundary resource ID cannot exceed {MaxResourceIdLength} characters.");
        }

        return normalized;
    }

    /// <summary>Returns the stored spelling of an existing boundary resource or throws a typed error.</summary>
    public static async Task<string> RequireExistingBoundaryAsync(
        SqlOSFgaSubtreeResolver resolver,
        string grantBoundaryResourceId,
        CancellationToken cancellationToken)
        => await resolver.FindIdAsync(grantBoundaryResourceId, cancellationToken)
            ?? throw new SqlOSScimGrantBoundaryException(
                SqlOSScimGrantBoundaryErrors.BoundaryNotFound,
                $"SCIM grant boundary resource '{grantBoundaryResourceId}' was not found. Create the organization's root FGA resource first, then set it as the boundary.",
                grantBoundaryResourceId);

    /// <summary>
    /// Returns the resource an enabled mapping always targets, or <c>null</c> when the target is
    /// resolved from the pushed group name and can only be checked when the grant is created.
    /// </summary>
    public static string? GetFixedTarget(
        string normalizedMatchType,
        string? groupPattern,
        string? resourceId,
        string? resourceIdTemplate)
    {
        if (!string.IsNullOrWhiteSpace(resourceId))
        {
            return resourceId.Trim();
        }

        if (string.IsNullOrWhiteSpace(resourceIdTemplate))
        {
            return null;
        }

        var template = resourceIdTemplate.Trim();
        if (!string.Equals(normalizedMatchType, SqlOSScimGroupMappingMatchTypes.Pattern, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(groupPattern))
        {
            // Only pattern captures are substituted, so any other template is a literal resource ID.
            return template;
        }

        string[] captureNames;
        try
        {
            captureNames = new Regex(
                    groupPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100))
                .GetGroupNames();
        }
        catch (ArgumentException)
        {
            return null;
        }

        var variesWithGroupName = captureNames
            .Where(name => !int.TryParse(name, out _))
            .Any(name => template.Contains("{" + name + "}", StringComparison.Ordinal));
        return variesWithGroupName ? null : template;
    }

    /// <summary>
    /// Enforces the save-time rule for an enabled mapping: the connection must have an existing
    /// boundary, and a fixed target that exists must sit inside it. A target that does not exist yet
    /// is accepted; the grant-time check records <c>scim.grant.resource_missing</c> until it exists
    /// and then applies the same containment rule. Templates that vary with the group name are
    /// enforced at grant time.
    /// </summary>
    public static async Task EnsureMappingWithinBoundaryAsync(
        SqlOSFgaSubtreeResolver resolver,
        string? grantBoundaryResourceId,
        string normalizedMatchType,
        string? groupPattern,
        string? resourceId,
        string? resourceIdTemplate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grantBoundaryResourceId))
        {
            throw new SqlOSScimGrantBoundaryException(
                SqlOSScimGrantBoundaryErrors.BoundaryRequired,
                "Set the SCIM connection's grant boundary resource before enabling group mappings. Mapped grants must target the boundary or one of its descendants.");
        }

        var boundary = await RequireExistingBoundaryAsync(resolver, grantBoundaryResourceId, cancellationToken);
        var fixedTarget = GetFixedTarget(normalizedMatchType, groupPattern, resourceId, resourceIdTemplate);
        if (fixedTarget == null)
        {
            return;
        }

        switch (await resolver.CheckAsync(fixedTarget, boundary, cancellationToken))
        {
            case SqlOSFgaSubtreeMembership.Within:
            case SqlOSFgaSubtreeMembership.ResourceNotFound:
                return;
            case SqlOSFgaSubtreeMembership.HierarchyCycle:
            case SqlOSFgaSubtreeMembership.HierarchyTooDeep:
                throw new SqlOSScimGrantBoundaryException(
                    SqlOSScimGrantBoundaryErrors.HierarchyInvalid,
                    $"SCIM mapping resource '{fixedTarget}' cannot be verified against grant boundary '{boundary}' because its FGA ancestor chain contains a cycle or exceeds the configured maximum depth.",
                    boundary,
                    fixedTarget);
            default:
                throw new SqlOSScimGrantBoundaryException(
                    SqlOSScimGrantBoundaryErrors.ResourceOutsideBoundary,
                    $"SCIM mapping resource '{fixedTarget}' is not grant boundary '{boundary}' or one of its descendants in the FGA resource tree.",
                    boundary,
                    fixedTarget);
        }
    }

    /// <summary>Projects the mapping-level boundary status shown by the admin API and dashboard.</summary>
    public static async Task<string> DescribeMappingAsync(
        SqlOSFgaSubtreeResolver resolver,
        string? grantBoundaryResourceId,
        SqlOSScimGroupMappingShape mapping,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grantBoundaryResourceId))
        {
            return MappingBoundaryMissing;
        }

        var boundary = await resolver.FindIdAsync(grantBoundaryResourceId, cancellationToken);
        if (boundary == null)
        {
            return MappingBoundaryNotFound;
        }

        var fixedTarget = GetFixedTarget(mapping.MatchType, mapping.GroupPattern, mapping.ResourceId, mapping.ResourceIdTemplate);
        if (fixedTarget == null)
        {
            return MappingCheckedAtGrantTime;
        }

        return await resolver.CheckAsync(fixedTarget, boundary, cancellationToken) switch
        {
            SqlOSFgaSubtreeMembership.Within => MappingWithin,
            SqlOSFgaSubtreeMembership.ResourceNotFound => MappingResourceNotFound,
            SqlOSFgaSubtreeMembership.HierarchyCycle or SqlOSFgaSubtreeMembership.HierarchyTooDeep => MappingHierarchyInvalid,
            SqlOSFgaSubtreeMembership.BoundaryNotFound => MappingBoundaryNotFound,
            _ => MappingOutside
        };
    }
}

internal sealed record SqlOSScimGroupMappingShape(
    string MatchType,
    string? GroupPattern,
    string? ResourceId,
    string? ResourceIdTemplate);
