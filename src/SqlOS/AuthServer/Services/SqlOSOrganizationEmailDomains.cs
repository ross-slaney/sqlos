using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Decides whether an email address is at a domain an organization has proven it owns. Only
/// active, unrevoked <see cref="SqlOSOrganizationDomain"/> claims count; an unverified
/// <see cref="SqlOSOrganization.PrimaryDomain"/> never does. Domains are compared as canonical
/// lower-case IDNA ASCII strings with ordinal equality in code, so database collation cannot make
/// a look-alike domain match a verified one.
/// </summary>
internal static class SqlOSOrganizationEmailDomains
{
    public const string UntrustedDomainReason = "untrusted_email_domain";

    public static async Task<bool> IsAtVerifiedDomainAsync(
        ISqlOSAuthServerDbContext context,
        string organizationId,
        string? emailOrNormalizedEmail,
        CancellationToken cancellationToken)
    {
        if (!SqlOSEmailAddress.TryNormalize(emailOrNormalizedEmail, out var normalizedEmail))
        {
            return false;
        }

        var domain = SqlOSEmailAddress.GetDomain(normalizedEmail);
        var verifiedDomains = await context.Set<SqlOSOrganizationDomain>()
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                && x.Status == SqlOSOrganizationDomainStatuses.Active
                && x.RevokedAt == null)
            .Select(x => x.Domain)
            .ToListAsync(cancellationToken);
        return verifiedDomains.Any(verified => SameDomain(verified, domain));
    }

    /// <summary>Ordinal comparison of two domains after canonical IDNA ASCII normalization.</summary>
    public static bool SameDomain(string? left, string? right)
    {
        var normalizedLeft = SqlOSAdminService.NormalizeDomain(left);
        var normalizedRight = SqlOSAdminService.NormalizeDomain(right);
        return SqlOSEmailAddress.TryNormalizeDomain(normalizedLeft, out var asciiLeft)
            && SqlOSEmailAddress.TryNormalizeDomain(normalizedRight, out var asciiRight)
            && string.Equals(asciiLeft, asciiRight, StringComparison.Ordinal);
    }
}
