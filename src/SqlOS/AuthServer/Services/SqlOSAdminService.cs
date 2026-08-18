using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Fga.Models;
using SqlOS.Pagination;

namespace SqlOS.AuthServer.Services;

public sealed partial class SqlOSAdminService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSClientResolutionService _clientResolutionService;

    public SqlOSAdminService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCryptoService cryptoService)
        : this(context, options, cryptoService, new SqlOSClientResolutionService(context, options))
    {
    }

    public SqlOSAdminService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCryptoService cryptoService,
        SqlOSClientResolutionService clientResolutionService)
    {
        _context = context;
        _options = options.Value;
        _cryptoService = cryptoService;
        _clientResolutionService = clientResolutionService;
    }

    public async Task CleanupExpiredTemporaryTokensAsync(CancellationToken cancellationToken = default)
    {
        var expired = await _context.Set<SqlOSTemporaryToken>()
            .Where(x => x.ExpiresAt < DateTime.UtcNow || x.ConsumedAt != null)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
        {
            return;
        }

        _context.Set<SqlOSTemporaryToken>().RemoveRange(expired);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task CleanupExpiredEmailOtpChallengesAsync(CancellationToken cancellationToken = default)
    {
        var expired = await _context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.ExpiresAt < DateTime.UtcNow || x.ConsumedAt != null || x.InvalidatedAt != null)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
        {
            return;
        }

        _context.Set<SqlOSEmailOtpChallenge>().RemoveRange(expired);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task CleanupExpiredPhoneOtpChallengesAsync(CancellationToken cancellationToken = default)
    {
        var expired = await _context.Set<SqlOSPhoneOtpChallenge>()
            .Where(x => x.ExpiresAt < DateTime.UtcNow || x.ConsumedAt != null || x.InvalidatedAt != null)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
        {
            return;
        }

        _context.Set<SqlOSPhoneOtpChallenge>().RemoveRange(expired);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task CleanupExpiredRefreshTokensAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var configuredGraceWindowSeconds = await _context.Set<SqlOSSettings>()
            .Where(x => x.Id == "default")
            .Select(x => (int?)x.RefreshTokenGraceWindowSeconds)
            .FirstOrDefaultAsync(cancellationToken)
            ?? _options.RefreshTokenGraceWindowSeconds;
        var staleResponseCutoff = now.AddSeconds(-Math.Max(0, configuredGraceWindowSeconds));

        // Keep consumed token hashes until their normal expiry so a later
        // replay can still identify and revoke the family. Only the
        // recoverable retry response is removed after the grace window.
        var staleResponses = await _context.Set<SqlOSRefreshToken>()
            .Where(x => x.ConsumedAt != null
                && x.ConsumedAt <= staleResponseCutoff
                && x.ReplacementTokenResponse != null)
            .ToListAsync(cancellationToken);
        foreach (var token in staleResponses)
        {
            token.ReplacementTokenResponse = null;
            token.ReplacementOrganizationId = null;
            token.ReplacementAccessTokenExpiresAt = null;
        }

        var expired = await _context.Set<SqlOSRefreshToken>()
            .Where(x => x.ExpiresAt < now || x.RevokedAt != null)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0 && staleResponses.Count == 0)
        {
            return;
        }

        _context.Set<SqlOSRefreshToken>().RemoveRange(expired);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertSeededClientsAsync(CancellationToken cancellationToken = default)
    {
        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
        {
            await UpsertSeededClientsCoreAsync(cancellationToken);
            return;
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        var attempt = 0;
        await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0 && _context is DbContext retryContext)
            {
                retryContext.ChangeTracker.Clear();
            }
            await using var transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
            if (string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
            {
                await _context.Database.ExecuteSqlRawAsync("""
                    DECLARE @result int;
                    EXEC @result = sys.sp_getapplock
                        @Resource = N'SqlOS:ClientSeedReconciliation',
                        @LockMode = N'Exclusive',
                        @LockOwner = N'Transaction',
                        @LockTimeout = 30000;
                    IF @result < 0 THROW 51000, 'Could not acquire the SqlOS client seed reconciliation lock.', 1;
                    """, cancellationToken);
            }
            await UpsertSeededClientsCoreAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task UpsertSeededClientsCoreAsync(CancellationToken cancellationToken)
    {
        var seeds = BuildStartupClientSeeds();
        var sourceKeys = seeds.Select(x => x.ClientId.Trim()).ToHashSet(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var auditOutcomes = new List<(string ResourceId, string SourceKey, string Outcome, string? Fingerprint)>();
        var orphans = await _context.Set<SqlOSClientApplication>()
            .Where(x => x.ConfigurationOwner == SqlOSConfigurationOwners.Code && x.ConfigurationSourceKey != null && !sourceKeys.Contains(x.ConfigurationSourceKey))
            .ToListAsync(cancellationToken);
        foreach (var orphan in orphans)
        {
            if (orphan.ConfigurationOrphanedAt == null)
            {
                orphan.ConfigurationOrphanedAt = now;
                auditOutcomes.Add((orphan.Id, orphan.ConfigurationSourceKey!, "orphaned", orphan.ConfigurationFingerprint));
            }
        }

        if (seeds.Count == 0 && orphans.Count == 0)
        {
            return;
        }

        foreach (var seed in seeds)
        {
            var normalized = NormalizeSeededClient(seed);
            var sourceKey = normalized.ClientId;
            var existing = await _context.Set<SqlOSClientApplication>()
                .FirstOrDefaultAsync(x => x.ClientId == normalized.ClientId, cancellationToken);
            if (seed.Assignments.Count > 0 && string.IsNullOrWhiteSpace(seed.AccessMode))
            {
                throw new InvalidOperationException($"Client '{normalized.ClientId}' must set AccessMode explicitly when declaring application assignments.");
            }
            var accessMode = string.IsNullOrWhiteSpace(seed.AccessMode)
                ? existing == null ? SqlOSApplicationAccessModes.AllOrganizations : NormalizeAccessMode(existing.AccessMode)
                : NormalizeAccessMode(seed.AccessMode);
            var assignmentFingerprint = seed.Assignments
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new { x.Key, x.PrincipalType, x.PrincipalId, x.OrganizationIdOrSlug, x.RoleKey, x.Access, x.Description });
            var fingerprint = SqlOSConfigurationOwnershipPolicy.Fingerprint(new { normalized.ClientId, normalized.Name, normalized.Description, normalized.Audience, normalized.ClientType, normalized.RequirePkce, normalized.AllowedScopes, normalized.IsFirstParty, normalized.AllowNativeHeadlessAuth, normalized.AllowDeviceAuthorization, normalized.EnableClientCredentials, normalized.RedirectUris, normalized.IsActive, AccessMode = accessMode, Assignments = assignmentFingerprint });
            var outcome = existing == null ? "created" : existing.ConfigurationFingerprint == fingerprint && existing.ConfigurationOrphanedAt == null ? null : "updated";

            if (existing != null && string.Equals(existing.RegistrationSource, "seeded", StringComparison.OrdinalIgnoreCase) && existing.ConfigurationSourceKey == null)
            {
                existing.ConfigurationOwner = SqlOSConfigurationOwners.Code;
                existing.ConfigurationSourceKey = sourceKey;
            }
            if (existing != null)
            {
                SqlOSConfigurationOwnershipPolicy.EnsureCodeOwnership(existing.ConfigurationOwner, existing.ConfigurationSourceKey, sourceKey, $"OAuth client '{sourceKey}'");
            }

            if (existing == null)
            {
                _context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
                {
                    Id = _cryptoService.GenerateId("cli"),
                    ClientId = normalized.ClientId,
                    Name = normalized.Name,
                    Description = normalized.Description,
                    Audience = normalized.Audience,
                    ClientType = normalized.ClientType,
                    RegistrationSource = "seeded",
                    ConfigurationOwner = SqlOSConfigurationOwners.Code,
                    ConfigurationSourceKey = sourceKey,
                    ConfigurationFingerprint = fingerprint,
                    LastReconciledAt = now,
                    TokenEndpointAuthMethod = ResolveTokenEndpointAuthMethod(normalized.ClientType),
                    GrantTypesJson = JsonSerializer.Serialize(normalized.GrantTypes),
                    ResponseTypesJson = JsonSerializer.Serialize(new[] { "code" }),
                    RequirePkce = normalized.RequirePkce,
                    AllowedScopesJson = JsonSerializer.Serialize(normalized.AllowedScopes),
                    AllowNativeHeadlessAuth = normalized.AllowNativeHeadlessAuth,
                    AllowDeviceAuthorization = normalized.AllowDeviceAuthorization,
                    RedirectUrisJson = JsonSerializer.Serialize(normalized.RedirectUris),
                    CreatedAt = DateTime.UtcNow,
                    IsFirstParty = normalized.IsFirstParty,
                    IsActive = normalized.IsActive && accessMode != SqlOSApplicationAccessModes.Disabled,
                    AccessMode = accessMode,
                    DisabledAt = accessMode == SqlOSApplicationAccessModes.Disabled ? now : null,
                    DisabledReason = accessMode == SqlOSApplicationAccessModes.Disabled ? "application_access_disabled" : null
                });
                auditOutcomes.Add((sourceKey, sourceKey, "created", fingerprint));
                continue;
            }

            existing.Name = normalized.Name;
            existing.Description = normalized.Description;
            existing.Audience = normalized.Audience;
            existing.ClientType = normalized.ClientType;
            existing.RegistrationSource = "seeded";
            existing.ConfigurationFingerprint = fingerprint;
            existing.LastReconciledAt = now;
            existing.ConfigurationOrphanedAt = null;
            if (outcome != null) auditOutcomes.Add((existing.Id, sourceKey, outcome, fingerprint));
            existing.TokenEndpointAuthMethod = ResolveTokenEndpointAuthMethod(normalized.ClientType);
            existing.GrantTypesJson = JsonSerializer.Serialize(normalized.GrantTypes);
            existing.ResponseTypesJson = string.IsNullOrWhiteSpace(existing.ResponseTypesJson)
                ? JsonSerializer.Serialize(new[] { "code" })
                : existing.ResponseTypesJson;
            existing.RequirePkce = normalized.RequirePkce;
            existing.AllowedScopesJson = JsonSerializer.Serialize(normalized.AllowedScopes);
            existing.AllowNativeHeadlessAuth = normalized.AllowNativeHeadlessAuth;
            existing.AllowDeviceAuthorization = normalized.AllowDeviceAuthorization;
            existing.RedirectUrisJson = JsonSerializer.Serialize(normalized.RedirectUris);
            existing.IsFirstParty = normalized.IsFirstParty;
            await ApplySeededApplicationAccessModeAsync(existing, accessMode, normalized.IsActive, cancellationToken);
            if (existing.DisabledAt != null)
            {
                existing.IsActive = false;
            }
            else
            {
                existing.IsActive = normalized.IsActive;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        await ReconcileSeededClientCredentialsAsync(seeds, now, cancellationToken);
        await ReconcileSeededApplicationAssignmentsAsync(seeds, now, cancellationToken);
        foreach (var audit in auditOutcomes)
        {
            await RecordAuditAsync("configuration.reconciled", "system", "startup", data: new { resourceType = "oauth_client", resourceId = audit.ResourceId, owner = SqlOSConfigurationOwners.Code, sourceKey = audit.SourceKey, outcome = audit.Outcome, fingerprint = audit.Fingerprint }, cancellationToken: cancellationToken);
        }
    }

    private async Task ReconcileSeededClientCredentialsAsync(
        IReadOnlyList<SqlOSClientSeedOptions> seeds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var seed in seeds)
        {
            var sourceKey = seed.ClientId.Trim();
            var client = await _context.Set<SqlOSClientApplication>()
                .SingleAsync(x => x.ClientId == sourceKey, cancellationToken);
            var secretResolver = seed.ClientSecretResolver ?? seed.MachineClient?.SecretResolver;
            var hashResolver = seed.ClientSecretHashResolver ?? seed.MachineClient?.SecretHashResolver;
            var configuredResolverCount = (seed.ClientSecretResolver == null ? 0 : 1)
                + (seed.ClientSecretHashResolver == null ? 0 : 1)
                + (seed.MachineClient?.SecretResolver == null ? 0 : 1)
                + (seed.MachineClient?.SecretHashResolver == null ? 0 : 1);
            var credential = await _context.Set<SqlOSClientCredential>()
                .SingleOrDefaultAsync(x => x.ClientApplicationId == client.Id
                    && x.ConfigurationOwner == SqlOSConfigurationOwners.Code
                    && x.ConfigurationSourceKey == "primary", cancellationToken);

            if (!string.Equals(client.TokenEndpointAuthMethod, "client_secret_basic", StringComparison.Ordinal))
            {
                if (configuredResolverCount != 0)
                {
                    throw new InvalidOperationException($"Public client '{sourceKey}' cannot declare a client-secret resolver.");
                }
                if (credential != null && credential.RevokedAt == null)
                {
                    credential.RevokedAt = now;
                    credential.LastReconciledAt = now;
                }
                continue;
            }

            if (configuredResolverCount != 1 || (secretResolver == null) == (hashResolver == null))
            {
                throw new InvalidOperationException($"Confidential client '{sourceKey}' requires exactly one client-secret or client-secret-hash resolver.");
            }

            string secretHash;
            if (hashResolver != null)
            {
                secretHash = hashResolver()?.Trim()
                    ?? throw new InvalidOperationException($"Confidential client '{sourceKey}' secret-hash resolver returned no value.");
                if (!IsSupportedPasswordHash(secretHash))
                {
                    throw new InvalidOperationException($"Confidential client '{sourceKey}' secret-hash resolver returned an unsupported PasswordHasher payload.");
                }
            }
            else
            {
                var secret = secretResolver!()?.Trim();
                if (string.IsNullOrWhiteSpace(secret) || secret.Length is < 43 or > 256)
                {
                    throw new InvalidOperationException($"Confidential client '{sourceKey}' secret resolver must return 43 to 256 characters.");
                }
                secretHash = credential != null && _cryptoService.VerifyPassword(credential.SecretHash, secret)
                    ? credential.SecretHash
                    : _cryptoService.HashPassword(secret);
            }

            if (credential == null)
            {
                _context.Set<SqlOSClientCredential>().Add(new SqlOSClientCredential
                {
                    Id = _cryptoService.GenerateId("clcred"),
                    ClientApplicationId = client.Id,
                    SecretHash = secretHash,
                    DisplayName = "Code-owned primary credential",
                    CreatedAt = now,
                    ConfigurationOwner = SqlOSConfigurationOwners.Code,
                    ConfigurationSourceKey = "primary",
                    LastReconciledAt = now
                });
            }
            else
            {
                credential.SecretHash = secretHash;
                credential.RevokedAt = null;
                credential.LastReconciledAt = now;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static bool IsSupportedPasswordHash(string hash)
    {
        try
        {
            var payload = Convert.FromBase64String(hash);
            return payload.Length >= 13 && payload[0] is 0x00 or 0x01;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task UpsertSeededOidcConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var sourceKeys = _options.OidcConnectionSeeds.Select(ResolveOidcSeedKey).ToHashSet(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var auditOutcomes = new List<(string ResourceId, string SourceKey, string Outcome, string? Fingerprint)>();
        var orphans = await _context.Set<SqlOSOidcConnection>()
            .Where(x => x.ConfigurationOwner == SqlOSConfigurationOwners.Code && x.ConfigurationSourceKey != null && !sourceKeys.Contains(x.ConfigurationSourceKey))
            .ToListAsync(cancellationToken);
        foreach (var orphan in orphans)
        {
            if (orphan.ConfigurationOrphanedAt == null)
            {
                orphan.ConfigurationOrphanedAt = now;
                auditOutcomes.Add((orphan.Id, orphan.ConfigurationSourceKey!, "orphaned", orphan.ConfigurationFingerprint));
            }
        }
        if (_options.OidcConnectionSeeds.Count == 0 && orphans.Count == 0)
        {
            return;
        }

        foreach (var seed in _options.OidcConnectionSeeds)
        {
            var displayName = (seed.DisplayName ?? string.Empty).Trim();
            var sourceKey = ResolveOidcSeedKey(seed);

            var existing = await _context.Set<SqlOSOidcConnection>().FirstOrDefaultAsync(x => x.ConfigurationSourceKey == sourceKey, cancellationToken);
            var conflicting = await _context.Set<SqlOSOidcConnection>().FirstOrDefaultAsync(x => x.Id != (existing == null ? string.Empty : existing.Id)
                && (seed.ProviderType == SqlOSOidcProviderType.Custom ? x.ProviderType == SqlOSOidcProviderType.Custom && x.DisplayName == displayName : x.ProviderType == seed.ProviderType), cancellationToken);
            if (existing == null && conflicting != null)
            {
                throw new InvalidOperationException($"Cannot reconcile OIDC connection '{displayName}' from code because an existing '{conflicting.ConfigurationOwner}' connection uses the same provider identity. Remove or rename the conflicting record explicitly.");
            }
            if (existing != null) SqlOSConfigurationOwnershipPolicy.EnsureCodeOwnership(existing.ConfigurationOwner, existing.ConfigurationSourceKey, sourceKey, $"OIDC connection '{displayName}'");

            var connectionId = existing?.Id ?? _cryptoService.GenerateId("oidc");
            var callbacks = NormalizeCallbackUris(seed.AllowedCallbackUris, connectionId);
            if (callbacks.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Seeded OIDC connection '{(string.IsNullOrWhiteSpace(displayName) ? seed.ProviderType.ToString() : displayName)}' requires at least one callback URI.");
            }

            var normalized = NormalizeOidcConfiguration(
                seed.ProviderType,
                seed.UseDiscovery,
                seed.DiscoveryUrl,
                seed.Issuer,
                seed.AuthorizationEndpoint,
                seed.TokenEndpoint,
                seed.UserInfoEndpoint,
                seed.JwksUri,
                seed.MicrosoftTenant,
                seed.Scopes,
                seed.ClaimMapping,
                seed.ClientAuthMethod,
                seed.UseUserInfo,
                seed.AppleTeamId,
                seed.AppleKeyId);
            var fingerprint = SqlOSConfigurationOwnershipPolicy.Fingerprint(new { SourceKey = sourceKey, seed.ProviderType, DisplayName = displayName, seed.ClientId, Callbacks = callbacks, normalized.Protocol, normalized.UseDiscovery, normalized.DiscoveryUrl, normalized.Issuer, normalized.AuthorizationEndpoint, normalized.TokenEndpoint, normalized.UserInfoEndpoint, normalized.JwksUri, normalized.MicrosoftTenant, normalized.Scopes, normalized.ClaimMapping, normalized.ClientAuthMethod, normalized.UseUserInfo, normalized.AppleTeamId, normalized.AppleKeyId, seed.TrustUpstreamMfa, AcceptedAmrValues = NormalizeTrustValues(seed.AcceptedAmrValues), AcceptedAcrValues = NormalizeTrustValues(seed.AcceptedAcrValues) });
            var outcome = existing == null ? "created" : existing.ConfigurationFingerprint == fingerprint && existing.ConfigurationOrphanedAt == null ? null : "updated";

            if (existing == null)
            {
                var connection = new SqlOSOidcConnection
                {
                    Id = connectionId,
                    ProviderType = seed.ProviderType,
                    Protocol = normalized.Protocol,
                    DisplayName = displayName,
                    LogoDataUrl = SqlOSOidcProviderLogoCatalog.NormalizeCustomLogoDataUrl(seed.LogoDataUrl),
                    ClientId = seed.ClientId.Trim(),
                    ClientSecretEncrypted = string.IsNullOrWhiteSpace(seed.ClientSecret) ? null : _cryptoService.ProtectSecret(seed.ClientSecret.Trim()),
                    AllowedCallbackUrisJson = JsonSerializer.Serialize(callbacks),
                    UseDiscovery = normalized.UseDiscovery,
                    DiscoveryUrl = normalized.DiscoveryUrl,
                    Issuer = normalized.Issuer,
                    AuthorizationEndpoint = normalized.AuthorizationEndpoint,
                    TokenEndpoint = normalized.TokenEndpoint,
                    UserInfoEndpoint = normalized.UserInfoEndpoint,
                    JwksUri = normalized.JwksUri,
                    MicrosoftTenant = normalized.MicrosoftTenant,
                    ScopesJson = JsonSerializer.Serialize(normalized.Scopes),
                    ClaimMappingJson = JsonSerializer.Serialize(normalized.ClaimMapping),
                    ClientAuthMethod = normalized.ClientAuthMethod,
                    UseUserInfo = normalized.UseUserInfo,
                    AppleTeamId = normalized.AppleTeamId,
                    AppleKeyId = normalized.AppleKeyId,
                    ApplePrivateKeyEncrypted = string.IsNullOrWhiteSpace(seed.ApplePrivateKeyPem) ? null : _cryptoService.ProtectSecret(NormalizePrivateKey(seed.ApplePrivateKeyPem)),
                    TrustUpstreamMfa = seed.TrustUpstreamMfa,
                    AcceptedAmrValuesJson = JsonSerializer.Serialize(NormalizeTrustValues(seed.AcceptedAmrValues)),
                    AcceptedAcrValuesJson = JsonSerializer.Serialize(NormalizeTrustValues(seed.AcceptedAcrValues)),
                    ConfigurationOwner = SqlOSConfigurationOwners.Code,
                    ConfigurationSourceKey = sourceKey,
                    ConfigurationFingerprint = fingerprint,
                    LastReconciledAt = now,
                    IsEnabled = seed.IsEnabled,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                ValidateOidcSecretRequirements(connection);
                _context.Set<SqlOSOidcConnection>().Add(connection);
                auditOutcomes.Add((connection.Id, sourceKey, "created", fingerprint));
                continue;
            }

            existing.DisplayName = displayName;
            existing.Protocol = normalized.Protocol;
            existing.LogoDataUrl = SqlOSOidcProviderLogoCatalog.NormalizeCustomLogoDataUrl(seed.LogoDataUrl);
            existing.ClientId = seed.ClientId.Trim();
            existing.AllowedCallbackUrisJson = JsonSerializer.Serialize(callbacks);
            existing.UseDiscovery = normalized.UseDiscovery;
            existing.DiscoveryUrl = normalized.DiscoveryUrl;
            existing.Issuer = normalized.Issuer;
            existing.AuthorizationEndpoint = normalized.AuthorizationEndpoint;
            existing.TokenEndpoint = normalized.TokenEndpoint;
            existing.UserInfoEndpoint = normalized.UserInfoEndpoint;
            existing.JwksUri = normalized.JwksUri;
            existing.MicrosoftTenant = normalized.MicrosoftTenant;
            existing.ScopesJson = JsonSerializer.Serialize(normalized.Scopes);
            existing.ClaimMappingJson = JsonSerializer.Serialize(normalized.ClaimMapping);
            existing.ClientAuthMethod = normalized.ClientAuthMethod;
            existing.UseUserInfo = normalized.UseUserInfo;
            existing.AppleTeamId = normalized.AppleTeamId;
            existing.AppleKeyId = normalized.AppleKeyId;
            existing.TrustUpstreamMfa = seed.TrustUpstreamMfa;
            existing.AcceptedAmrValuesJson = JsonSerializer.Serialize(NormalizeTrustValues(seed.AcceptedAmrValues));
            existing.AcceptedAcrValuesJson = JsonSerializer.Serialize(NormalizeTrustValues(seed.AcceptedAcrValues));
            existing.ConfigurationFingerprint = fingerprint;
            existing.LastReconciledAt = now;
            existing.ConfigurationOrphanedAt = null;
            if (outcome != null) auditOutcomes.Add((existing.Id, sourceKey, outcome, fingerprint));
            existing.UpdatedAt = DateTime.UtcNow;

            // Only overwrite secrets when the seed supplies them, so config without a secret
            // (e.g. a rotated value kept out of source) never clears an existing credential.
            if (!string.IsNullOrWhiteSpace(seed.ClientSecret))
            {
                existing.ClientSecretEncrypted = _cryptoService.ProtectSecret(seed.ClientSecret.Trim());
            }

            if (!string.IsNullOrWhiteSpace(seed.ApplePrivateKeyPem))
            {
                existing.ApplePrivateKeyEncrypted = _cryptoService.ProtectSecret(NormalizePrivateKey(seed.ApplePrivateKeyPem));
            }

            // Enable/disable is owner-managed once the connection exists: respect dashboard changes
            // across restarts instead of forcing the seeded value on every boot.
            ValidateOidcSecretRequirements(existing);
        }

        await _context.SaveChangesAsync(cancellationToken);
        foreach (var audit in auditOutcomes)
        {
            await RecordAuditAsync("configuration.reconciled", "system", "startup", data: new { resourceType = "oidc_connection", resourceId = audit.ResourceId, owner = SqlOSConfigurationOwners.Code, sourceKey = audit.SourceKey, outcome = audit.Outcome, fingerprint = audit.Fingerprint }, cancellationToken: cancellationToken);
        }
    }

    private static string ResolveOidcSeedKey(SqlOSOidcConnectionSeedOptions seed)
    {
        if (!string.IsNullOrWhiteSpace(seed.Key)) return seed.Key.Trim();
        if (seed.ProviderType != SqlOSOidcProviderType.Custom) return seed.ProviderType.ToString().ToLowerInvariant();
        throw new InvalidOperationException("Custom seeded OIDC connections require a stable key. Use SeedOidcConnection(key, configure).");
    }

    public async Task<SqlOSUser> CreateUserAsync(SqlOSCreateUserRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(request.Email);
        var existingEmail = await _context.Set<SqlOSUserEmail>()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
        if (existingEmail != null)
        {
            throw new InvalidOperationException($"Email '{request.Email}' already exists.");
        }

        var user = new SqlOSUser
        {
            Id = _cryptoService.GenerateId("usr"),
            DisplayName = request.DisplayName,
            DefaultEmail = request.Email,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var email = new SqlOSUserEmail
        {
            Id = _cryptoService.GenerateId("eml"),
            UserId = user.Id,
            Email = request.Email,
            NormalizedEmail = normalizedEmail,
            IsPrimary = true,
            IsVerified = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.Set<SqlOSUser>().Add(user);
        _context.Set<SqlOSUserEmail>().Add(email);

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            _context.Set<SqlOSCredential>().Add(new SqlOSCredential
            {
                Id = _cryptoService.GenerateId("cred"),
                UserId = user.Id,
                SecretHash = _cryptoService.HashPassword(request.Password),
                Type = "password",
                CreatedAt = DateTime.UtcNow
            });
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlOSSignupOrchestration.IsUniqueConstraintViolation(ex))
        {
            throw new InvalidOperationException($"Email '{request.Email}' already exists.", ex);
        }

        return user;
    }

    public async Task<SqlOSOrganization> CreateOrganizationAsync(SqlOSCreateOrganizationRequest request, CancellationToken cancellationToken = default)
    {
        var slug = string.IsNullOrWhiteSpace(request.Slug) ? Slugify(request.Name) : Slugify(request.Slug);
        var exists = await _context.Set<SqlOSOrganization>().AnyAsync(x => x.Slug == slug, cancellationToken);
        if (exists)
        {
            slug = $"{slug}-{Guid.NewGuid():N}"[..Math.Min(slug.Length + 9, 120)];
        }

        var organization = new SqlOSOrganization
        {
            Id = _cryptoService.GenerateId("org"),
            Name = request.Name,
            Slug = slug,
            PrimaryDomain = NormalizeDomain(request.PrimaryDomain),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _context.Set<SqlOSOrganization>().Add(organization);
        await _context.SaveChangesAsync(cancellationToken);
        return organization;
    }

    public async Task<SqlOSOrganization> UpdateOrganizationAsync(
        string organizationId,
        SqlOSUpdateOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
        {
            await SqlOSSsoPortalOrganizationLock.AcquireAsync(_context, organizationId, cancellationToken);
            return await UpdateOrganizationCoreAsync(organizationId, request, cancellationToken);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0 && _context is DbContext retryContext)
            {
                retryContext.ChangeTracker.Clear();
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);
            await SqlOSSsoPortalOrganizationLock.AcquireAsync(_context, organizationId, cancellationToken);
            var updated = await UpdateOrganizationCoreAsync(organizationId, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return updated;
        });
    }

    private async Task<SqlOSOrganization> UpdateOrganizationCoreAsync(
        string organizationId,
        SqlOSUpdateOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        if (_context is DbContext dbContext)
        {
            var trackedOrganization = dbContext.ChangeTracker
                .Entries<SqlOSOrganization>()
                .FirstOrDefault(x => x.Entity.Id == organizationId);
            if (trackedOrganization != null)
            {
                await trackedOrganization.ReloadAsync(cancellationToken);
            }
        }

        var organization = await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(x => x.Id == organizationId, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");
        var isDeactivating = organization.IsActive && !request.IsActive;
        var isReactivating = !organization.IsActive && request.IsActive;

        var slug = string.IsNullOrWhiteSpace(request.Slug) ? Slugify(request.Name) : Slugify(request.Slug);
        var slugExists = await _context.Set<SqlOSOrganization>()
            .AnyAsync(x => x.Id != organizationId && x.Slug == slug, cancellationToken);
        if (slugExists)
        {
            slug = $"{slug}-{Guid.NewGuid():N}"[..Math.Min(slug.Length + 9, 120)];
        }

        organization.Name = request.Name.Trim();
        organization.Slug = slug;
        organization.PrimaryDomain = NormalizeDomain(request.PrimaryDomain);
        organization.IsActive = request.IsActive;

        if (isDeactivating)
        {
            var now = DateTime.UtcNow;
            await SqlOSAuthLifecyclePolicy.RevokeAsync(
                _context,
                userId: null,
                organizationId: organization.Id,
                reason: "organization_deactivated",
                now: now,
                cancellationToken: cancellationToken);
        }

        if (isDeactivating || isReactivating)
        {
            var now = DateTime.UtcNow;
            var revocationReason = isDeactivating
                ? "organization_deactivated"
                : "organization_reactivated";
            var portalSessions = await _context.Set<SqlOSSsoPortalSession>()
                .Where(x => x.OrganizationId == organization.Id && x.RevokedAt == null)
                .ToListAsync(cancellationToken);
            foreach (var portalSession in portalSessions)
            {
                portalSession.RevokedAt = now;
                portalSession.RevokedReason = revocationReason;
            }

            await RecordAuditAsync(
                "sso.portal.sessions.revoked",
                "admin",
                actorId: null,
                organizationId: organization.Id,
                data: new
                {
                    reason = revocationReason,
                    revokedSessions = portalSessions.Count
                },
                cancellationToken: cancellationToken);
        }
        else
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        return organization;
    }

    public async Task<SqlOSMembership> CreateMembershipAsync(string organizationId, SqlOSCreateMembershipRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await _context.Set<SqlOSMembership>().FirstOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.UserId == request.UserId, cancellationToken);
        if (existing != null)
        {
            existing.IsActive = true;
            existing.Role = request.Role;
            await _context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var membership = new SqlOSMembership
        {
            OrganizationId = organizationId,
            UserId = request.UserId,
            Role = request.Role,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _context.Set<SqlOSMembership>().Add(membership);
        await _context.SaveChangesAsync(cancellationToken);
        return membership;
    }

    public async Task<SqlOSClientApplication> CreateClientAsync(SqlOSCreateClientRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeClientRequest(request);

        if (await _context.Set<SqlOSClientApplication>().AnyAsync(x => x.ClientId == normalized.ClientId, cancellationToken))
        {
            throw new InvalidOperationException($"Client '{normalized.ClientId}' already exists.");
        }

        var client = new SqlOSClientApplication
        {
            Id = _cryptoService.GenerateId("cli"),
            ClientId = normalized.ClientId,
            Name = normalized.Name,
            Description = normalized.Description,
            Audience = normalized.Audience,
            ClientType = normalized.ClientType,
            RegistrationSource = "manual",
            TokenEndpointAuthMethod = ResolveTokenEndpointAuthMethod(normalized.ClientType),
            GrantTypesJson = JsonSerializer.Serialize(normalized.GrantTypes),
            ResponseTypesJson = JsonSerializer.Serialize(new[] { "code" }),
            RequirePkce = normalized.RequirePkce,
            AllowedScopesJson = JsonSerializer.Serialize(normalized.AllowedScopes),
            IsFirstParty = normalized.IsFirstParty,
            AllowNativeHeadlessAuth = normalized.AllowNativeHeadlessAuth,
            AllowDeviceAuthorization = normalized.AllowDeviceAuthorization,
            RedirectUrisJson = JsonSerializer.Serialize(normalized.RedirectUris),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _context.Set<SqlOSClientApplication>().Add(client);
        await _context.SaveChangesAsync(cancellationToken);
        return client;
    }

    public async Task<SqlOSOidcConnection> CreateOidcConnectionAsync(SqlOSCreateOidcConnectionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProviderType != SqlOSOidcProviderType.Custom &&
            await _context.Set<SqlOSOidcConnection>().AnyAsync(x => x.ProviderType == request.ProviderType, cancellationToken))
        {
            throw new InvalidOperationException($"An OIDC connection for provider '{request.ProviderType}' already exists.");
        }

        var connectionId = _cryptoService.GenerateId("oidc");
        var callbacks = NormalizeCallbackUris(request.AllowedCallbackUris, connectionId);
        if (callbacks.Count == 0)
        {
            throw new InvalidOperationException("At least one callback URI is required.");
        }

        var normalized = NormalizeOidcConfiguration(
            request.ProviderType,
            request.UseDiscovery,
            request.DiscoveryUrl,
            request.Issuer,
            request.AuthorizationEndpoint,
            request.TokenEndpoint,
            request.UserInfoEndpoint,
            request.JwksUri,
            request.MicrosoftTenant,
            request.Scopes,
            request.ClaimMapping,
            request.ClientAuthMethod,
            request.UseUserInfo,
            request.AppleTeamId,
            request.AppleKeyId);

        var connection = new SqlOSOidcConnection
        {
            Id = connectionId,
            ProviderType = request.ProviderType,
            Protocol = normalized.Protocol,
            DisplayName = request.DisplayName,
            LogoDataUrl = SqlOSOidcProviderLogoCatalog.NormalizeCustomLogoDataUrl(request.LogoDataUrl),
            ClientId = request.ClientId.Trim(),
            ClientSecretEncrypted = string.IsNullOrWhiteSpace(request.ClientSecret) ? null : _cryptoService.ProtectSecret(request.ClientSecret.Trim()),
            AllowedCallbackUrisJson = JsonSerializer.Serialize(callbacks),
            UseDiscovery = normalized.UseDiscovery,
            DiscoveryUrl = normalized.DiscoveryUrl,
            Issuer = normalized.Issuer,
            AuthorizationEndpoint = normalized.AuthorizationEndpoint,
            TokenEndpoint = normalized.TokenEndpoint,
            UserInfoEndpoint = normalized.UserInfoEndpoint,
            JwksUri = normalized.JwksUri,
            MicrosoftTenant = normalized.MicrosoftTenant,
            ScopesJson = JsonSerializer.Serialize(normalized.Scopes),
            ClaimMappingJson = JsonSerializer.Serialize(normalized.ClaimMapping),
            ClientAuthMethod = normalized.ClientAuthMethod,
            UseUserInfo = normalized.UseUserInfo,
            AppleTeamId = normalized.AppleTeamId,
            AppleKeyId = normalized.AppleKeyId,
            ApplePrivateKeyEncrypted = string.IsNullOrWhiteSpace(request.ApplePrivateKeyPem) ? null : _cryptoService.ProtectSecret(NormalizePrivateKey(request.ApplePrivateKeyPem)),
            TrustUpstreamMfa = request.TrustUpstreamMfa,
            AcceptedAmrValuesJson = JsonSerializer.Serialize(NormalizeTrustValues(request.AcceptedAmrValues)),
            AcceptedAcrValuesJson = JsonSerializer.Serialize(NormalizeTrustValues(request.AcceptedAcrValues)),
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        ValidateOidcSecretRequirements(connection);

        _context.Set<SqlOSOidcConnection>().Add(connection);
        await _context.SaveChangesAsync(cancellationToken);
        return connection;
    }

    public async Task<SqlOSOidcConnection> UpdateOidcConnectionAsync(string connectionId, SqlOSUpdateOidcConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSOidcConnection>()
            .FirstOrDefaultAsync(x => x.Id == connectionId, cancellationToken)
            ?? throw new InvalidOperationException("OIDC connection not found.");

        SqlOSConfigurationOwnershipPolicy.EnsureDashboardEditable(connection.ConfigurationOwner, "OIDC connection");

        var callbacks = NormalizeCallbackUris(request.AllowedCallbackUris, connectionId);
        if (callbacks.Count == 0)
        {
            throw new InvalidOperationException("At least one callback URI is required.");
        }

        var normalized = NormalizeOidcConfiguration(
            connection.ProviderType,
            request.UseDiscovery,
            request.DiscoveryUrl,
            request.Issuer,
            request.AuthorizationEndpoint,
            request.TokenEndpoint,
            request.UserInfoEndpoint,
            request.JwksUri,
            request.MicrosoftTenant,
            request.Scopes,
            request.ClaimMapping,
            request.ClientAuthMethod,
            request.UseUserInfo,
            request.AppleTeamId,
            request.AppleKeyId);

        connection.DisplayName = request.DisplayName;
        connection.Protocol = normalized.Protocol;
        connection.LogoDataUrl = SqlOSOidcProviderLogoCatalog.NormalizeCustomLogoDataUrl(request.LogoDataUrl);
        connection.ClientId = request.ClientId.Trim();
        connection.AllowedCallbackUrisJson = JsonSerializer.Serialize(callbacks);
        connection.UseDiscovery = normalized.UseDiscovery;
        connection.DiscoveryUrl = normalized.DiscoveryUrl;
        connection.Issuer = normalized.Issuer;
        connection.AuthorizationEndpoint = normalized.AuthorizationEndpoint;
        connection.TokenEndpoint = normalized.TokenEndpoint;
        connection.UserInfoEndpoint = normalized.UserInfoEndpoint;
        connection.JwksUri = normalized.JwksUri;
        connection.MicrosoftTenant = normalized.MicrosoftTenant;
        connection.ScopesJson = JsonSerializer.Serialize(normalized.Scopes);
        connection.ClaimMappingJson = JsonSerializer.Serialize(normalized.ClaimMapping);
        connection.ClientAuthMethod = normalized.ClientAuthMethod;
        connection.UseUserInfo = normalized.UseUserInfo;
        connection.AppleTeamId = normalized.AppleTeamId;
        connection.AppleKeyId = normalized.AppleKeyId;
        connection.TrustUpstreamMfa = request.TrustUpstreamMfa;
        connection.AcceptedAmrValuesJson = JsonSerializer.Serialize(NormalizeTrustValues(request.AcceptedAmrValues));
        connection.AcceptedAcrValuesJson = JsonSerializer.Serialize(NormalizeTrustValues(request.AcceptedAcrValues));
        connection.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            connection.ClientSecretEncrypted = _cryptoService.ProtectSecret(request.ClientSecret.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.ApplePrivateKeyPem))
        {
            connection.ApplePrivateKeyEncrypted = _cryptoService.ProtectSecret(NormalizePrivateKey(request.ApplePrivateKeyPem));
        }

        ValidateOidcSecretRequirements(connection);

        await _context.SaveChangesAsync(cancellationToken);
        return connection;
    }

    public async Task<SqlOSOidcConnection> SetOidcConnectionEnabledAsync(string connectionId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSOidcConnection>()
            .FirstOrDefaultAsync(x => x.Id == connectionId, cancellationToken)
            ?? throw new InvalidOperationException("OIDC connection not found.");

        connection.IsEnabled = isEnabled;
        connection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return connection;
    }

    public async Task<SqlOSSsoConnection> CreateSsoConnectionAsync(SqlOSCreateSsoConnectionRequest request, CancellationToken cancellationToken = default)
    {
        _ = await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(x => x.Id == request.OrganizationId, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");
        var normalized = NormalizeSamlConnection(
            null,
            request.IdentityProviderEntityId,
            request.SingleSignOnUrl,
            request.X509CertificatePem);
        if (await _context.Set<SqlOSSsoConnection>().AnyAsync(
            x => x.IdentityProviderEntityId == normalized.IdentityProviderEntityId,
            cancellationToken))
        {
            throw new InvalidOperationException("A SAML connection already uses this IdP entity ID.");
        }
        var connection = new SqlOSSsoConnection
        {
            Id = _cryptoService.GenerateId("sso"),
            OrganizationId = request.OrganizationId,
            DisplayName = RequireSamlSeedValue(request.DisplayName, "SAML display name is required.", 200),
            IdentityProviderEntityId = normalized.IdentityProviderEntityId,
            SingleSignOnUrl = normalized.SingleSignOnUrl,
            X509CertificatePem = normalized.X509CertificatePem,
            AutoProvisionUsers = request.AutoProvisionUsers,
            AutoLinkByEmail = request.AutoLinkByEmail,
            TrustUpstreamMfa = request.TrustUpstreamMfa,
            AcceptedAuthnContextClassRefsJson = JsonSerializer.Serialize(
                NormalizeTrustValues(request.AcceptedAuthnContextClassRefs)),
            EmailAttributeName = NormalizeSamlAttribute(request.EmailAttributeName, "email"),
            FirstNameAttributeName = NormalizeSamlAttribute(request.FirstNameAttributeName, "first_name"),
            LastNameAttributeName = NormalizeSamlAttribute(request.LastNameAttributeName, "last_name"),
            ConfigurationOwner = SqlOSConfigurationOwners.Dashboard,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsEnabled = true
        };

        _context.Set<SqlOSSsoConnection>().Add(connection);
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync("saml.connection.created", "admin", null, organizationId: connection.OrganizationId, data: new { connection.Id, connection.IdentityProviderEntityId }, cancellationToken: cancellationToken);
        return connection;
    }

    public async Task<SqlOSSsoConnection> CreateSsoConnectionDraftAsync(SqlOSCreateSsoConnectionDraftRequest request, CancellationToken cancellationToken = default)
    {
        var organization = await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(x => x.Id == request.OrganizationId, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");

        var normalizedPrimaryDomain = NormalizeDomain(request.PrimaryDomain);
        if (!string.IsNullOrWhiteSpace(normalizedPrimaryDomain))
        {
            organization.PrimaryDomain = normalizedPrimaryDomain;
        }

        var connection = new SqlOSSsoConnection
        {
            Id = _cryptoService.GenerateId("sso"),
            OrganizationId = request.OrganizationId,
            DisplayName = request.DisplayName,
            IdentityProviderEntityId = string.Empty,
            SingleSignOnUrl = string.Empty,
            X509CertificatePem = string.Empty,
            AutoProvisionUsers = request.AutoProvisionUsers,
            AutoLinkByEmail = request.AutoLinkByEmail,
            EmailAttributeName = "email",
            FirstNameAttributeName = "first_name",
            LastNameAttributeName = "last_name",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsEnabled = false
        };

        _context.Set<SqlOSSsoConnection>().Add(connection);
        await _context.SaveChangesAsync(cancellationToken);
        return connection;
    }

    public async Task<SqlOSSsoConnection> ImportSsoMetadataAsync(
        string connectionId,
        SqlOSImportSsoMetadataRequest request,
        CancellationToken cancellationToken = default)
        => await ImportSsoMetadataAsync(connectionId, request, enableConnection: true, cancellationToken);

    public async Task<SqlOSSsoConnection> ImportSsoMetadataAsync(
        string connectionId,
        SqlOSImportSsoMetadataRequest request,
        bool enableConnection,
        CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSSsoConnection>()
            .Include(x => x.Organization)
            .FirstOrDefaultAsync(x => x.Id == connectionId, cancellationToken)
            ?? throw new InvalidOperationException("SAML connection not found.");

        SqlOSConfigurationOwnershipPolicy.EnsureDashboardEditable(connection.ConfigurationOwner, "SAML connection metadata");

        var metadata = NormalizeSamlConnection(request.MetadataXml, null, null, null);
        if (await _context.Set<SqlOSSsoConnection>().AnyAsync(
            x => x.Id != connection.Id && x.IdentityProviderEntityId == metadata.IdentityProviderEntityId,
            cancellationToken))
        {
            throw new InvalidOperationException("A SAML connection already uses this IdP entity ID.");
        }
        connection.IdentityProviderEntityId = metadata.IdentityProviderEntityId;
        connection.SingleSignOnUrl = metadata.SingleSignOnUrl;
        connection.X509CertificatePem = metadata.X509CertificatePem;
        connection.IsEnabled = enableConnection;
        connection.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync("saml.connection.metadata-imported", "admin", null, organizationId: connection.OrganizationId, data: new { connection.Id, connection.IdentityProviderEntityId }, cancellationToken: cancellationToken);
        return connection;
    }

    public SqlOSSsoMetadataValidationResult ValidateSsoMetadata(SqlOSImportSsoMetadataRequest request)
    {
        try
        {
            var metadata = NormalizeSamlConnection(request.MetadataXml, null, null, null);
            return new SqlOSSsoMetadataValidationResult(
                true,
                null,
                metadata.IdentityProviderEntityId,
                metadata.SingleSignOnUrl,
                !string.IsNullOrWhiteSpace(metadata.X509CertificatePem));
        }
        catch (Exception ex) when (ex is InvalidOperationException or XmlException or FormatException or CryptographicException)
        {
            return new SqlOSSsoMetadataValidationResult(false, ex.Message, null, null, false);
        }
    }

    public async Task<SqlOSSsoConnection> SetSsoConnectionEnabledAsync(
        string connectionId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSSsoConnection>()
            .FirstOrDefaultAsync(x => x.Id == connectionId, cancellationToken)
            ?? throw new InvalidOperationException("SAML connection not found.");
        if (enabled && GetSsoSetupStatus(connection) == "draft")
        {
            throw new InvalidOperationException("Import valid SAML metadata before enabling this connection.");
        }
        connection.IsEnabled = enabled;
        connection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            enabled ? "saml.connection.enabled" : "saml.connection.disabled",
            "admin",
            null,
            organizationId: connection.OrganizationId,
            data: new { connection.Id, connection.ConfigurationOwner },
            cancellationToken: cancellationToken);
        return connection;
    }

    public async Task<SqlOSClientApplication> RequireClientAsync(
        string? clientId,
        string? redirectUri,
        CancellationToken cancellationToken = default,
        HttpContext? httpContext = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Client application is required.");
        }

        var resolved = await _clientResolutionService.ResolveRequiredClientAsync(clientId, redirectUri, httpContext, cancellationToken);
        return resolved.Client;
    }

    public async Task<List<SqlOSOrganizationOption>> GetUserOrganizationsAsync(string userId, CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSMembership>()
            .AsNoTracking()
            .Where(x => x.UserId == userId
                && x.IsActive
                && x.User!.IsActive
                && x.Organization!.IsActive)
            .Include(x => x.Organization)
            .Select(x => new SqlOSOrganizationOption(
                x.OrganizationId,
                x.Organization!.Slug,
                x.Organization.Name,
                x.Role))
            .ToListAsync(cancellationToken);

    public async Task<bool> UserHasMembershipAsync(string userId, string organizationId, CancellationToken cancellationToken = default)
        => (await SqlOSAuthLifecyclePolicy.EvaluateAsync(
            _context,
            userId,
            organizationId,
            cancellationToken)).IsActive;

    public async Task<object> GetDashboardSummaryAsync(CancellationToken cancellationToken = default)
    {
        var users = await _context.Set<SqlOSUser>().CountAsync(cancellationToken);
        var orgs = await _context.Set<SqlOSOrganization>().CountAsync(cancellationToken);
        var sessions = await _context.Set<SqlOSSession>().CountAsync(cancellationToken);
        var connections = await _context.Set<SqlOSSsoConnection>().CountAsync(cancellationToken);
        var oidcConnections = await _context.Set<SqlOSOidcConnection>().CountAsync(cancellationToken);
        var clients = await _context.Set<SqlOSClientApplication>().CountAsync(cancellationToken);
        var eventsCount = await _context.Set<SqlOSAuditEvent>().CountAsync(cancellationToken);
        return new { users, organizations = orgs, sessions, ssoConnections = connections, oidcConnections, clients, auditEvents = eventsCount };
    }

    public async Task<object> GetUserAsync(string userId, CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSUser>()
            .Where(x => x.Id == userId)
            .Select(x => new
            {
                x.Id,
                x.DisplayName,
                x.DefaultEmail,
                x.IsActive,
                x.CreatedAt,
                x.UpdatedAt,
                MembershipCount = x.Memberships.Count(m => m.IsActive),
                SessionCount = x.Sessions.Count(s => s.RevokedAt == null),
                ExternalIdentityCount = x.ExternalIdentities.Count
            })
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException("User not found.");

    public async Task<object> ListUsersAsync(
        string? search = null,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Set<SqlOSUser>().AsNoTracking().Select(x => new UserListRow
        {
            Id = x.Id,
            DisplayName = x.DisplayName,
            DefaultEmail = x.DefaultEmail,
            IsActive = x.IsActive,
            CreatedAt = x.CreatedAt,
            MembershipCount = x.Memberships.Count(m => m.IsActive)
        });
        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmed = search.Trim();
            query = query.Where(x =>
                x.DisplayName.Contains(trimmed)
                || (x.DefaultEmail != null && x.DefaultEmail.Contains(trimmed)));
        }

        return await PaginateByCursorAsync(
            query,
            SqlOSKeyset<UserListRow>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id),
            "auth.users",
            SqlOSCursorCodec.Fingerprint(search),
            cursor,
            pageSize,
            page,
            cancellationToken: cancellationToken);
    }

    public async Task<object> GetOrganizationAsync(string organizationId, CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSOrganization>()
            .Where(x => x.Id == organizationId)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.Slug,
                x.PrimaryDomain,
                x.IsActive,
                x.CreatedAt,
                MembershipCount = x.Memberships.Count(m => m.IsActive),
                SsoConnectionCount = x.SsoConnections.Count,
                EnabledSsoConnections = x.SsoConnections.Count(c => c.IsEnabled)
            })
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException("Organization not found.");

    public async Task<object> ListOrganizationsAsync(
        string? search = null,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Set<SqlOSOrganization>().AsNoTracking().Select(x => new OrganizationListRow
        {
            Id = x.Id,
            Name = x.Name,
            Slug = x.Slug,
            PrimaryDomain = x.PrimaryDomain,
            IsActive = x.IsActive,
            MembershipCount = x.Memberships.Count(m => m.IsActive),
            EnabledSsoConnections = x.SsoConnections.Count(c => c.IsEnabled)
        });
        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmed = search.Trim();
            query = query.Where(x =>
                x.Name.Contains(trimmed)
                || x.Slug.Contains(trimmed)
                || (x.PrimaryDomain != null && x.PrimaryDomain.Contains(trimmed)));
        }

        return await PaginateByCursorAsync(
            query,
            SqlOSKeyset<OrganizationListRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id),
            "auth.organizations",
            SqlOSCursorCodec.Fingerprint(search),
            cursor,
            pageSize,
            page,
            cancellationToken: cancellationToken);
    }

    public async Task<object> ListMembershipsAsync(
        string? search = null,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        var query = ApplyMembershipSearch(
            ProjectMemberships(_context.Set<SqlOSMembership>().AsNoTracking()),
            search);
        return await PaginateByCursorAsync(
            query,
            SqlOSKeyset<MembershipListRow>.Create()
                .Ascending(x => x.OrganizationName)
                .ThenAscending(x => x.UserDisplayName)
                .ThenAscending(x => x.OrganizationId)
                .ThenAscending(x => x.UserId),
            "auth.memberships",
            SqlOSCursorCodec.Fingerprint(search),
            cursor,
            pageSize,
            page,
            MapMembershipListRow,
            cancellationToken: cancellationToken);
    }

    public async Task<object> ListOrganizationMembershipsAsync(
        string organizationId,
        string? search = null,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        var query = ApplyMembershipSearch(
            ProjectMemberships(_context.Set<SqlOSMembership>().AsNoTracking())
                .Where(x => x.OrganizationId == organizationId),
            search);
        return await PaginateByCursorAsync(
            query,
            SqlOSKeyset<MembershipListRow>.Create()
                .Ascending(x => x.UserDisplayName)
                .ThenAscending(x => x.UserId),
            "auth.organization-memberships",
            SqlOSCursorCodec.Fingerprint(organizationId, search),
            cursor,
            pageSize,
            page,
            MapMembershipListRow,
            cancellationToken: cancellationToken);
    }

    public async Task<object> ListUserMembershipsAsync(
        string userId,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        var query = ProjectMemberships(_context.Set<SqlOSMembership>().AsNoTracking())
            .Where(x => x.UserId == userId);
        return await PaginateByCursorAsync(
            query,
            SqlOSKeyset<MembershipListRow>.Create()
                .Ascending(x => x.OrganizationName)
                .ThenAscending(x => x.OrganizationId),
            "auth.user-memberships",
            SqlOSCursorCodec.Fingerprint(userId),
            cursor,
            pageSize,
            page,
            MapMembershipListRow,
            cancellationToken: cancellationToken);
    }

    public async Task<object> ListClientsAsync(
        string? source = null,
        string? status = null,
        string? search = null,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        SqlOSCursorPagination.RejectLegacyOffset(page);
        var resolvedPageSize = SqlOSCursorPagination.NormalizePageSize(pageSize, 10);
        var managedClientIds = GetStartupManagedClientIds();
        var query = ApplyClientListFilters(_context.Set<SqlOSClientApplication>().AsNoTracking(), source, status, search);
        var keyset = SqlOSKeyset<SqlOSClientApplication>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id);
        var fingerprint = SqlOSCursorCodec.Fingerprint(source, status, search);
        var pageResult = await SqlOSCursorPagination.ToPageAsync(
            query,
            keyset,
            "auth.clients",
            fingerprint,
            cursor,
            resolvedPageSize,
            cancellationToken);

        var duplicateCounts = await CountClientDuplicatesForPageAsync(pageResult.Data, cancellationToken);
        var data = pageResult.Data
            .Select(client => FormatClientListItem(
                client,
                managedClientIds.Contains(client.ClientId),
                duplicateCounts.GetValueOrDefault(CalculateDuplicateFingerprint(client) ?? string.Empty)))
            .Cast<object>()
            .ToList();

        object? summary = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            summary = await query.GroupBy(_ => 1).Select(g => new
            {
                ActiveCount = g.Count(x => x.IsActive && x.DisabledAt == null),
                DiscoveredCount = g.Count(x => x.RegistrationSource == "cimd"),
                RegisteredCount = g.Count(x => x.RegistrationSource == "dcr"),
                DisabledCount = g.Count(x => !x.IsActive || x.DisabledAt != null)
            }).FirstOrDefaultAsync(cancellationToken);
        }

        return new
        {
            Data = data,
            PageSize = pageResult.PageSize,
            NextCursor = pageResult.NextCursor,
            HasNextPage = pageResult.HasNextPage,
            Summary = summary
        };
    }

    public async Task<object> GetClientDetailAsync(string clientApplicationId, CancellationToken cancellationToken = default)
    {
        var managedClientIds = GetStartupManagedClientIds();
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        var duplicateFingerprint = CalculateDuplicateFingerprint(client);
        var duplicateCount = 0;
        if (!string.IsNullOrWhiteSpace(duplicateFingerprint))
        {
            duplicateCount = await CountDuplicateClientsAsync(client, duplicateFingerprint, cancellationToken);
        }

        var recentAuditEvents = await _context.Set<SqlOSAuditEvent>()
            .AsNoTracking()
            .Where(x => x.ActorId == client.Id || (x.DataJson != null && x.DataJson.Contains(client.ClientId)))
            .OrderByDescending(x => x.OccurredAt)
            .Take(20)
            .Select(x => new
            {
                x.Id,
                x.EventType,
                x.ActorType,
                x.ActorId,
                x.OccurredAt,
                x.DataJson
            })
            .ToListAsync(cancellationToken);

        var item = FormatClientListItem(client, managedClientIds.Contains(client.ClientId), duplicateCount);
        return new
        {
            item.Id,
            item.ClientId,
            item.Name,
            item.Description,
            item.Audience,
            item.AccessMode,
            item.ClientType,
            item.RegistrationSource,
            item.SourceLabel,
            item.TokenEndpointAuthMethod,
            item.RequirePkce,
            item.IsFirstParty,
            item.AllowNativeHeadlessAuth,
            item.AllowDeviceAuthorization,
            item.RedirectUris,
            item.GrantTypes,
            item.ResponseTypes,
            item.AllowedScopes,
            item.MetadataDocumentUrl,
            item.ClientUri,
            item.LogoUri,
            item.SoftwareId,
            item.SoftwareVersion,
            item.MetadataFetchedAt,
            item.MetadataExpiresAt,
            item.MetadataCacheState,
            item.LastSeenAt,
            item.IsActive,
            item.DisabledAt,
            item.DisabledReason,
            item.ManagedByStartupSeed,
            item.CoreMetadataEditable,
            item.DuplicateFingerprint,
            item.DuplicateCount,
            item.LifecycleState,
            item.Ownership,
            item.EmptyAllowlistWarning,
            item.OmittedOpenIdWarning,
            client.MetadataJson,
            RecentAuditEvents = recentAuditEvents
        };
    }

    internal const string OAuthClientEmergencyDisabledReason = "oauth_client_emergency_disabled";

    public async Task<SqlOSClientApplication> DisableClientAsync(string clientApplicationId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        EnsureOrdinaryClientLifecycle(client, "disable");
        if (!client.IsActive && client.DisabledAt != null)
        {
            return client;
        }

        client.IsActive = false;
        client.DisabledAt = DateTime.UtcNow;
        client.DisabledReason = string.IsNullOrWhiteSpace(reason) ? "disabled_by_operator" : reason.Trim();
        await RevokeClientSessionsInternalAsync(client.Id, "client_disabled", cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "client.disabled",
            "client",
            client.Id,
            ipAddress: null,
            data: new
            {
                client_id = client.ClientId,
                source = client.RegistrationSource,
                reason = client.DisabledReason
            },
            cancellationToken: cancellationToken);
        return client;
    }

    public async Task<SqlOSClientApplication> EnableClientAsync(string clientApplicationId, CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        EnsureOrdinaryClientLifecycle(client, "enable");
        if (!client.IsActive && client.DisabledAt == null)
        {
            throw new InvalidOperationException($"OAuth client '{client.ClientId}' is disabled in its seed. Set IsActive in source control to re-enable it.");
        }

        client.IsActive = true;
        client.DisabledAt = null;
        client.DisabledReason = null;
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "client.enabled",
            "client",
            client.Id,
            data: new
            {
                client_id = client.ClientId,
                source = client.RegistrationSource
            },
            cancellationToken: cancellationToken);
        return client;
    }

    public async Task<SqlOSClientApplication> EmergencyDisableClientAsync(string clientApplicationId, CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        EnsureCodeOwnedClient(client, "emergency disable");
        if (IsEmergencyDisabled(client))
        {
            return client;
        }

        client.IsActive = false;
        client.DisabledAt = DateTime.UtcNow;
        client.DisabledReason = OAuthClientEmergencyDisabledReason;
        await RevokeClientSessionsInternalAsync(client.Id, "client_emergency_disabled", cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "client.emergency_disabled",
            "client",
            client.Id,
            ipAddress: null,
            data: new
            {
                client_id = client.ClientId,
                source = client.RegistrationSource,
                reason = client.DisabledReason
            },
            cancellationToken: cancellationToken);
        return client;
    }

    public async Task<SqlOSClientApplication> EmergencyEnableClientAsync(string clientApplicationId, CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        EnsureCodeOwnedClient(client, "emergency enable");
        if (client.IsActive && client.DisabledAt == null)
        {
            return client;
        }

        if (!client.IsActive && client.DisabledAt == null)
        {
            throw new InvalidOperationException($"OAuth client '{client.ClientId}' is disabled in its seed. Set IsActive in source control to re-enable it.");
        }

        if (!IsEmergencyDisabled(client))
        {
            throw new InvalidOperationException($"OAuth client '{client.ClientId}' can only be emergency-enabled from '{OAuthClientEmergencyDisabledReason}'.");
        }

        client.IsActive = true;
        client.DisabledAt = null;
        client.DisabledReason = null;
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "client.emergency_enabled",
            "client",
            client.Id,
            data: new
            {
                client_id = client.ClientId,
                source = client.RegistrationSource
            },
            cancellationToken: cancellationToken);
        return client;
    }

    private static bool AllowsOrdinaryClientLifecycle(string owner)
        => string.Equals(owner, SqlOSConfigurationOwners.Dashboard, StringComparison.OrdinalIgnoreCase)
            || string.Equals(owner, SqlOSConfigurationOwners.Dynamic, StringComparison.OrdinalIgnoreCase);

    private static bool IsEmergencyDisabled(SqlOSClientApplication client)
        => client.DisabledAt != null
            && string.Equals(client.DisabledReason, OAuthClientEmergencyDisabledReason, StringComparison.Ordinal);

    private static void EnsureOrdinaryClientLifecycle(SqlOSClientApplication client, string action)
    {
        if (!AllowsOrdinaryClientLifecycle(client.ConfigurationOwner))
        {
            throw new InvalidOperationException(
                $"OAuth client '{client.ClientId}' is owned by the '{client.ConfigurationOwner}' configuration source. Use emergency {action} for incidents, or change its seed.");
        }
    }

    private static void EnsureCodeOwnedClient(SqlOSClientApplication client, string action)
    {
        if (!string.Equals(client.ConfigurationOwner, SqlOSConfigurationOwners.Code, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"OAuth client '{client.ClientId}' is not code-owned. Use ordinary {action} instead.");
        }
    }

    public async Task<int> RevokeClientSessionsAsync(string clientApplicationId, string reason = "client_revoked", CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        var revokedCount = await RevokeClientSessionsInternalAsync(client.Id, reason, cancellationToken);
        if (revokedCount > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        await RecordAuditAsync(
            "client.sessions-revoked",
            "client",
            client.Id,
            data: new
            {
                client_id = client.ClientId,
                source = client.RegistrationSource,
                revoked_sessions = revokedCount,
                reason
            },
            cancellationToken: cancellationToken);
        return revokedCount;
    }

    public async Task<SqlOSClientApplication> SetApplicationAccessModeAsync(
        string clientApplicationId,
        SqlOSSetApplicationAccessModeRequest request,
        string actorType = "admin",
        string? actorId = null,
        CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        if (string.Equals(actorType, "dashboard", StringComparison.OrdinalIgnoreCase))
        {
            SqlOSConfigurationOwnershipPolicy.EnsureDashboardEditable(client.ConfigurationOwner, $"OAuth client '{client.ClientId}' access mode");
        }
        var accessMode = NormalizeAccessMode(request.AccessMode);
        var previousMode = NormalizeAccessMode(client.AccessMode);
        client.AccessMode = accessMode;

        if (accessMode == SqlOSApplicationAccessModes.Disabled)
        {
            client.IsActive = false;
            client.DisabledAt ??= DateTime.UtcNow;
            client.DisabledReason ??= "application_access_disabled";
            await RevokeClientSessionsInternalAsync(client.Id, "application_access_disabled", cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "application.access_mode.changed",
            actorType,
            actorId,
            data: new
            {
                client_application_id = client.Id,
                client_id = client.ClientId,
                previous_access_mode = previousMode,
                access_mode = accessMode
            },
            cancellationToken: cancellationToken);
        return client;
    }

    public async Task<object> ListApplicationAssignmentsAsync(
        string clientApplicationId,
        bool includeRevoked = false,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        var query = _context.Set<SqlOSApplicationAssignment>()
            .AsNoTracking()
            .Include(x => x.Organization)
            .Where(x => x.ClientApplicationId == client.Id);
        if (!includeRevoked)
        {
            query = query.Where(x => x.RevokedAt == null);
        }

        SqlOSCursorPagination.RejectLegacyOffset(page);
        var size = SqlOSCursorPagination.NormalizePageSize(pageSize, 10);
        var pageResult = await SqlOSCursorPagination.ToPageAsync(
            query,
            SqlOSKeyset<SqlOSApplicationAssignment>.Create()
                .Descending(x => x.CreatedAt)
                .ThenDescending(x => x.Id),
            "auth.application-assignments",
            SqlOSCursorCodec.Fingerprint(client.Id, includeRevoked ? "revoked" : "active"),
            cursor,
            size,
            cancellationToken);

        return new
        {
            client.Id,
            client.ClientId,
            client.Name,
            AccessMode = NormalizeAccessMode(client.AccessMode),
            Data = pageResult.Data.Select(x => new
            {
                x.Id,
                x.ClientApplicationId,
                x.OrganizationId,
                Organization = x.Organization == null ? null : x.Organization.Name,
                x.PrincipalType,
                x.PrincipalId,
                x.RoleKey,
                x.Access,
                x.Reason,
                Ownership = SqlOSConfigurationOwnershipPolicy.ToDto(
                    x.ConfigurationOwner,
                    x.ConfigurationSourceKey,
                    x.LastReconciledAt,
                    x.ConfigurationFingerprint,
                    x.ConfigurationOrphanedAt,
                    false),
                x.CreatedAt,
                x.CreatedByActorType,
                x.CreatedByActorId,
                x.RevokedAt,
                x.RevokedByActorType,
                x.RevokedByActorId
            }).ToList(),
            PageSize = pageResult.PageSize,
            NextCursor = pageResult.NextCursor,
            HasNextPage = pageResult.HasNextPage
        };
    }

    public async Task<SqlOSApplicationAssignment> AssignApplicationAsync(
        string clientApplicationId,
        SqlOSCreateApplicationAssignmentRequest request,
        string actorType = "admin",
        string? actorId = null,
        CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        var normalized = NormalizeAssignmentRequest(request);
        await ValidateAssignmentPrincipalAsync(normalized, cancellationToken);

        var assignment = new SqlOSApplicationAssignment
        {
            Id = _cryptoService.GenerateId("asa"),
            ClientApplicationId = client.Id,
            OrganizationId = normalized.OrganizationId,
            PrincipalType = normalized.PrincipalType,
            PrincipalId = normalized.PrincipalId,
            RoleKey = normalized.RoleKey,
            Access = normalized.Access,
            Reason = normalized.Reason,
            ConfigurationOwner = SqlOSConfigurationOwners.Dashboard,
            CreatedAt = DateTime.UtcNow,
            CreatedByActorType = actorType,
            CreatedByActorId = actorId
        };

        _context.Set<SqlOSApplicationAssignment>().Add(assignment);
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "application.assignment.created",
            actorType,
            actorId,
            organizationId: assignment.OrganizationId,
            data: new
            {
                client_application_id = client.Id,
                client_id = client.ClientId,
                assignment_id = assignment.Id,
                assignment.PrincipalType,
                assignment.PrincipalId,
                assignment.RoleKey,
                assignment.Access
            },
            cancellationToken: cancellationToken);
        return assignment;
    }

    public async Task<SqlOSApplicationAssignment> RevokeApplicationAssignmentAsync(
        string clientApplicationId,
        string assignmentId,
        SqlOSRevokeApplicationAssignmentRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        var assignment = await _context.Set<SqlOSApplicationAssignment>()
            .FirstOrDefaultAsync(x => x.Id == assignmentId && x.ClientApplicationId == client.Id, cancellationToken)
            ?? throw new InvalidOperationException("Application assignment was not found.");

        SqlOSConfigurationOwnershipPolicy.EnsureDashboardEditable(assignment.ConfigurationOwner, $"Application assignment '{assignment.Id}'");

        if (assignment.RevokedAt == null)
        {
            assignment.RevokedAt = DateTime.UtcNow;
            assignment.RevokedByActorType = string.IsNullOrWhiteSpace(request?.ActorType) ? "admin" : request.ActorType!.Trim();
            assignment.RevokedByActorId = string.IsNullOrWhiteSpace(request?.ActorId) ? null : request.ActorId!.Trim();
            await _context.SaveChangesAsync(cancellationToken);
            await RecordAuditAsync(
                "application.assignment.revoked",
                assignment.RevokedByActorType,
                assignment.RevokedByActorId,
                organizationId: assignment.OrganizationId,
                data: new
                {
                    client_application_id = client.Id,
                    client_id = client.ClientId,
                    assignment_id = assignment.Id,
                    assignment.PrincipalType,
                    assignment.PrincipalId,
                    assignment.RoleKey,
                    assignment.Access,
                    reason = request?.Reason
                },
                cancellationToken: cancellationToken);
        }

        return assignment;
    }

    public async Task<SqlOSApplicationAccessCheckResult> CheckApplicationAccessAsync(
        string clientApplicationId,
        string? userId,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        return await CheckApplicationAccessAsync(client, userId, organizationId, cancellationToken: cancellationToken);
    }

    public async Task EnsureApplicationAccessAsync(
        SqlOSClientApplication client,
        string? userId,
        string? organizationId,
        string eventType,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var decision = await CheckApplicationAccessAsync(client, userId, organizationId, recordDeniedAudit: true, eventType, ipAddress, cancellationToken);
        if (!decision.Allowed)
        {
            throw new InvalidOperationException("Application access is not allowed.");
        }
    }

    public async Task<SqlOSApplicationAccessCheckResult> CheckApplicationAccessAsync(
        SqlOSClientApplication client,
        string? userId,
        string? organizationId,
        bool recordDeniedAudit = false,
        string eventType = "application.access.checked",
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await EvaluateApplicationAccessAsync(client, userId, organizationId, cancellationToken);
        if (!result.Allowed && recordDeniedAudit)
        {
            await RecordAuditAsync(
                eventType,
                "client",
                client.Id,
                userId: userId,
                organizationId: organizationId,
                ipAddress: ipAddress,
                data: new
                {
                    client_application_id = client.Id,
                    client_id = client.ClientId,
                    result.AccessMode,
                    result.Source,
                    result.AssignmentId,
                    result.Reason
                },
                cancellationToken: cancellationToken);
        }

        return result;
    }

    public async Task<object> ListApplicationsForOrganizationAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        var organization = await _context.Set<SqlOSOrganization>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == organizationId, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");

        var clients = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var results = new List<object>();
        foreach (var client in clients)
        {
            var decision = await CheckApplicationAccessAsync(client, userId: null, organizationId, cancellationToken: cancellationToken);
            if (decision.Allowed)
            {
                results.Add(new
                {
                    client.Id,
                    client.ClientId,
                    client.Name,
                    client.Audience,
                    AccessMode = decision.AccessMode,
                    decision.Source,
                    decision.AssignmentId
                });
            }
        }

        return new
        {
            organization.Id,
            organization.Name,
            Applications = results
        };
    }

    public async Task<object> ListApplicationsForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await _context.Set<SqlOSUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        var memberships = await _context.Set<SqlOSMembership>()
            .AsNoTracking()
            .Include(x => x.Organization)
            .Where(x => x.UserId == userId && x.IsActive && x.Organization!.IsActive)
            .OrderBy(x => x.Organization!.Name)
            .ToListAsync(cancellationToken);
        var clients = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var rows = new List<object>();
        foreach (var client in clients)
        {
            if (memberships.Count == 0)
            {
                var decision = await CheckApplicationAccessAsync(client, userId, organizationId: null, cancellationToken: cancellationToken);
                if (decision.Allowed)
                {
                    rows.Add(new
                    {
                        client.Id,
                        client.ClientId,
                        client.Name,
                        client.Audience,
                        OrganizationId = (string?)null,
                        Organization = (string?)null,
                        AccessMode = decision.AccessMode,
                        decision.Source,
                        decision.AssignmentId
                    });
                }
            }

            foreach (var membership in memberships)
            {
                var decision = await CheckApplicationAccessAsync(client, userId, membership.OrganizationId, cancellationToken: cancellationToken);
                if (!decision.Allowed)
                {
                    continue;
                }

                rows.Add(new
                {
                    client.Id,
                    client.ClientId,
                    client.Name,
                    client.Audience,
                    membership.OrganizationId,
                    Organization = membership.Organization?.Name,
                    AccessMode = decision.AccessMode,
                    decision.Source,
                    decision.AssignmentId
                });
            }
        }

        var sessions = await _context.Set<SqlOSSession>()
            .AsNoTracking()
            .Include(x => x.ClientApplication)
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(20)
            .Select(x => new
            {
                x.Id,
                x.ClientApplicationId,
                ClientId = x.ClientApplication == null ? null : x.ClientApplication.ClientId,
                ClientName = x.ClientApplication == null ? null : x.ClientApplication.Name,
                x.OrganizationId,
                x.CreatedAt,
                x.LastSeenAt,
                x.RevokedAt
            })
            .ToListAsync(cancellationToken);

        return new
        {
            user.Id,
            user.DisplayName,
            user.DefaultEmail,
            Applications = rows,
            RecentSessions = sessions
        };
    }

    public async Task<int> CleanupStaleDynamicClientsAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.ClientRegistration.Dcr.EnableAutomaticCleanup)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - _options.ClientRegistration.Dcr.StaleClientRetention;
        var candidates = await _context.Set<SqlOSClientApplication>()
            .Where(x => x.RegistrationSource == "dcr"
                && (x.LastSeenAt == null || x.LastSeenAt < cutoff)
                && x.CreatedAt < cutoff)
            .ToListAsync(cancellationToken);

        var removed = 0;
        foreach (var client in candidates)
        {
            var hasAnySessions = await _context.Set<SqlOSSession>()
                .AnyAsync(x => x.ClientApplicationId == client.Id, cancellationToken);
            if (hasAnySessions)
            {
                continue;
            }

            _context.Set<SqlOSClientApplication>().Remove(client);
            await _context.SaveChangesAsync(cancellationToken);
            removed++;

            await RecordAuditAsync(
                "client.cleanup.removed",
                "client",
                client.Id,
                data: new
                {
                    client_id = client.ClientId,
                    source = client.RegistrationSource
                },
                cancellationToken: cancellationToken);
        }

        return removed;
    }

    public async Task<object> ListOidcConnectionsAsync(
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
        => await PaginateByCursorAsync(
            _context.Set<SqlOSOidcConnection>().AsNoTracking(),
            SqlOSKeyset<SqlOSOidcConnection>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id),
            "auth.oidc-connections",
            SqlOSCursorCodec.Fingerprint(),
            cursor,
            pageSize,
            page,
            x => new
            {
                x.Id,
                ProviderType = x.ProviderType.ToString(),
                Protocol = x.Protocol.ToString(),
                x.DisplayName,
                x.LogoDataUrl,
                EffectiveLogoDataUrl = SqlOSOidcProviderLogoCatalog.ResolveEffectiveLogoDataUrl(x.ProviderType, x.LogoDataUrl),
                x.ClientId,
                AllowedCallbackUris = x.AllowedCallbackUrisJson,
                x.UseDiscovery,
                x.DiscoveryUrl,
                x.Issuer,
                x.AuthorizationEndpoint,
                x.TokenEndpoint,
                x.UserInfoEndpoint,
                x.JwksUri,
                x.MicrosoftTenant,
                Scopes = x.ScopesJson,
                ClaimMapping = x.ClaimMappingJson,
                ClientAuthMethod = x.ClientAuthMethod.ToString(),
                x.UseUserInfo,
                x.AppleTeamId,
                x.AppleKeyId,
                x.IsEnabled,
                Ownership = SqlOSConfigurationOwnershipPolicy.ToDto(
                    x.ConfigurationOwner,
                    x.ConfigurationSourceKey,
                    x.LastReconciledAt,
                    x.ConfigurationFingerprint,
                    x.ConfigurationOrphanedAt),
                x.CreatedAt,
                x.UpdatedAt
            },
            cancellationToken: cancellationToken);

    public async Task<object> ListSsoConnectionsAsync(
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
        => await PaginateByCursorAsync(
            _context.Set<SqlOSSsoConnection>().AsNoTracking().Include(x => x.Organization),
            SqlOSKeyset<SqlOSSsoConnection>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id),
            "auth.sso-connections",
            SqlOSCursorCodec.Fingerprint(),
            cursor,
            pageSize,
            page,
            x => new
            {
                x.Id,
                x.DisplayName,
                x.IdentityProviderEntityId,
                x.SingleSignOnUrl,
                x.IsEnabled,
                Organization = x.Organization!.Name,
                x.OrganizationId,
                x.Organization!.PrimaryDomain,
                x.AutoProvisionUsers,
                x.AutoLinkByEmail,
                Ownership = SqlOSConfigurationOwnershipPolicy.ToDto(x.ConfigurationOwner, x.ConfigurationSourceKey, x.LastReconciledAt, x.ConfigurationFingerprint, x.ConfigurationOrphanedAt),
                SetupStatus = GetSsoSetupStatus(x),
                ServiceProviderEntityId = GetServiceProviderEntityId(),
                AssertionConsumerServiceUrl = GetAssertionConsumerServiceUrl(x.Id)
            },
            cancellationToken: cancellationToken);

    public async Task<object> ListOrganizationSsoConnectionsAsync(
        string organizationId,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Set<SqlOSSsoConnection>()
            .AsNoTracking()
            .Include(x => x.Organization)
            .Where(x => x.OrganizationId == organizationId);
        SqlOSCursorPagination.RejectLegacyOffset(page);
        var size = SqlOSCursorPagination.NormalizePageSize(pageSize, 10);
        var pageResult = await SqlOSCursorPagination.ToPageAsync(
            query,
            SqlOSKeyset<SqlOSSsoConnection>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id),
            "auth.organization-sso-connections",
            SqlOSCursorCodec.Fingerprint(organizationId),
            cursor,
            size,
            cancellationToken);
        var serviceProviderEntityId = GetServiceProviderEntityId();
        return new
        {
            Data = pageResult.Data.Select(x => new
            {
                x.Id,
                x.DisplayName,
                x.IdentityProviderEntityId,
                x.SingleSignOnUrl,
                x.IsEnabled,
                Organization = x.Organization!.Name,
                x.OrganizationId,
                PrimaryDomain = x.Organization!.PrimaryDomain,
                x.AutoProvisionUsers,
                x.AutoLinkByEmail,
                Ownership = SqlOSConfigurationOwnershipPolicy.ToDto(x.ConfigurationOwner, x.ConfigurationSourceKey, x.LastReconciledAt, x.ConfigurationFingerprint, x.ConfigurationOrphanedAt),
                SetupStatus = GetSsoSetupStatus(x),
                ServiceProviderEntityId = serviceProviderEntityId,
                AssertionConsumerServiceUrl = GetAssertionConsumerServiceUrl(x.Id)
            }).ToList(),
            PageSize = pageResult.PageSize,
            NextCursor = pageResult.NextCursor,
            HasNextPage = pageResult.HasNextPage
        };
    }

    public async Task<object> ListSessionsAsync(
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
        => await PaginateByCursorAsync(
            _context.Set<SqlOSSession>().AsNoTracking().Include(x => x.User),
            SqlOSKeyset<SqlOSSession>.Create().Descending(x => x.CreatedAt).ThenDescending(x => x.Id),
            "auth.sessions",
            SqlOSCursorCodec.Fingerprint(),
            cursor,
            pageSize,
            page,
            MapSessionListRow,
            cancellationToken: cancellationToken);

    public async Task<object> ListUserSessionsAsync(
        string userId,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
        => await PaginateByCursorAsync(
            _context.Set<SqlOSSession>().AsNoTracking().Include(x => x.User).Where(x => x.UserId == userId),
            SqlOSKeyset<SqlOSSession>.Create().Descending(x => x.CreatedAt).ThenDescending(x => x.Id),
            "auth.user-sessions",
            SqlOSCursorCodec.Fingerprint(userId),
            cursor,
            pageSize,
            page,
            MapSessionListRow,
            cancellationToken: cancellationToken);

    private static object MapSessionListRow(SqlOSSession x) => new
    {
        x.Id,
        x.AuthenticationMethod,
        User = x.User?.DisplayName,
        x.UserId,
        x.ClientApplicationId,
        x.CreatedAt,
        x.LastSeenAt,
        x.IdleExpiresAt,
        x.AbsoluteExpiresAt,
        x.RevokedAt,
        x.RevocationReason,
        x.UserAgent,
        x.IpAddress
    };

    public async Task<List<object>> ListAuditEventsAsync(CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSAuditEvent>()
            .OrderByDescending(x => x.OccurredAt)
            .Take(200)
            .Select(x => new
            {
                x.Id,
                x.EventType,
                x.Action,
                x.Source,
                x.ApplicationId,
                x.ApplicationKey,
                x.ActorType,
                x.ActorId,
                x.ActorDisplayName,
                x.UserId,
                x.OrganizationId,
                x.SessionId,
                x.OccurredAt,
                x.IngestedAt,
                x.IpAddress,
                x.UserAgent,
                x.RequestId,
                x.CorrelationId,
                x.TargetsJson,
                x.ContextJson,
                x.MetadataJson,
                x.DataJson
            })
            .Cast<object>()
            .ToListAsync(cancellationToken);

    private static IQueryable<MembershipListRow> ApplyMembershipSearch(
        IQueryable<MembershipListRow> query,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var trimmed = search.Trim();
        return query.Where(x =>
            x.OrganizationName.Contains(trimmed)
            || x.UserDisplayName.Contains(trimmed)
            || (x.UserEmail != null && x.UserEmail.Contains(trimmed))
            || x.Role.Contains(trimmed));
    }

    private static IQueryable<MembershipListRow> ProjectMemberships(IQueryable<SqlOSMembership> query)
        => query.Select(x => new MembershipListRow
        {
            OrganizationId = x.OrganizationId,
            OrganizationName = x.Organization!.Name,
            UserId = x.UserId,
            UserDisplayName = x.User!.DisplayName,
            UserEmail = x.User!.DefaultEmail,
            Role = x.Role,
            IsActive = x.IsActive,
            CreatedAt = x.CreatedAt
        });

    private static object MapMembershipListRow(MembershipListRow x) => new
    {
        x.OrganizationId,
        Organization = x.OrganizationName,
        x.UserId,
        User = x.UserDisplayName,
        UserEmail = x.UserEmail,
        x.Role,
        x.IsActive,
        x.CreatedAt
    };

    private static async Task<object> PaginateByCursorAsync<T>(
        IQueryable<T> query,
        SqlOSKeyset<T> keyset,
        string sortKey,
        string filterFingerprint,
        string? cursor,
        int? pageSize,
        int? page,
        int defaultPageSize = 10,
        CancellationToken cancellationToken = default)
        where T : class
    {
        SqlOSCursorPagination.RejectLegacyOffset(page);
        var size = SqlOSCursorPagination.NormalizePageSize(pageSize, defaultPageSize);
        var pageResult = await SqlOSCursorPagination.ToPageAsync(
            query,
            keyset,
            sortKey,
            filterFingerprint,
            cursor,
            size,
            cancellationToken);
        return pageResult.ToResponse();
    }

    private static async Task<object> PaginateByCursorAsync<T>(
        IQueryable<T> query,
        SqlOSKeyset<T> keyset,
        string sortKey,
        string filterFingerprint,
        string? cursor,
        int? pageSize,
        int? page,
        Func<T, object> selector,
        int defaultPageSize = 10,
        CancellationToken cancellationToken = default)
        where T : class
    {
        SqlOSCursorPagination.RejectLegacyOffset(page);
        var size = SqlOSCursorPagination.NormalizePageSize(pageSize, defaultPageSize);
        var pageResult = await SqlOSCursorPagination.ToPageAsync(
            query,
            keyset,
            sortKey,
            filterFingerprint,
            cursor,
            size,
            cancellationToken);
        return pageResult.ToResponse(selector);
    }

    private static IQueryable<SqlOSClientApplication> ApplyClientListFilters(
        IQueryable<SqlOSClientApplication> query,
        string? source,
        string? status,
        string? search)
    {
        var registrationSource = NormalizeClientSourceFilter(source);
        if (registrationSource != null)
        {
            query = query.Where(x => x.RegistrationSource == registrationSource);
        }

        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            switch (status.Trim().ToLowerInvariant())
            {
                case "active":
                    query = query.Where(x => x.IsActive && x.DisabledAt == null);
                    break;
                case "disabled":
                    query = query.Where(x => !x.IsActive || x.DisabledAt != null);
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim().ToLower();
            query = query.Where(x =>
                (x.Name != null && x.Name.ToLower().Contains(needle))
                || x.ClientId.ToLower().Contains(needle)
                || (x.Description != null && x.Description.ToLower().Contains(needle))
                || x.Audience.ToLower().Contains(needle)
                || (x.SoftwareId != null && x.SoftwareId.ToLower().Contains(needle))
                || (x.SoftwareVersion != null && x.SoftwareVersion.ToLower().Contains(needle))
                || (x.MetadataDocumentUrl != null && x.MetadataDocumentUrl.ToLower().Contains(needle))
                || (needle == "seeded" && x.RegistrationSource == "seeded")
                || (needle == "manual" && x.RegistrationSource == "manual")
                || (needle == "discovered" && x.RegistrationSource == "cimd")
                || (needle == "registered" && x.RegistrationSource == "dcr"));
        }

        return query;
    }

    private static string? NormalizeClientSourceFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return filter.Trim().ToLowerInvariant() switch
        {
            "discovered" => "cimd",
            "registered" => "dcr",
            var value => value
        };
    }

    private async Task<Dictionary<string, int>> CountClientDuplicatesForPageAsync(
        IReadOnlyList<SqlOSClientApplication> page,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var client in page)
        {
            var fingerprint = CalculateDuplicateFingerprint(client);
            if (string.IsNullOrWhiteSpace(fingerprint) || counts.ContainsKey(fingerprint))
            {
                continue;
            }

            counts[fingerprint] = await CountDuplicateClientsAsync(client, fingerprint, cancellationToken);
        }

        return counts;
    }

    private sealed class UserListRow
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? DefaultEmail { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public int MembershipCount { get; set; }
    }

    private sealed class OrganizationListRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string? PrimaryDomain { get; set; }
        public bool IsActive { get; set; }
        public int MembershipCount { get; set; }
        public int EnabledSsoConnections { get; set; }
    }

    private sealed class MembershipListRow
    {
        public string OrganizationId { get; set; } = string.Empty;
        public string OrganizationName { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string UserDisplayName { get; set; } = string.Empty;
        public string? UserEmail { get; set; }
        public string Role { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public async Task RecordAuditAsync(
        string eventType,
        string actorType,
        string? actorId,
        string? userId = null,
        string? organizationId = null,
        string? sessionId = null,
        string? ipAddress = null,
        object? data = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, object?>? metadata = data == null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(data),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var auditLogs = new SqlOSAuditLogService(_context, _cryptoService);
        await auditLogs.RecordAsync(
            new SqlOSAuditLogRecordRequest(
                Action: eventType,
                OrganizationId: organizationId,
                UserId: userId,
                Source: "authserver",
                Actor: new SqlOSAuditActor(actorType, actorId),
                Context: new SqlOSAuditContext(
                    IpAddress: ipAddress,
                    SessionId: sessionId),
                Metadata: metadata),
            cancellationToken);
    }

    private async Task<SqlOSApplicationAccessCheckResult> EvaluateApplicationAccessAsync(
        SqlOSClientApplication client,
        string? userId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var accessMode = NormalizeAccessMode(client.AccessMode);
        if (!client.IsActive || client.DisabledAt != null || accessMode == SqlOSApplicationAccessModes.Disabled)
        {
            return Denied(client, userId, organizationId, accessMode, "application_disabled", "Application is disabled.");
        }

        var assignments = await _context.Set<SqlOSApplicationAssignment>()
            .AsNoTracking()
            .Where(x => x.ClientApplicationId == client.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        var denied = await FirstMatchingAssignmentAsync(assignments, userId, organizationId, SqlOSApplicationAssignmentAccess.Denied, cancellationToken);
        if (denied != null)
        {
            return Denied(client, userId, organizationId, accessMode, "assignment_denied", denied.Reason ?? "Explicit deny assignment matched.", denied.Id);
        }

        if (accessMode == SqlOSApplicationAccessModes.AllOrganizations)
        {
            return Allowed(client, userId, organizationId, accessMode, "all_organizations", "Application is open to all organizations.");
        }

        var allowedAssignments = assignments
            .Where(x => string.Equals(x.Access, SqlOSApplicationAssignmentAccess.Allowed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (accessMode == SqlOSApplicationAccessModes.SelectedOrganizations)
        {
            var orgAssignment = allowedAssignments.FirstOrDefault(x => OrganizationAssignmentMatches(x, organizationId));
            return orgAssignment == null
                ? Denied(client, userId, organizationId, accessMode, "no_organization_assignment", "Organization is not assigned to this application.")
                : Allowed(client, userId, organizationId, accessMode, "organization_assignment", orgAssignment.Reason, orgAssignment.Id);
        }

        if (accessMode == SqlOSApplicationAccessModes.SelectedUsersGroupsRoles
            || accessMode == SqlOSApplicationAccessModes.InternalOnly)
        {
            var assignment = await FirstMatchingAssignmentAsync(allowedAssignments, userId, organizationId, SqlOSApplicationAssignmentAccess.Allowed, cancellationToken);
            return assignment == null
                ? Denied(client, userId, organizationId, accessMode, "no_principal_assignment", "No user, group, role, or service assignment matched.")
                : Allowed(client, userId, organizationId, accessMode, $"{assignment.PrincipalType}_assignment", assignment.Reason, assignment.Id);
        }

        return Denied(client, userId, organizationId, accessMode, "unsupported_access_mode", "Application access mode is not supported.");
    }

    private async Task<SqlOSApplicationAssignment?> FirstMatchingAssignmentAsync(
        IReadOnlyList<SqlOSApplicationAssignment> assignments,
        string? userId,
        string? organizationId,
        string access,
        CancellationToken cancellationToken)
    {
        foreach (var assignment in assignments.Where(x => string.Equals(x.Access, access, StringComparison.OrdinalIgnoreCase)))
        {
            if (await AssignmentMatchesAsync(assignment, userId, organizationId, cancellationToken))
            {
                return assignment;
            }
        }

        return null;
    }

    private async Task<bool> AssignmentMatchesAsync(
        SqlOSApplicationAssignment assignment,
        string? userId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        return assignment.PrincipalType switch
        {
            SqlOSApplicationAssignmentPrincipalTypes.Organization => OrganizationAssignmentMatches(assignment, organizationId),
            SqlOSApplicationAssignmentPrincipalTypes.User => !string.IsNullOrWhiteSpace(userId)
                && string.Equals(assignment.PrincipalId, userId, StringComparison.Ordinal),
            SqlOSApplicationAssignmentPrincipalTypes.Role => await RoleAssignmentMatchesAsync(assignment, userId, organizationId, cancellationToken),
            SqlOSApplicationAssignmentPrincipalTypes.Group => await GroupAssignmentMatchesAsync(assignment, userId, cancellationToken),
            SqlOSApplicationAssignmentPrincipalTypes.ServiceAccount => !string.IsNullOrWhiteSpace(userId)
                && string.Equals(assignment.PrincipalId, userId, StringComparison.Ordinal),
            SqlOSApplicationAssignmentPrincipalTypes.Agent => !string.IsNullOrWhiteSpace(userId)
                && string.Equals(assignment.PrincipalId, userId, StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool OrganizationAssignmentMatches(SqlOSApplicationAssignment assignment, string? organizationId)
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return false;
        }

        return string.Equals(assignment.OrganizationId, organizationId, StringComparison.Ordinal)
            || string.Equals(assignment.PrincipalId, organizationId, StringComparison.Ordinal);
    }

    private async Task<bool> RoleAssignmentMatchesAsync(
        SqlOSApplicationAssignment assignment,
        string? userId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(organizationId)
            || string.IsNullOrWhiteSpace(assignment.RoleKey))
        {
            return false;
        }

        return await _context.Set<SqlOSMembership>()
            .AnyAsync(x => x.UserId == userId
                && x.OrganizationId == organizationId
                && x.IsActive
                && x.Role == assignment.RoleKey, cancellationToken);
    }

    private async Task<bool> GroupAssignmentMatchesAsync(
        SqlOSApplicationAssignment assignment,
        string? userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(assignment.PrincipalId))
        {
            return false;
        }

        var subjectIds = await ResolveFgaSubjectIdsForAuthUserAsync(userId, cancellationToken);
        if (subjectIds.Count == 0)
        {
            return false;
        }

        return await _context.Set<SqlOSFgaUserGroupMembership>()
            .Join(
                _context.Set<SqlOSFgaUserGroup>(),
                membership => membership.UserGroupId,
                group => group.Id,
                (membership, group) => new { membership.SubjectId, GroupId = group.Id, GroupSubjectId = group.SubjectId })
            .AnyAsync(x => subjectIds.Contains(x.SubjectId)
                && (x.GroupId == assignment.PrincipalId || x.GroupSubjectId == assignment.PrincipalId), cancellationToken);
    }

    private async Task<List<string>> ResolveFgaSubjectIdsForAuthUserAsync(string userId, CancellationToken cancellationToken)
    {
        var subjectIds = new HashSet<string>(StringComparer.Ordinal) { userId };

        var directSubjectIds = await _context.Set<SqlOSFgaSubject>()
            .Where(x => x.Id == userId || x.ExternalRef == userId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        foreach (var subjectId in directSubjectIds)
        {
            subjectIds.Add(subjectId);
        }

        var fgaUserSubjectIds = await _context.Set<SqlOSFgaUser>()
            .Where(x => x.Id == userId || x.SubjectId == userId)
            .Select(x => x.SubjectId)
            .ToListAsync(cancellationToken);
        foreach (var subjectId in fgaUserSubjectIds)
        {
            subjectIds.Add(subjectId);
        }

        return subjectIds.ToList();
    }

    private static SqlOSApplicationAccessCheckResult Allowed(
        SqlOSClientApplication client,
        string? userId,
        string? organizationId,
        string accessMode,
        string source,
        string? reason,
        string? assignmentId = null)
        => new(true, "allowed", accessMode, source, assignmentId, reason, client.Id, client.ClientId, organizationId, userId);

    private static SqlOSApplicationAccessCheckResult Denied(
        SqlOSClientApplication client,
        string? userId,
        string? organizationId,
        string accessMode,
        string source,
        string? reason,
        string? assignmentId = null)
        => new(false, "denied", accessMode, source, assignmentId, reason, client.Id, client.ClientId, organizationId, userId);

    private static NormalizedAssignmentRequest NormalizeAssignmentRequest(SqlOSCreateApplicationAssignmentRequest request)
    {
        var principalType = NormalizePrincipalType(request.PrincipalType);
        var access = NormalizeAssignmentAccess(request.Access);
        var principalId = NormalizeBoundedOptional(request.PrincipalId, "Application assignment principalId", 128);
        var organizationId = NormalizeBoundedOptional(request.OrganizationId, "Application assignment organizationId", 64);
        var roleKey = NormalizeBoundedOptional(request.RoleKey, "Application assignment roleKey", 80);

        switch (principalType)
        {
            case SqlOSApplicationAssignmentPrincipalTypes.Organization:
                organizationId ??= principalId;
                principalId = null;
                if (string.IsNullOrWhiteSpace(organizationId))
                {
                    throw new InvalidOperationException("Organization assignments require organizationId or principalId.");
                }
                break;
            case SqlOSApplicationAssignmentPrincipalTypes.User:
            case SqlOSApplicationAssignmentPrincipalTypes.Group:
            case SqlOSApplicationAssignmentPrincipalTypes.ServiceAccount:
            case SqlOSApplicationAssignmentPrincipalTypes.Agent:
                if (string.IsNullOrWhiteSpace(principalId))
                {
                    throw new InvalidOperationException($"{principalType} assignments require principalId.");
                }
                break;
            case SqlOSApplicationAssignmentPrincipalTypes.Role:
                if (string.IsNullOrWhiteSpace(organizationId))
                {
                    throw new InvalidOperationException("Role assignments require organizationId.");
                }
                if (string.IsNullOrWhiteSpace(roleKey))
                {
                    throw new InvalidOperationException("Role assignments require roleKey.");
                }
                break;
        }

        return new NormalizedAssignmentRequest(
            principalType,
            principalId,
            organizationId,
            roleKey,
            access,
            NormalizeBoundedOptional(request.Reason, "Application assignment reason", 500));
    }

    private static string? NormalizeBoundedOptional(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > maxLength) throw new InvalidOperationException($"{name} must be {maxLength} characters or fewer.");
        return normalized;
    }

    private static string NormalizePrincipalType(string value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            SqlOSApplicationAssignmentPrincipalTypes.Organization => SqlOSApplicationAssignmentPrincipalTypes.Organization,
            SqlOSApplicationAssignmentPrincipalTypes.User => SqlOSApplicationAssignmentPrincipalTypes.User,
            "user_group" => SqlOSApplicationAssignmentPrincipalTypes.Group,
            SqlOSApplicationAssignmentPrincipalTypes.Group => SqlOSApplicationAssignmentPrincipalTypes.Group,
            SqlOSApplicationAssignmentPrincipalTypes.Role => SqlOSApplicationAssignmentPrincipalTypes.Role,
            SqlOSApplicationAssignmentPrincipalTypes.ServiceAccount => SqlOSApplicationAssignmentPrincipalTypes.ServiceAccount,
            SqlOSApplicationAssignmentPrincipalTypes.Agent => SqlOSApplicationAssignmentPrincipalTypes.Agent,
            _ => throw new InvalidOperationException("Unsupported application assignment principal type.")
        };

    private static string NormalizeAssignmentAccess(string value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" => SqlOSApplicationAssignmentAccess.Allowed,
            SqlOSApplicationAssignmentAccess.Allowed => SqlOSApplicationAssignmentAccess.Allowed,
            SqlOSApplicationAssignmentAccess.Denied => SqlOSApplicationAssignmentAccess.Denied,
            _ => throw new InvalidOperationException("Application assignment access must be allowed or denied.")
        };

    private static string NormalizeAccessMode(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" => SqlOSApplicationAccessModes.AllOrganizations,
            SqlOSApplicationAccessModes.AllOrganizations => SqlOSApplicationAccessModes.AllOrganizations,
            SqlOSApplicationAccessModes.SelectedOrganizations => SqlOSApplicationAccessModes.SelectedOrganizations,
            SqlOSApplicationAccessModes.SelectedUsersGroupsRoles => SqlOSApplicationAccessModes.SelectedUsersGroupsRoles,
            SqlOSApplicationAccessModes.InternalOnly => SqlOSApplicationAccessModes.InternalOnly,
            SqlOSApplicationAccessModes.Disabled => SqlOSApplicationAccessModes.Disabled,
            _ => throw new InvalidOperationException("Unsupported application access mode.")
        };

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    public static string? NormalizeDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        var atIndex = normalized.LastIndexOf('@');
        if (atIndex >= 0)
        {
            normalized = normalized[(atIndex + 1)..];
        }

        normalized = normalized.Trim().Trim('.').Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public string GetServiceProviderEntityId() => _options.Issuer;

    public static string GetSsoSetupStatus(SqlOSSsoConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.IdentityProviderEntityId)
            || string.IsNullOrWhiteSpace(connection.SingleSignOnUrl)
            || string.IsNullOrWhiteSpace(connection.X509CertificatePem))
        {
            return "draft";
        }

        return connection.IsEnabled ? "active" : "ready_to_activate";
    }

    public string GetAssertionConsumerServiceUrl(string connectionId)
    {
        if (Uri.TryCreate(_options.Issuer, UriKind.Absolute, out var issuerUri))
        {
            var authority = issuerUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            var basePath = _options.BasePath.Trim();
            if (!basePath.StartsWith("/", StringComparison.Ordinal))
            {
                basePath = "/" + basePath;
            }

            return $"{authority}{basePath.TrimEnd('/')}/saml/acs/{connectionId}";
        }

        return $"{_options.Issuer.TrimEnd('/')}/saml/acs/{connectionId}";
    }

    public static List<string> NormalizeCallbackUris(IEnumerable<string>? values, string? connectionId = null)
        => values?
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ReplaceConnectionIdPlaceholder(value!, connectionId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList()
        ?? [];

    public static List<string> NormalizeScopes(IEnumerable<string>? values)
        => values?
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? [];

    public static SqlOSOidcClaimMapping NormalizeClaimMapping(SqlOSOidcClaimMapping? value)
    {
        value ??= new SqlOSOidcClaimMapping();

        return new SqlOSOidcClaimMapping
        {
            SubjectClaim = string.IsNullOrWhiteSpace(value.SubjectClaim) ? "sub" : value.SubjectClaim.Trim(),
            EmailClaim = NormalizeOptionalClaim(value.EmailClaim, "email"),
            EmailVerifiedClaim = NormalizeOptionalClaim(value.EmailVerifiedClaim, "email_verified"),
            DisplayNameClaim = NormalizeOptionalClaim(value.DisplayNameClaim, "name"),
            FirstNameClaim = NormalizeOptionalClaim(value.FirstNameClaim, "given_name"),
            LastNameClaim = NormalizeOptionalClaim(value.LastNameClaim, "family_name"),
            PreferredUsernameClaim = NormalizeOptionalClaim(value.PreferredUsernameClaim, "preferred_username")
        };
    }

    public static string Slugify(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }

    private static SqlOSFederationMetadata ParseFederationMetadata(string metadataXml)
    {
        var xml = new XmlDocument { PreserveWhitespace = false };
        xml.LoadXml(metadataXml);

        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("md", "urn:oasis:names:tc:SAML:2.0:metadata");
        ns.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");

        var entityId = xml.SelectSingleNode("/md:EntityDescriptor/@entityID", ns)?.InnerText
            ?? throw new InvalidOperationException("Federation metadata is missing the entityID attribute.");

        var ssoNode = xml.SelectSingleNode("//md:IDPSSODescriptor/md:SingleSignOnService[@Binding='urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect']", ns)
            ?? xml.SelectSingleNode("//md:IDPSSODescriptor/md:SingleSignOnService[@Binding='urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST']", ns)
            ?? throw new InvalidOperationException("Federation metadata is missing an IdP SingleSignOnService endpoint.");

        var ssoUrl = ssoNode.Attributes?["Location"]?.Value
            ?? throw new InvalidOperationException("Federation metadata SSO endpoint is missing its Location attribute.");

        var certificateNode = xml.SelectSingleNode("//md:IDPSSODescriptor/md:KeyDescriptor[@use='signing']//ds:X509Certificate", ns)
            ?? xml.SelectSingleNode("//md:IDPSSODescriptor/md:KeyDescriptor[not(@use)]//ds:X509Certificate", ns)
            ?? throw new InvalidOperationException("Federation metadata is missing an X509 signing certificate.");

        var certificateBase64 = string.Concat(certificateNode.InnerText.Where(ch => !char.IsWhiteSpace(ch)));
        if (string.IsNullOrWhiteSpace(certificateBase64))
        {
            throw new InvalidOperationException("Federation metadata certificate value is empty.");
        }

        var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificateBase64));
        var certificatePem = ToPem(certificate.Export(X509ContentType.Cert));

        return new SqlOSFederationMetadata(entityId, ssoUrl, certificatePem);
    }

    private static string ToPem(byte[] rawCertificate)
    {
        var base64 = Convert.ToBase64String(rawCertificate, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN CERTIFICATE-----\n{base64}\n-----END CERTIFICATE-----";
    }

    private static string? NormalizeMicrosoftTenant(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeOptionalClaim(string? value, string? fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeRequiredUrl(string? value, string message)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value.Trim();

    private static string? NormalizeOptionalUrl(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizePrivateKey(string value)
        => value.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string ReplaceConnectionIdPlaceholder(string value, string? connectionId)
        => string.IsNullOrWhiteSpace(connectionId)
            ? value
            : value.Replace("{connectionId}", connectionId, StringComparison.OrdinalIgnoreCase);

    private static void ValidateOidcSecretRequirements(SqlOSOidcConnection connection)
    {
        if (connection.ProviderType == SqlOSOidcProviderType.Apple)
        {
            if (string.IsNullOrWhiteSpace(connection.AppleTeamId) ||
                string.IsNullOrWhiteSpace(connection.AppleKeyId) ||
                string.IsNullOrWhiteSpace(connection.ApplePrivateKeyEncrypted))
            {
                throw new InvalidOperationException("Apple OIDC connections require team ID, key ID, and a private key.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(connection.ClientSecretEncrypted))
        {
            throw new InvalidOperationException("This social login connection requires a client secret.");
        }
    }

    private static List<string> NormalizeTrustValues(IEnumerable<string>? values)
    {
        var normalized = (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalized.Count > 32)
        {
            throw new InvalidOperationException("Upstream MFA trust policies support at most 32 accepted values.");
        }

        if (normalized.Any(static value => value.Length > 500))
        {
            throw new InvalidOperationException("Upstream MFA trust policy values cannot exceed 500 characters.");
        }

        return normalized;
    }

    private static NormalizedOidcConfiguration NormalizeOidcConfiguration(
        SqlOSOidcProviderType providerType,
        bool useDiscovery,
        string? discoveryUrl,
        string? issuer,
        string? authorizationEndpoint,
        string? tokenEndpoint,
        string? userInfoEndpoint,
        string? jwksUri,
        string? microsoftTenant,
        IEnumerable<string>? scopes,
        SqlOSOidcClaimMapping? claimMapping,
        SqlOSOidcClientAuthMethod? clientAuthMethod,
        bool? useUserInfo,
        string? appleTeamId,
        string? appleKeyId)
    {
        var normalizedScopes = NormalizeScopes(scopes);
        var normalizedClaimMapping = NormalizeClaimMapping(claimMapping);
        var normalizedTenant = providerType == SqlOSOidcProviderType.Microsoft ? NormalizeMicrosoftTenant(microsoftTenant) : null;
        var effectiveUseDiscovery = providerType != SqlOSOidcProviderType.Custom || useDiscovery;
        var effectiveClientAuthMethod = clientAuthMethod ?? SqlOSOidcClientAuthMethod.ClientSecretPost;
        var effectiveUseUserInfo = useUserInfo ?? providerType != SqlOSOidcProviderType.Apple;

        if (providerType == SqlOSOidcProviderType.Google)
        {
            return new NormalizedOidcConfiguration(
                true,
                "https://accounts.google.com/.well-known/openid-configuration",
                null,
                null,
                null,
                null,
                null,
                null,
                normalizedScopes,
                normalizedClaimMapping,
                effectiveClientAuthMethod,
                true,
                null,
                null,
                SqlOSSocialProviderProtocol.Oidc);
        }

        if (providerType == SqlOSOidcProviderType.Microsoft)
        {
            var tenant = normalizedTenant ?? "common";
            return new NormalizedOidcConfiguration(
                true,
                $"https://login.microsoftonline.com/{tenant}/v2.0/.well-known/openid-configuration",
                null,
                null,
                null,
                null,
                null,
                tenant,
                normalizedScopes,
                normalizedClaimMapping,
                effectiveClientAuthMethod,
                true,
                null,
                null,
                SqlOSSocialProviderProtocol.Oidc);
        }

        if (providerType == SqlOSOidcProviderType.Apple)
        {
            return new NormalizedOidcConfiguration(
                true,
                "https://appleid.apple.com/.well-known/openid-configuration",
                null,
                null,
                null,
                null,
                null,
                null,
                normalizedScopes,
                new SqlOSOidcClaimMapping
                {
                    SubjectClaim = "sub",
                    EmailClaim = "email",
                    EmailVerifiedClaim = "email_verified",
                    DisplayNameClaim = null,
                    FirstNameClaim = "given_name",
                    LastNameClaim = "family_name",
                    PreferredUsernameClaim = null
                },
                SqlOSOidcClientAuthMethod.ClientSecretPost,
                false,
                string.IsNullOrWhiteSpace(appleTeamId) ? null : appleTeamId.Trim(),
                string.IsNullOrWhiteSpace(appleKeyId) ? null : appleKeyId.Trim(),
                SqlOSSocialProviderProtocol.Oidc);
        }

        if (providerType == SqlOSOidcProviderType.GitHub)
        {
            return new NormalizedOidcConfiguration(
                false,
                null,
                "https://github.com",
                "https://github.com/login/oauth/authorize",
                "https://github.com/login/oauth/access_token",
                "https://api.github.com/user",
                null,
                null,
                normalizedScopes.Count > 0 ? normalizedScopes : ["read:user", "user:email"],
                new SqlOSOidcClaimMapping
                {
                    SubjectClaim = "id",
                    EmailClaim = "email",
                    EmailVerifiedClaim = "email_verified",
                    DisplayNameClaim = "name",
                    FirstNameClaim = null,
                    LastNameClaim = null,
                    PreferredUsernameClaim = "login"
                },
                SqlOSOidcClientAuthMethod.ClientSecretPost,
                true,
                null,
                null,
                SqlOSSocialProviderProtocol.OAuthProfile);
        }

        if (effectiveUseDiscovery)
        {
            return new NormalizedOidcConfiguration(
                true,
                NormalizeRequiredUrl(discoveryUrl, "A discovery URL is required for custom OIDC connections when discovery mode is enabled."),
                null,
                null,
                null,
                null,
                null,
                null,
                normalizedScopes,
                normalizedClaimMapping,
                effectiveClientAuthMethod,
                effectiveUseUserInfo,
                null,
                null,
                SqlOSSocialProviderProtocol.Oidc);
        }

        return new NormalizedOidcConfiguration(
            false,
            null,
            NormalizeRequiredUrl(issuer, "An issuer is required for manual OIDC connections."),
            NormalizeRequiredUrl(authorizationEndpoint, "An authorization endpoint is required for manual OIDC connections."),
            NormalizeRequiredUrl(tokenEndpoint, "A token endpoint is required for manual OIDC connections."),
            NormalizeOptionalUrl(userInfoEndpoint),
            NormalizeRequiredUrl(jwksUri, "A JWKS URI is required for manual OIDC connections."),
            null,
            normalizedScopes,
            normalizedClaimMapping,
            effectiveClientAuthMethod,
            effectiveUseUserInfo,
            null,
            null,
            SqlOSSocialProviderProtocol.Oidc);
    }

    private List<SqlOSClientSeedOptions> BuildStartupClientSeeds()
    {
        if (_options.SingleApplication == null)
        {
            return _options.ClientSeeds;
        }

        if (_options.ClientSeeds.Count > 0)
        {
            throw new InvalidOperationException("Single-application mode cannot be combined with explicit startup client seeds. Remove SeedClient/SeedBrowserClient calls or use the advanced multi-application setup path.");
        }

        return [BuildSingleApplicationSeed(_options.SingleApplication)];
    }

    private SqlOSClientSeedOptions BuildSingleApplicationSeed(SqlOSSingleApplicationOptions application)
    {
        var name = RequireText(application.Name, "Single application name");
        var clientId = string.IsNullOrWhiteSpace(application.ClientId)
            ? Slugify(name)
            : application.ClientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Single-application mode could not derive a valid client id from the application name.");
        }

        var redirectUris = NormalizeSingleApplicationRedirectUris(application);
        var allowedScopes = application.AllowedScopes
            .Where(static scope => !string.IsNullOrWhiteSpace(scope))
            .Select(static scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (allowedScopes.Count == 0)
        {
            allowedScopes = ["openid", "profile", "email", "offline_access"];
        }

        return new SqlOSClientSeedOptions
        {
            ClientId = clientId,
            Name = name,
            Audience = string.IsNullOrWhiteSpace(application.Audience)
                ? clientId
                : application.Audience.Trim(),
            RedirectUris = redirectUris,
            AllowedScopes = allowedScopes,
            ClientType = "public_pkce",
            RequirePkce = true,
            IsFirstParty = true,
            IsActive = true
        };
    }

    private static List<string> NormalizeSingleApplicationRedirectUris(SqlOSSingleApplicationOptions application)
    {
        var redirectUris = application.RedirectUris
            .Where(static uri => !string.IsNullOrWhiteSpace(uri))
            .Select(static uri => uri.Trim())
            .ToList();

        if (!string.IsNullOrWhiteSpace(application.Origin))
        {
            var origin = NormalizeOrigin(application.Origin);
            var redirectPath = string.IsNullOrWhiteSpace(application.RedirectPath)
                ? "/auth/callback"
                : application.RedirectPath.Trim();
            if (!redirectPath.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Single-application RedirectPath must start with '/'.");
            }

            redirectUris.Add($"{origin}{redirectPath}");
        }

        redirectUris = redirectUris
            .Select(NormalizeRedirectUri)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (redirectUris.Count == 0)
        {
            throw new InvalidOperationException("Single-application mode requires Origin or at least one RedirectUri.");
        }

        return redirectUris;
    }

    private static string NormalizeOrigin(string origin)
    {
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrWhiteSpace(uri.Query)
            || !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            throw new InvalidOperationException("Single-application Origin must be an absolute http(s) origin without query or fragment.");
        }

        var authority = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var path = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath.TrimEnd('/');
        return $"{authority}{path}";
    }

    private static string NormalizeRedirectUri(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            throw new InvalidOperationException($"Redirect URI '{redirectUri}' must be an absolute http(s) URI without a fragment.");
        }

        return redirectUri;
    }

    private NormalizedClientDefinition NormalizeSeededClient(SqlOSClientSeedOptions seed)
        => NormalizeClientDefinition(
            seed.ClientId,
            seed.Name,
            seed.Audience,
            seed.RedirectUris,
            seed.Description,
            seed.AllowedScopes,
            seed.RequirePkce,
            seed.IsFirstParty,
            seed.AllowNativeHeadlessAuth,
            seed.AllowDeviceAuthorization,
            seed.EnableClientCredentials,
            seed.ClientType,
            seed.IsActive);

    private static ClientAdminView FormatClientListItem(SqlOSClientApplication client, bool managedByStartupSeed, int duplicateCount)
    {
        var redirectUris = DeserializeJsonList(client.RedirectUrisJson);
        var grantTypes = DeserializeJsonList(client.GrantTypesJson);
        var responseTypes = DeserializeJsonList(client.ResponseTypesJson);
        var allowedScopes = DeserializeJsonList(client.AllowedScopesJson);
        var duplicateFingerprint = CalculateDuplicateFingerprint(client);

        return new ClientAdminView(
            client.Id,
            client.ClientId,
            client.Name,
            client.Description,
            client.Audience,
            NormalizeAccessMode(client.AccessMode),
            client.ClientType,
            client.RegistrationSource,
            GetSourceLabel(client.RegistrationSource),
            client.TokenEndpointAuthMethod,
            client.RequirePkce,
            client.IsFirstParty,
            client.AllowNativeHeadlessAuth,
            client.AllowDeviceAuthorization,
            redirectUris,
            grantTypes,
            responseTypes,
            allowedScopes,
            client.MetadataDocumentUrl,
            client.ClientUri,
            client.LogoUri,
            client.SoftwareId,
            client.SoftwareVersion,
            client.MetadataFetchedAt,
            client.MetadataExpiresAt,
            GetMetadataCacheState(client),
            client.LastSeenAt,
            client.IsActive,
            client.DisabledAt,
            client.DisabledReason,
            managedByStartupSeed,
            SqlOSConfigurationOwnershipPolicy.ToDto(client.ConfigurationOwner, client.ConfigurationSourceKey, client.LastReconciledAt, client.ConfigurationFingerprint, client.ConfigurationOrphanedAt),
            string.Equals(client.RegistrationSource, "manual", StringComparison.OrdinalIgnoreCase)
                || string.Equals(client.RegistrationSource, "seeded", StringComparison.OrdinalIgnoreCase),
            duplicateFingerprint,
            duplicateCount,
            client.DisabledAt != null || !client.IsActive ? "disabled" : "active",
            SqlOSClientAllowlistWarnings.ForEmptyAllowlist(
                allowedScopes,
                client.IsFirstParty,
                client.AllowNativeHeadlessAuth,
                client.AllowDeviceAuthorization,
                redirectUris,
                grantTypes),
            SqlOSOpenIdScopeWarnings.ForMissingAllowlistedOpenId(
                allowedScopes,
                client.IsFirstParty,
                client.AllowNativeHeadlessAuth,
                client.AllowDeviceAuthorization,
                redirectUris,
                grantTypes));
    }

    private static bool MatchesSourceFilter(string registrationSource, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(registrationSource, filter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesStatusFilter(bool isActive, DateTime? disabledAt, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return filter.Trim().ToLowerInvariant() switch
        {
            "active" => isActive && disabledAt == null,
            "disabled" => !isActive || disabledAt != null,
            _ => true
        };
    }

    private static bool MatchesClientSearch(ClientAdminView item, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var normalized = search.Trim();
        return (item.Name?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
            || item.ClientId.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || item.SourceLabel.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || (item.Description?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
            || item.Audience.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || (item.SoftwareId?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.SoftwareVersion?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.MetadataDocumentUrl?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string GetSourceLabel(string registrationSource)
        => registrationSource?.Trim().ToLowerInvariant() switch
        {
            "seeded" => "Seeded",
            "manual" => "Manual",
            "cimd" => "Discovered",
            "dcr" => "Registered",
            _ => "Unknown"
        };

    private static string? GetMetadataCacheState(SqlOSClientApplication client)
    {
        if (!string.Equals(client.RegistrationSource, "cimd", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (client.MetadataExpiresAt == null)
        {
            return "unknown";
        }

        return client.MetadataExpiresAt <= DateTime.UtcNow
            ? "stale"
            : "fresh";
    }

    private async Task<int> CountDuplicateClientsAsync(
        SqlOSClientApplication client,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var candidates = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .Where(x => x.RegistrationSource == "dcr"
                && x.SoftwareId == client.SoftwareId
                && x.SoftwareVersion == client.SoftwareVersion
                && x.ClientUri == client.ClientUri)
            .Select(x => new { x.RegistrationSource, x.SoftwareId, x.SoftwareVersion, x.ClientUri, x.RedirectUrisJson })
            .ToListAsync(cancellationToken);

        return candidates.Count(x => string.Equals(
            CalculateDuplicateFingerprint(x.RegistrationSource, x.SoftwareId, x.SoftwareVersion, x.ClientUri, x.RedirectUrisJson),
            fingerprint,
            StringComparison.Ordinal));
    }

    private static string? CalculateDuplicateFingerprint(SqlOSClientApplication client)
        => CalculateDuplicateFingerprint(
            client.RegistrationSource,
            client.SoftwareId,
            client.SoftwareVersion,
            client.ClientUri,
            client.RedirectUrisJson);

    private static string? CalculateDuplicateFingerprint(
        string? registrationSource,
        string? softwareId,
        string? softwareVersion,
        string? clientUri,
        string? redirectUrisJson)
    {
        if (!string.Equals(registrationSource, "dcr", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var redirectUris = DeserializeJsonList(redirectUrisJson);
        return string.Join("|", redirectUris.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
            + $"|{softwareId ?? string.Empty}|{softwareVersion ?? string.Empty}|{clientUri ?? string.Empty}";
    }

    private HashSet<string> GetStartupManagedClientIds()
    {
        var ids = _options.ClientSeeds
            .Select(static seed => seed.ClientId)
            .Where(static clientId => !string.IsNullOrWhiteSpace(clientId))
            .Select(static clientId => clientId.Trim())
            .ToHashSet(StringComparer.Ordinal);

        if (_options.SingleApplication != null)
        {
            var singleClientId = string.IsNullOrWhiteSpace(_options.SingleApplication.ClientId)
                ? Slugify(_options.SingleApplication.Name)
                : _options.SingleApplication.ClientId.Trim();
            if (!string.IsNullOrWhiteSpace(singleClientId))
            {
                ids.Add(singleClientId);
            }
        }

        return ids;
    }

    private async Task<SqlOSClientApplication> GetRequiredClientByIdAsync(string clientApplicationId, CancellationToken cancellationToken)
    {
        var client = await _context.Set<SqlOSClientApplication>()
            .FirstOrDefaultAsync(x => x.Id == clientApplicationId, cancellationToken);
        if (client != null)
        {
            return client;
        }

        client = await _context.Set<SqlOSClientApplication>()
            .FirstOrDefaultAsync(x => x.ClientId == clientApplicationId, cancellationToken);
        return client ?? throw new InvalidOperationException("Client application was not found.");
    }

    private async Task<int> RevokeClientSessionsInternalAsync(string clientApplicationId, string reason, CancellationToken cancellationToken)
    {
        var sessions = await _context.Set<SqlOSSession>()
            .Where(x => x.ClientApplicationId == clientApplicationId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var sessionIds = sessions.Select(x => x.Id).ToList();
        var refreshTokens = await _context.Set<SqlOSRefreshToken>()
            .Where(x => sessionIds.Contains(x.SessionId) && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.RevokedAt = now;
            session.RevocationReason = reason;
        }

        foreach (var refreshToken in refreshTokens)
        {
            refreshToken.RevokedAt = now;
        }

        return sessions.Count;
    }

    private NormalizedClientDefinition NormalizeClientRequest(SqlOSCreateClientRequest request)
        => NormalizeClientDefinition(
            request.ClientId,
            request.Name,
            request.Audience,
            request.RedirectUris,
            request.Description,
            request.AllowedScopes,
            request.RequirePkce,
            request.IsFirstParty,
            request.AllowNativeHeadlessAuth,
            request.AllowDeviceAuthorization,
            false,
            request.ClientType,
            true);

    private NormalizedClientDefinition NormalizeClientDefinition(
        string clientId,
        string name,
        string? audience,
        IEnumerable<string>? redirectUris,
        string? description,
        IEnumerable<string>? allowedScopes,
        bool requirePkce,
        bool isFirstParty,
        bool allowNativeHeadlessAuth,
        bool allowDeviceAuthorization,
        bool enableClientCredentials,
        string? clientType,
        bool isActive)
    {
        var normalizedClientId = RequireText(clientId, nameof(clientId));
        var normalizedName = RequireText(name, nameof(name));
        var normalizedAudience = string.IsNullOrWhiteSpace(audience)
            ? _options.DefaultAudience
            : audience.Trim();
        var normalizedClientType = string.IsNullOrWhiteSpace(clientType)
            ? "public_pkce"
            : clientType.Trim();
        if (normalizedClientType is not ("public_pkce" or "public_cli" or "confidential"))
        {
            throw new InvalidOperationException($"Client '{normalizedClientId}' has unsupported client type '{normalizedClientType}'.");
        }
        if (enableClientCredentials && !string.Equals(normalizedClientType, "confidential", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Client '{normalizedClientId}' must be confidential to enable client_credentials.");
        }
        if (allowDeviceAuthorization && string.Equals(normalizedClientType, "confidential", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Client '{normalizedClientId}' cannot combine device authorization with confidential client authentication.");
        }
        var normalizedRedirectUris = (redirectUris ?? [])
            .Where(static uri => !string.IsNullOrWhiteSpace(uri))
            .Select(static uri => uri.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalizedRedirectUris.Count == 0 && !allowDeviceAuthorization && !enableClientCredentials)
        {
            throw new InvalidOperationException($"Client '{normalizedClientId}' must define at least one redirect URI.");
        }

        var normalizedAllowedScopes = (allowedScopes ?? [])
            .Where(static scope => !string.IsNullOrWhiteSpace(scope))
            .Select(static scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new NormalizedClientDefinition(
            normalizedClientId,
            normalizedName,
            normalizedAudience,
            normalizedRedirectUris,
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            normalizedAllowedScopes,
            normalizedClientType,
            requirePkce,
            isFirstParty,
            allowNativeHeadlessAuth,
            allowDeviceAuthorization,
            BuildGrantTypes(normalizedRedirectUris, allowDeviceAuthorization, enableClientCredentials),
            enableClientCredentials,
            isActive);
    }

    private static List<string> BuildGrantTypes(
        IReadOnlyCollection<string> redirectUris,
        bool allowDeviceAuthorization,
        bool enableClientCredentials)
    {
        var grants = new List<string>();
        if (redirectUris.Count > 0)
        {
            grants.Add(SqlOSOAuthGrantTypes.AuthorizationCode);
        }

        if (allowDeviceAuthorization)
        {
            grants.Add(SqlOSOAuthGrantTypes.DeviceCode);
        }

        if (enableClientCredentials)
        {
            grants.Add(SqlOSOAuthGrantTypes.ClientCredentials);
        }

        if (redirectUris.Count > 0 || allowDeviceAuthorization)
        {
            grants.Add(SqlOSOAuthGrantTypes.RefreshToken);
        }
        return grants.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string ResolveTokenEndpointAuthMethod(string clientType)
        => string.Equals(clientType, "confidential", StringComparison.Ordinal)
            ? "client_secret_basic"
            : "none";

    private static string RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return value.Trim();
    }

    internal static List<string> DeserializeJsonList(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed record ClientAdminView(
        string Id,
        string ClientId,
        string Name,
        string? Description,
        string Audience,
        string AccessMode,
        string ClientType,
        string RegistrationSource,
        string SourceLabel,
        string TokenEndpointAuthMethod,
        bool RequirePkce,
        bool IsFirstParty,
        bool AllowNativeHeadlessAuth,
        bool AllowDeviceAuthorization,
        List<string> RedirectUris,
        List<string> GrantTypes,
        List<string> ResponseTypes,
        List<string> AllowedScopes,
        string? MetadataDocumentUrl,
        string? ClientUri,
        string? LogoUri,
        string? SoftwareId,
        string? SoftwareVersion,
        DateTime? MetadataFetchedAt,
        DateTime? MetadataExpiresAt,
        string? MetadataCacheState,
        DateTime? LastSeenAt,
        bool IsActive,
        DateTime? DisabledAt,
        string? DisabledReason,
        bool ManagedByStartupSeed,
        SqlOSConfigurationOwnershipDto Ownership,
        bool CoreMetadataEditable,
        string? DuplicateFingerprint,
        int DuplicateCount,
        string LifecycleState,
        SqlOSClientAllowlistWarning? EmptyAllowlistWarning,
        SqlOSOpenIdScopeWarning? OmittedOpenIdWarning);

    private sealed record SqlOSFederationMetadata(
        string IdentityProviderEntityId,
        string SingleSignOnUrl,
        string X509CertificatePem);

    private sealed record NormalizedOidcConfiguration(
        bool UseDiscovery,
        string? DiscoveryUrl,
        string? Issuer,
        string? AuthorizationEndpoint,
        string? TokenEndpoint,
        string? UserInfoEndpoint,
        string? JwksUri,
        string? MicrosoftTenant,
        List<string> Scopes,
        SqlOSOidcClaimMapping ClaimMapping,
        SqlOSOidcClientAuthMethod ClientAuthMethod,
        bool UseUserInfo,
        string? AppleTeamId,
        string? AppleKeyId,
        SqlOSSocialProviderProtocol Protocol);

    private sealed record NormalizedClientDefinition(
        string ClientId,
        string Name,
        string Audience,
        List<string> RedirectUris,
        string? Description,
        List<string> AllowedScopes,
        string ClientType,
        bool RequirePkce,
        bool IsFirstParty,
        bool AllowNativeHeadlessAuth,
        bool AllowDeviceAuthorization,
        List<string> GrantTypes,
        bool EnableClientCredentials,
        bool IsActive);

    private sealed record NormalizedAssignmentRequest(
        string PrincipalType,
        string? PrincipalId,
        string? OrganizationId,
        string? RoleKey,
        string Access,
        string? Reason);
}
