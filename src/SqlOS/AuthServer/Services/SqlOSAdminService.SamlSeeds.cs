using System.Data;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed partial class SqlOSAdminService
{
    public async Task UpsertSeededSamlConnectionsAsync(CancellationToken cancellationToken = default)
    {
        if (_options.SamlConnectionSeeds.Count == 0
            && !await _context.Set<SqlOSSsoConnection>().AnyAsync(
                x => x.ConfigurationOwner == SqlOSConfigurationOwners.Code,
                cancellationToken))
        {
            return;
        }

        await RunSamlSeedAtomicAsync(() => UpsertSeededSamlConnectionsCoreAsync(cancellationToken), cancellationToken);
    }

    private async Task UpsertSeededSamlConnectionsCoreAsync(CancellationToken cancellationToken)
    {
        var seeds = new List<ResolvedSamlSeed>();
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in _options.SamlConnectionSeeds)
        {
            var sourceKey = RequireSamlSeedValue(seed.Key, "SAML seed key is required.", 160);
            if (!sourceKeys.Add(sourceKey))
            {
                throw new InvalidOperationException($"SAML seed '{sourceKey}' is configured more than once.");
            }

            var organization = await ResolveSamlSeedOrganizationAsync(seed, cancellationToken);
            var definition = NormalizeSamlConnection(
                seed.MetadataXml,
                seed.IdentityProviderEntityId,
                seed.SingleSignOnUrl,
                seed.X509CertificatePem);
            seeds.Add(new ResolvedSamlSeed(seed, sourceKey, organization, definition));
        }

        var now = DateTime.UtcNow;
        var outcomes = new List<(string Id, string Key, string Outcome, string? Fingerprint, string OrganizationId)>();
        var orphans = await _context.Set<SqlOSSsoConnection>()
            .Where(x => x.ConfigurationOwner == SqlOSConfigurationOwners.Code
                && x.ConfigurationSourceKey != null
                && !sourceKeys.Contains(x.ConfigurationSourceKey))
            .ToListAsync(cancellationToken);
        foreach (var orphan in orphans)
        {
            if (orphan.ConfigurationOrphanedAt == null)
            {
                orphan.ConfigurationOrphanedAt = now;
                outcomes.Add((orphan.Id, orphan.ConfigurationSourceKey!, "orphaned", orphan.ConfigurationFingerprint, orphan.OrganizationId));
            }
        }

        foreach (var resolved in seeds)
        {
            var seed = resolved.Seed;
            var displayName = RequireSamlSeedValue(seed.DisplayName, $"SAML seed '{resolved.SourceKey}' requires a display name.", 200);
            var existing = await _context.Set<SqlOSSsoConnection>()
                .FirstOrDefaultAsync(x => x.ConfigurationSourceKey == resolved.SourceKey, cancellationToken);
            var conflict = await _context.Set<SqlOSSsoConnection>()
                .FirstOrDefaultAsync(x => x.Id != (existing == null ? string.Empty : existing.Id)
                    && x.IdentityProviderEntityId == resolved.Definition.IdentityProviderEntityId,
                    cancellationToken);
            if (existing == null && conflict != null)
            {
                throw new InvalidOperationException(
                    $"Cannot reconcile SAML seed '{resolved.SourceKey}' because an existing '{conflict.ConfigurationOwner}' connection uses the same IdP entity ID.");
            }
            if (existing != null)
            {
                SqlOSConfigurationOwnershipPolicy.EnsureCodeOwnership(
                    existing.ConfigurationOwner,
                    existing.ConfigurationSourceKey,
                    resolved.SourceKey,
                    $"SAML connection '{displayName}'");
            }

            if (!string.IsNullOrWhiteSpace(seed.PrimaryDomain))
            {
                var domain = NormalizeDomain(seed.PrimaryDomain);
                if (string.IsNullOrWhiteSpace(resolved.Organization.PrimaryDomain))
                {
                    resolved.Organization.PrimaryDomain = domain;
                }
                else if (!string.Equals(resolved.Organization.PrimaryDomain, domain, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"SAML seed '{resolved.SourceKey}' cannot replace organization '{resolved.Organization.Id}' primary domain '{resolved.Organization.PrimaryDomain}'. Change it explicitly in the dashboard or API first.");
                }
            }

            var acceptedContexts = NormalizeTrustValues(seed.AcceptedAuthnContextClassRefs);
            var fingerprint = SqlOSConfigurationOwnershipPolicy.Fingerprint(new
            {
                resolved.SourceKey,
                OrganizationId = resolved.Organization.Id,
                DisplayName = displayName,
                resolved.Definition.IdentityProviderEntityId,
                resolved.Definition.SingleSignOnUrl,
                resolved.Definition.X509CertificatePem,
                seed.PrimaryDomain,
                seed.AutoProvisionUsers,
                seed.AutoLinkByEmail,
                seed.NameIdFormat,
                seed.EmailAttributeName,
                seed.FirstNameAttributeName,
                seed.LastNameAttributeName,
                seed.TrustUpstreamMfa,
                AcceptedAuthnContextClassRefs = acceptedContexts,
                seed.IsEnabled
            });
            var outcome = existing == null
                ? "created"
                : existing.ConfigurationFingerprint == fingerprint && existing.ConfigurationOrphanedAt == null ? null : "updated";

            var isNew = existing == null;
            existing ??= new SqlOSSsoConnection
            {
                Id = _cryptoService.GenerateId("sso"),
                CreatedAt = now,
                IsEnabled = seed.IsEnabled,
                ConfigurationOwner = SqlOSConfigurationOwners.Code,
                ConfigurationSourceKey = resolved.SourceKey
            };
            if (isNew)
            {
                _context.Set<SqlOSSsoConnection>().Add(existing);
            }

            existing.OrganizationId = resolved.Organization.Id;
            existing.DisplayName = displayName;
            existing.IdentityProviderEntityId = resolved.Definition.IdentityProviderEntityId;
            existing.SingleSignOnUrl = resolved.Definition.SingleSignOnUrl;
            existing.X509CertificatePem = resolved.Definition.X509CertificatePem;
            existing.AutoProvisionUsers = seed.AutoProvisionUsers;
            existing.AutoLinkByEmail = seed.AutoLinkByEmail;
            existing.NameIdFormat = NormalizeOptionalSamlValue(seed.NameIdFormat, 200);
            existing.EmailAttributeName = NormalizeSamlAttribute(seed.EmailAttributeName, "email");
            existing.FirstNameAttributeName = NormalizeSamlAttribute(seed.FirstNameAttributeName, "first_name");
            existing.LastNameAttributeName = NormalizeSamlAttribute(seed.LastNameAttributeName, "last_name");
            existing.TrustUpstreamMfa = seed.TrustUpstreamMfa;
            existing.AcceptedAuthnContextClassRefsJson = JsonSerializer.Serialize(acceptedContexts);
            existing.ConfigurationFingerprint = fingerprint;
            existing.LastReconciledAt = now;
            existing.ConfigurationOrphanedAt = null;
            existing.UpdatedAt = now;
            // Preserve an operator emergency disable. Code can disable, but does not
            // silently re-enable an existing enterprise connection on restart.
            if (!seed.IsEnabled) existing.IsEnabled = false;
            if (outcome != null) outcomes.Add((existing.Id, resolved.SourceKey, outcome, fingerprint, resolved.Organization.Id));
        }

        await _context.SaveChangesAsync(cancellationToken);
        foreach (var outcome in outcomes)
        {
            await RecordAuditAsync(
                "configuration.reconciled",
                "system",
                "startup",
                organizationId: outcome.OrganizationId,
                data: new
                {
                    resourceType = "saml_connection",
                    resourceId = outcome.Id,
                    owner = SqlOSConfigurationOwners.Code,
                    sourceKey = outcome.Key,
                    outcome = outcome.Outcome,
                    fingerprint = outcome.Fingerprint
                },
                cancellationToken: cancellationToken);
        }
    }

    private async Task RunSamlSeedAtomicAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational())
        {
            await action();
            return;
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        var attempt = 0;
        await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0 && _context is DbContext retryContext) retryContext.ChangeTracker.Clear();
            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
            {
                const string resource = "SqlOS:SamlSeedReconciliation";
                await _context.Database.ExecuteSqlInterpolatedAsync($"""
                    DECLARE @result int;
                    EXEC @result = sys.sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
                    IF @result < 0 THROW 51000, 'Could not acquire the SAML seed reconciliation lock.', 1;
                    """, cancellationToken);
            }
            await action();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task<SqlOSOrganization> ResolveSamlSeedOrganizationAsync(
        SqlOSSamlConnectionSeedOptions seed,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(seed.OrganizationId))
        {
            var id = seed.OrganizationId.Trim();
            return await _context.Set<SqlOSOrganization>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new InvalidOperationException($"Seeded SAML organization '{id}' was not found.");
        }

        var slug = RequireSamlSeedValue(seed.OrganizationSlug, $"SAML seed '{seed.Key}' requires OrganizationId or OrganizationSlug.", 160);
        return await _context.Set<SqlOSOrganization>().FirstOrDefaultAsync(x => x.Slug == slug, cancellationToken)
            ?? throw new InvalidOperationException($"Seeded SAML organization slug '{slug}' was not found.");
    }

    private static NormalizedSamlConnection NormalizeSamlConnection(
        string? metadataXml,
        string? identityProviderEntityId,
        string? singleSignOnUrl,
        string? certificatePem)
    {
        if (!string.IsNullOrWhiteSpace(metadataXml))
        {
            if (!string.IsNullOrWhiteSpace(identityProviderEntityId)
                || !string.IsNullOrWhiteSpace(singleSignOnUrl)
                || !string.IsNullOrWhiteSpace(certificatePem))
            {
                throw new InvalidOperationException("Configure SAML federation metadata XML or explicit IdP fields, not both.");
            }
            var metadata = ParseFederationMetadata(metadataXml);
            identityProviderEntityId = metadata.IdentityProviderEntityId;
            singleSignOnUrl = metadata.SingleSignOnUrl;
            certificatePem = metadata.X509CertificatePem;
        }

        var entityId = RequireSamlSeedValue(identityProviderEntityId, "SAML IdP entity ID is required.", 500);
        var ssoUrl = RequireSamlSeedValue(singleSignOnUrl, "SAML SSO URL is required.", 2000);
        if (!Uri.TryCreate(ssoUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("SAML SSO URL must be an absolute HTTPS URL.");
        }

        var rawCertificate = RequireSamlSeedValue(certificatePem, "SAML signing certificate is required.", 20_000);
        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPem(rawCertificate);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("SAML signing certificate is malformed.", exception);
        }
        using (certificate)
        {
            var now = DateTime.UtcNow;
            if (certificate.NotBefore.ToUniversalTime() > now.AddMinutes(5))
                throw new InvalidOperationException("SAML signing certificate is not valid yet.");
            if (certificate.NotAfter.ToUniversalTime() <= now)
                throw new InvalidOperationException("SAML signing certificate is expired.");
            rawCertificate = ToPem(certificate.Export(X509ContentType.Cert));
        }

        return new NormalizedSamlConnection(entityId, uri.AbsoluteUri, rawCertificate);
    }

    private static string RequireSamlSeedValue(string? value, string message, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value.Trim();
        if (normalized.Length > maxLength) throw new InvalidOperationException(message.Replace("required", $"limited to {maxLength} characters", StringComparison.OrdinalIgnoreCase));
        return normalized;
    }

    private static string NormalizeSamlAttribute(string? value, string fallback)
        => RequireSamlSeedValue(string.IsNullOrWhiteSpace(value) ? fallback : value, "SAML attribute name is required.", 500);

    private static string? NormalizeOptionalSamlValue(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) ? null : RequireSamlSeedValue(value, "SAML value is required.", maxLength);

    private sealed record NormalizedSamlConnection(
        string IdentityProviderEntityId,
        string SingleSignOnUrl,
        string X509CertificatePem);

    private sealed record ResolvedSamlSeed(
        SqlOSSamlConnectionSeedOptions Seed,
        string SourceKey,
        SqlOSOrganization Organization,
        NormalizedSamlConnection Definition);
}
