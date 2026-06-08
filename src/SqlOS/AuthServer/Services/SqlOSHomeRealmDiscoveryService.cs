using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSHomeRealmDiscoveryService
{
    private readonly ISqlOSAuthServerDbContext _context;

    public SqlOSHomeRealmDiscoveryService(ISqlOSAuthServerDbContext context)
    {
        _context = context;
    }

    public async Task<SqlOSHomeRealmDiscoveryResult> DiscoverAsync(SqlOSHomeRealmDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedDomain = SqlOSAdminService.NormalizeDomain(request.Email);
        if (string.IsNullOrWhiteSpace(normalizedDomain))
        {
            return new SqlOSHomeRealmDiscoveryResult("password", null, null, null, null);
        }

        var normalizedEmail = string.IsNullOrWhiteSpace(request.Email)
            ? null
            : SqlOSAdminService.NormalizeEmail(request.Email);

        var verifiedMatches = await _context.Set<SqlOSOrganizationDomain>()
            .Where(domain => domain.Domain == normalizedDomain
                && domain.Status == SqlOSOrganizationDomainStatuses.Active
                && domain.RevokedAt == null)
            .Join(
                _context.Set<SqlOSOrganization>().Where(organization => organization.IsActive),
                domain => domain.OrganizationId,
                organization => organization.Id,
                (domain, organization) => new { Domain = domain, Organization = organization })
            .Join(
                _context.Set<SqlOSSsoConnection>()
                    .Where(connection => connection.IsEnabled
                        && connection.IdentityProviderEntityId != ""
                        && connection.SingleSignOnUrl != ""
                        && connection.X509CertificatePem != ""),
                match => match.Organization.Id,
                connection => connection.OrganizationId,
                (match, connection) => new
                {
                    OrganizationId = match.Organization.Id,
                    OrganizationName = match.Organization.Name,
                    PrimaryDomain = match.Domain.Domain,
                    ConnectionId = connection.Id,
                    connection.AutoLinkByEmail,
                    connection.AutoProvisionUsers
                })
            .ToListAsync(cancellationToken);

        foreach (var verifiedMatch in verifiedMatches)
        {
            if (await ShouldRouteToSsoAsync(
                verifiedMatch.OrganizationId,
                normalizedEmail,
                verifiedMatch.AutoLinkByEmail,
                verifiedMatch.AutoProvisionUsers,
                cancellationToken))
            {
                return new SqlOSHomeRealmDiscoveryResult(
                    "sso",
                    verifiedMatch.OrganizationId,
                    verifiedMatch.OrganizationName,
                    verifiedMatch.PrimaryDomain,
                    verifiedMatch.ConnectionId);
            }
        }

        var match = await _context.Set<SqlOSOrganization>()
            .Where(x => x.PrimaryDomain == normalizedDomain && x.IsActive)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.PrimaryDomain,
                Connection = x.SsoConnections
                    .Where(c => c.IsEnabled && c.IdentityProviderEntityId != "" && c.SingleSignOnUrl != "" && c.X509CertificatePem != "")
                    .Select(c => new
                    {
                        c.Id,
                        c.AutoLinkByEmail,
                        c.AutoProvisionUsers
                    })
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (match?.Connection == null
            || !await ShouldRouteToSsoAsync(
                match.Id,
                normalizedEmail,
                match.Connection.AutoLinkByEmail,
                match.Connection.AutoProvisionUsers,
                cancellationToken))
        {
            return new SqlOSHomeRealmDiscoveryResult("password", null, null, null, null);
        }

        return new SqlOSHomeRealmDiscoveryResult("sso", match.Id, match.Name, match.PrimaryDomain, match.Connection.Id);
    }

    private async Task<bool> ShouldRouteToSsoAsync(
        string organizationId,
        string? normalizedEmail,
        bool requireSsoForExistingMembers,
        bool allowJitProvisioning,
        CancellationToken cancellationToken)
    {
        var hasExistingMember = !string.IsNullOrWhiteSpace(normalizedEmail)
            && await _context.Set<SqlOSUserEmail>()
                .Where(email => email.NormalizedEmail == normalizedEmail && email.IsVerified)
                .Join(
                    _context.Set<SqlOSMembership>().Where(membership => membership.OrganizationId == organizationId && membership.IsActive),
                    email => email.UserId,
                    membership => membership.UserId,
                    (_, _) => true)
                .AnyAsync(cancellationToken);

        return hasExistingMember
            ? requireSsoForExistingMembers
            : allowJitProvisioning;
    }
}
