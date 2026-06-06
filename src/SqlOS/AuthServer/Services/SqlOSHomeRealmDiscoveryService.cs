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

        var verifiedMatch = await _context.Set<SqlOSOrganizationDomain>()
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
                    ConnectionId = connection.Id
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (verifiedMatch != null)
        {
            return new SqlOSHomeRealmDiscoveryResult(
                "sso",
                verifiedMatch.OrganizationId,
                verifiedMatch.OrganizationName,
                verifiedMatch.PrimaryDomain,
                verifiedMatch.ConnectionId);
        }

        var match = await _context.Set<SqlOSOrganization>()
            .Where(x => x.PrimaryDomain == normalizedDomain && x.IsActive)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.PrimaryDomain,
                ConnectionId = x.SsoConnections
                    .Where(c => c.IsEnabled && c.IdentityProviderEntityId != "" && c.SingleSignOnUrl != "" && c.X509CertificatePem != "")
                    .Select(c => c.Id)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (match?.ConnectionId == null)
        {
            return new SqlOSHomeRealmDiscoveryResult("password", null, null, null, null);
        }

        return new SqlOSHomeRealmDiscoveryResult("sso", match.Id, match.Name, match.PrimaryDomain, match.ConnectionId);
    }
}
