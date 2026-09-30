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
        if (!SqlOSEmailAddress.TryNormalize(request.Email, out var normalizedEmail))
        {
            return new SqlOSHomeRealmDiscoveryResult("password", null, null, null, null);
        }

        // Domains are matched as canonical IDNA ASCII strings and confirmed in code, so a
        // look-alike domain can never route to another organization's SSO.
        var asciiDomain = SqlOSEmailAddress.GetDomain(normalizedEmail);
        var legacyDomain = SqlOSAdminService.NormalizeDomain(request.Email) ?? asciiDomain;

        var verifiedMatches = (await _context.Set<SqlOSOrganizationDomain>()
            .Where(domain => domain.Domain == asciiDomain
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
            .ToListAsync(cancellationToken))
            .Where(match => SqlOSOrganizationEmailDomains.SameDomain(match.PrimaryDomain, asciiDomain));

        foreach (var verifiedMatch in verifiedMatches)
        {
            if (await ShouldRouteToSsoAsync(
                verifiedMatch.OrganizationId,
                request.Email,
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

        var primaryMatches = await _context.Set<SqlOSOrganization>()
            .Where(x => (x.PrimaryDomain == asciiDomain || x.PrimaryDomain == legacyDomain) && x.IsActive)
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
            .ToListAsync(cancellationToken);
        var match = primaryMatches.FirstOrDefault(x => SqlOSOrganizationEmailDomains.SameDomain(x.PrimaryDomain, asciiDomain));

        if (match?.Connection == null
            || !await ShouldRouteToSsoAsync(
                match.Id,
                request.Email,
                match.Connection.AutoLinkByEmail,
                match.Connection.AutoProvisionUsers,
                cancellationToken))
        {
            return new SqlOSHomeRealmDiscoveryResult("password", null, null, null, null);
        }

        return new SqlOSHomeRealmDiscoveryResult("sso", match.Id, match.Name, match.PrimaryDomain, match.Connection.Id);
    }

    /// <summary>
    /// Binds a discovery result to an authorization request. An SSO match binds the
    /// connection together with that connection's organization. Any other result clears the
    /// connection an earlier discovery bound, together with its organization, so a request
    /// never pairs one discovery's connection with an organization from somewhere else.
    /// </summary>
    internal static void BindToAuthorizationRequest(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSHomeRealmDiscoveryResult discovery)
    {
        if (string.Equals(discovery.Mode, "sso", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(discovery.ConnectionId)
            && !string.IsNullOrWhiteSpace(discovery.OrganizationId))
        {
            authorizationRequest.OrganizationId = discovery.OrganizationId;
            authorizationRequest.ResolvedOrganizationId = discovery.OrganizationId;
            authorizationRequest.ConnectionId = discovery.ConnectionId;
            authorizationRequest.ResolvedConnectionId = discovery.ConnectionId;
            return;
        }

        if (!string.IsNullOrWhiteSpace(authorizationRequest.ConnectionId))
        {
            authorizationRequest.OrganizationId = null;
            authorizationRequest.ResolvedOrganizationId = null;
            authorizationRequest.ConnectionId = null;
            authorizationRequest.ResolvedConnectionId = null;
        }
    }

    private async Task<bool> ShouldRouteToSsoAsync(
        string organizationId,
        string? email,
        bool requireSsoForExistingMembers,
        bool allowJitProvisioning,
        CancellationToken cancellationToken)
    {
        var existingEmail = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FindByEmailAsync(email, cancellationToken);
        var hasExistingMember = existingEmail is { IsVerified: true }
            && await _context.Set<SqlOSMembership>()
                .AnyAsync(
                    membership => membership.OrganizationId == organizationId
                        && membership.IsActive
                        && membership.UserId == existingEmail.UserId,
                    cancellationToken);

        return hasExistingMember
            ? requireSsoForExistingMembers
            : allowJitProvisioning;
    }
}
