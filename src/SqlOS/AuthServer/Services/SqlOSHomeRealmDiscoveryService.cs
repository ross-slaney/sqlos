using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;

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

        var match = await _context.Set<Models.SqlOSOrganization>()
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
