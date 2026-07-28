using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSOrganizationDomainService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSAdminService _adminService;
    private readonly ISqlOSDomainDnsVerifier _dnsVerifier;

    public SqlOSOrganizationDomainService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCryptoService cryptoService,
        SqlOSAdminService adminService,
        ISqlOSDomainDnsVerifier dnsVerifier)
    {
        _context = context;
        _options = options.Value;
        _cryptoService = cryptoService;
        _adminService = adminService;
        _dnsVerifier = dnsVerifier;
    }

    public async Task<IReadOnlyList<SqlOSOrganizationDomainResult>> ListOrganizationDomainsAsync(
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireOrganizationAsync(organizationId, cancellationToken);
        var domains = await _context.Set<SqlOSOrganizationDomain>()
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.RevokedAt == null)
            .OrderByDescending(x => x.Status == SqlOSOrganizationDomainStatuses.Active)
            .ThenByDescending(x => x.UpdatedAt)
            .ToListAsync(cancellationToken);

        return domains.Select(ToResult).ToList();
    }

    public async Task<SqlOSOrganizationDomainResult?> GetPreferredDomainAsync(
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        var domain = await GetPreferredDomainEntityAsync(organizationId, tracking: false, cancellationToken);
        return domain == null ? null : ToResult(domain);
    }

    public async Task<bool> HasActiveDomainAsync(string organizationId, CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSOrganizationDomain>()
            .AsNoTracking()
            .AnyAsync(x => x.OrganizationId == organizationId
                && x.Status == SqlOSOrganizationDomainStatuses.Active
                && x.RevokedAt == null, cancellationToken);

    public async Task<bool> HasPendingDomainAsync(string organizationId, CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSOrganizationDomain>()
            .AsNoTracking()
            .AnyAsync(x => x.OrganizationId == organizationId
                && x.Status == SqlOSOrganizationDomainStatuses.PendingOwnership
                && x.RevokedAt == null, cancellationToken);

    public async Task<SqlOSOrganizationDomainResult> StartVerificationAsync(
        string organizationId,
        SqlOSSsoPortalDomainRequest request,
        HttpContext? httpContext = null,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var organization = await RequireOrganizationAsync(organizationId, cancellationToken);
        var domain = SqlOSDomainOwnershipVerification.NormalizeDomain(request.Domain, _options.SsoPortal);
        await EnsureNoActiveClaimElsewhereAsync(organizationId, domain, cancellationToken);

        var now = DateTime.UtcNow;
        var claim = await _context.Set<SqlOSOrganizationDomain>()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Domain == domain && x.RevokedAt == null, cancellationToken);

        if (claim == null)
        {
            claim = new SqlOSOrganizationDomain
            {
                Id = _cryptoService.GenerateId("dom"),
                OrganizationId = organization.Id,
                Domain = domain,
                Status = SqlOSOrganizationDomainStatuses.PendingOwnership,
                VerificationToken = SqlOSDomainOwnershipVerification.CreateVerificationToken(_cryptoService, _options.SsoPortal),
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.Set<SqlOSOrganizationDomain>().Add(claim);
        }
        else if (claim.Status != SqlOSOrganizationDomainStatuses.Active)
        {
            claim.Status = SqlOSOrganizationDomainStatuses.PendingOwnership;
            claim.VerificationToken ??= SqlOSDomainOwnershipVerification.CreateVerificationToken(_cryptoService, _options.SsoPortal);
            claim.LastError = null;
            claim.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "organization.domain.verification.started",
            claim,
            httpContext,
            new { claim.Domain, claim.Status },
            cancellationToken);

        return ToResult(claim);
    }

    public async Task<SqlOSOrganizationDomainResult> ConfirmOwnershipAsync(
        string organizationId,
        string domainId,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var ownershipCheck = await CheckOwnershipAsync(
            organizationId,
            domainId,
            cancellationToken);
        return await ApplyOwnershipCheckAsync(
            ownershipCheck,
            httpContext,
            cancellationToken);
    }

    internal async Task<SqlOSOrganizationDomainOwnershipCheck> CheckOwnershipAsync(
        string organizationId,
        string domainId,
        CancellationToken cancellationToken = default)
    {
        var claim = await _context.Set<SqlOSOrganizationDomain>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == domainId
                    && x.OrganizationId == organizationId
                    && x.RevokedAt == null,
                cancellationToken)
            ?? throw new InvalidOperationException("Domain verification request was not found.");

        if (claim.Status == SqlOSOrganizationDomainStatuses.Active)
        {
            return new SqlOSOrganizationDomainOwnershipCheck(
                claim.OrganizationId,
                claim.Id,
                claim.Domain,
                claim.VerificationToken,
                Verified: true);
        }

        if (string.IsNullOrWhiteSpace(claim.VerificationToken))
        {
            throw new InvalidOperationException("Domain verification token is missing.");
        }

        var ownership = SqlOSDomainOwnershipVerification.BuildOwnershipRecord(
            claim.Domain,
            claim.VerificationToken,
            _options.SsoPortal);
        var verified = SqlOSDomainOwnershipVerification.IsLocalhostDomain(claim.Domain)
            && _options.SsoPortal.AllowLocalhostDomainVerification;
        if (!verified)
        {
            verified = await _dnsVerifier.HasTxtRecordValueAsync(
                ownership.Name,
                ownership.Value,
                cancellationToken);
        }

        return new SqlOSOrganizationDomainOwnershipCheck(
            claim.OrganizationId,
            claim.Id,
            claim.Domain,
            claim.VerificationToken,
            verified);
    }

    internal async Task<SqlOSOrganizationDomainResult> ApplyOwnershipCheckAsync(
        SqlOSOrganizationDomainOwnershipCheck ownershipCheck,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        if (_context is DbContext dbContext)
        {
            var trackedClaim = dbContext.ChangeTracker
                .Entries<SqlOSOrganizationDomain>()
                .FirstOrDefault(x => x.Entity.Id == ownershipCheck.DomainId);
            if (trackedClaim != null)
            {
                await trackedClaim.ReloadAsync(cancellationToken);
            }
        }

        var claim = await _context.Set<SqlOSOrganizationDomain>()
            .Include(x => x.Organization)
            .FirstOrDefaultAsync(
                x => x.Id == ownershipCheck.DomainId
                    && x.OrganizationId == ownershipCheck.OrganizationId
                    && x.RevokedAt == null,
                cancellationToken)
            ?? throw new InvalidOperationException("Domain verification request was not found.");

        if (claim.Status == SqlOSOrganizationDomainStatuses.Active)
        {
            return ToResult(claim);
        }

        if (string.IsNullOrWhiteSpace(claim.VerificationToken))
        {
            throw new InvalidOperationException("Domain verification token is missing.");
        }

        if (!string.Equals(claim.Domain, ownershipCheck.Domain, StringComparison.Ordinal)
            || !string.Equals(
                claim.VerificationToken,
                ownershipCheck.VerificationToken,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Domain verification request changed. Retry ownership verification.");
        }

        var ownership = SqlOSDomainOwnershipVerification.BuildOwnershipRecord(
            claim.Domain,
            claim.VerificationToken,
            _options.SsoPortal);
        var now = DateTime.UtcNow;
        claim.LastCheckedAt = now;
        claim.UpdatedAt = now;
        if (!ownershipCheck.Verified)
        {
            claim.LastError = $"TXT record not found. Create {ownership.Type} {ownership.Name} with value {ownership.Value}.";
            await _context.SaveChangesAsync(cancellationToken);
            await RecordAuditAsync(
                "organization.domain.verification.failed",
                claim,
                httpContext,
                new { claim.Domain, ownership.Name },
                cancellationToken);

            return ToResult(claim);
        }

        await EnsureNoActiveClaimElsewhereAsync(
            ownershipCheck.OrganizationId,
            claim.Domain,
            cancellationToken);
        claim.Status = SqlOSOrganizationDomainStatuses.Active;
        claim.VerifiedAt = now;
        claim.LastError = null;

        if (claim.Organization != null
            && string.IsNullOrWhiteSpace(claim.Organization.PrimaryDomain)
            && !await IsPrimaryDomainInUseElsewhereAsync(
                ownershipCheck.OrganizationId,
                claim.Domain,
                cancellationToken))
        {
            claim.Organization.PrimaryDomain = claim.Domain;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "organization.domain.verification.confirmed",
            claim,
            httpContext,
            new { claim.Domain, ownership.Name },
            cancellationToken);

        return ToResult(claim);
    }

    public async Task<SqlOSOrganizationDomainResult> RevokeAsync(
        string organizationId,
        string domainId,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var claim = await _context.Set<SqlOSOrganizationDomain>()
            .FirstOrDefaultAsync(x => x.Id == domainId && x.OrganizationId == organizationId && x.RevokedAt == null, cancellationToken)
            ?? throw new InvalidOperationException("Domain verification request was not found.");

        var now = DateTime.UtcNow;
        claim.Status = SqlOSOrganizationDomainStatuses.Revoked;
        claim.RevokedAt = now;
        claim.UpdatedAt = now;
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "organization.domain.verification.revoked",
            claim,
            httpContext,
            new { claim.Domain },
            cancellationToken);

        return ToResult(claim);
    }

    public SqlOSOrganizationDomainResult ToResult(SqlOSOrganizationDomain domain)
    {
        var ownership = domain.Status == SqlOSOrganizationDomainStatuses.PendingOwnership
            && !string.IsNullOrWhiteSpace(domain.VerificationToken)
                ? SqlOSDomainOwnershipVerification.BuildOwnershipRecord(
                    domain.Domain,
                    domain.VerificationToken,
                    _options.SsoPortal)
                : null;

        return new SqlOSOrganizationDomainResult(
            domain.Id,
            domain.OrganizationId,
            domain.Domain,
            domain.Status,
            ownership,
            domain.CreatedAt,
            domain.VerifiedAt,
            domain.LastCheckedAt,
            domain.RevokedAt,
            domain.LastError);
    }

    private async Task<SqlOSOrganization> RequireOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken)
        => await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(x => x.Id == organizationId && x.IsActive, cancellationToken)
           ?? throw new InvalidOperationException("Organization not found.");

    private async Task<SqlOSOrganizationDomain?> GetPreferredDomainEntityAsync(
        string organizationId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = _context.Set<SqlOSOrganizationDomain>()
            .Where(x => x.OrganizationId == organizationId && x.RevokedAt == null);
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query
            .OrderByDescending(x => x.Status == SqlOSOrganizationDomainStatuses.Active)
            .ThenByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task EnsureNoActiveClaimElsewhereAsync(
        string organizationId,
        string domain,
        CancellationToken cancellationToken)
    {
        var exists = await _context.Set<SqlOSOrganizationDomain>()
            .AsNoTracking()
            .AnyAsync(x => x.OrganizationId != organizationId
                && x.Domain == domain
                && x.Status == SqlOSOrganizationDomainStatuses.Active
                && x.RevokedAt == null, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException("Domain is already verified by another organization.");
        }
    }

    private async Task<bool> IsPrimaryDomainInUseElsewhereAsync(
        string organizationId,
        string domain,
        CancellationToken cancellationToken)
        => await _context.Set<SqlOSOrganization>()
            .AsNoTracking()
            .AnyAsync(x => x.Id != organizationId && x.PrimaryDomain == domain, cancellationToken);

    private async Task RecordAuditAsync(
        string eventType,
        SqlOSOrganizationDomain domain,
        HttpContext? httpContext,
        object? data,
        CancellationToken cancellationToken)
    {
        await _adminService.RecordAuditAsync(
            eventType,
            "organization_domain",
            domain.Id,
            userId: domain.CreatedByUserId,
            organizationId: domain.OrganizationId,
            ipAddress: httpContext?.Connection.RemoteIpAddress?.ToString(),
            data: data,
            cancellationToken: cancellationToken);
    }
}

internal sealed record SqlOSOrganizationDomainOwnershipCheck(
    string OrganizationId,
    string DomainId,
    string Domain,
    string? VerificationToken,
    bool Verified);
