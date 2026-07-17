using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Fga.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSMachineClientAdminService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _admin;
    private readonly SqlOSCryptoService _crypto;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSMachineClientAdminService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService admin,
        SqlOSCryptoService crypto,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _admin = admin;
        _crypto = crypto;
        _options = options.Value;
    }

    public async Task UpsertSeededMachineClientsAsync(CancellationToken cancellationToken = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
            {
                await _context.Database.ExecuteSqlRawAsync("""
                    DECLARE @result int;
                    EXEC @result = sys.sp_getapplock @Resource=N'SqlOS:MachineClientReconciliation', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000;
                    IF @result < 0 THROW 51000, 'Could not acquire the machine-client reconciliation lock.', 1;
                    """, cancellationToken);
            }
            await UpsertSeededMachineClientsCoreAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task<SqlOSMachineClientCreated> CreateAsync(SqlOSCreateMachineClientRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = await NormalizeCreateAsync(request, cancellationToken);
        var secret = GenerateSecret();
        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (await _context.Set<SqlOSClientApplication>().AnyAsync(x => x.ClientId == normalized.ClientId, cancellationToken)
            || await _context.Set<SqlOSFgaServiceAccount>().AnyAsync(x => x.ClientId == normalized.ClientId, cancellationToken))
        {
            throw new InvalidOperationException("A client with that client ID already exists.");
        }

        var now = DateTime.UtcNow;
        var client = CreateClient(normalized, now);
        var subject = new SqlOSFgaSubject
        {
            Id = _crypto.GenerateId("sub"), SubjectTypeId = "service_account", OrganizationId = normalized.OrganizationId,
            DisplayName = normalized.DisplayName, ExternalRef = normalized.ClientId, CreatedAt = now, UpdatedAt = now
        };
        var account = new SqlOSFgaServiceAccount
        {
            Id = _crypto.GenerateId("sa"), SubjectId = subject.Id, ClientId = normalized.ClientId,
            ClientSecretHash = _crypto.HashPassword(secret), Description = normalized.Description, ExpiresAt = normalized.ExpiresAt,
            ConfigurationOwner = SqlOSConfigurationOwners.Dashboard, CreatedAt = now, UpdatedAt = now
        };
        _context.Set<SqlOSClientApplication>().Add(client);
        _context.Set<SqlOSFgaSubject>().Add(subject);
        _context.Set<SqlOSFgaServiceAccount>().Add(account);
        AddGrants(subject.Id, normalized.Grants, now, marker: null);
        await _context.SaveChangesAsync(cancellationToken);
        await _admin.RecordAuditAsync("machine_client.created", "admin", null, organizationId: normalized.OrganizationId,
            data: new { normalized.ClientId, subjectId = subject.Id, grantCount = normalized.Grants.Count }, cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SqlOSMachineClientCreated(ToDto(client, account, subject, normalized.Grants.Count), secret);
    }

    public async Task<IReadOnlyList<SqlOSMachineClientDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _context.Set<SqlOSFgaServiceAccount>().AsNoTracking().Include(x => x.Subject)
            .OrderBy(x => x.Subject!.DisplayName).ToListAsync(cancellationToken);
        var clients = await _context.Set<SqlOSClientApplication>().AsNoTracking()
            .Where(x => rows.Select(row => row.ClientId).Contains(x.ClientId)).ToDictionaryAsync(x => x.ClientId, cancellationToken);
        var subjects = rows.Select(x => x.SubjectId).ToArray();
        var counts = await _context.Set<SqlOSFgaGrant>().AsNoTracking().Where(x => subjects.Contains(x.SubjectId))
            .GroupBy(x => x.SubjectId).Select(x => new { x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        return rows.Where(x => clients.ContainsKey(x.ClientId))
            .Select(x => ToDto(clients[x.ClientId], x, x.Subject!, counts.GetValueOrDefault(x.SubjectId))).ToArray();
    }

    public async Task<SqlOSMachineClientCreated> RotateAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var (client, account, subject) = await RequireAsync(clientId, cancellationToken);
        EnsureDashboardOwned(account);
        var secret = GenerateSecret();
        account.ClientSecretHash = _crypto.HashPassword(secret);
        account.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await _admin.RecordAuditAsync("machine_client.secret_rotated", "admin", null, organizationId: subject.OrganizationId,
            data: new { clientId, subjectId = subject.Id }, cancellationToken: cancellationToken);
        var grantCount = await _context.Set<SqlOSFgaGrant>().CountAsync(x => x.SubjectId == subject.Id, cancellationToken);
        return new SqlOSMachineClientCreated(ToDto(client, account, subject, grantCount), secret);
    }

    public async Task<SqlOSMachineClientValidation> ValidateCredentialAsync(string clientId, string secret, string resource, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
    {
        var (client, account, subject) = await RequireAsync(clientId, cancellationToken);
        var now = DateTime.UtcNow;
        var valid = client.IsActive && client.DisabledAt == null && (account.ExpiresAt == null || account.ExpiresAt > now)
            && secret.Length is >= 43 and <= 256 && _crypto.VerifyPassword(SqlOSClientCredentialsService.ResolveCredentialHashForVerification(account), secret)
            && string.Equals(resource, client.Audience, StringComparison.Ordinal)
            && scopes.All(scope => SqlOSAdminService.DeserializeJsonList(client.AllowedScopesJson).Contains(scope, StringComparer.Ordinal));
        await _admin.RecordAuditAsync(valid ? "machine_client.credential_test_succeeded" : "machine_client.credential_test_failed",
            "admin", null, organizationId: subject.OrganizationId, data: new { clientId, resource, scopes }, cancellationToken: cancellationToken);
        return new SqlOSMachineClientValidation(valid, valid ? "ready" : "credential_or_binding_invalid");
    }

    public async Task RevokeAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var (client, account, subject) = await RequireAsync(clientId, cancellationToken);
        account.ExpiresAt = DateTime.UtcNow;
        account.UpdatedAt = account.ExpiresAt.Value;
        client.IsActive = false;
        client.DisabledAt = account.ExpiresAt;
        client.DisabledReason = "machine_client_revoked";
        await _context.SaveChangesAsync(cancellationToken);
        await _admin.RecordAuditAsync("machine_client.revoked", "admin", null, organizationId: subject.OrganizationId,
            data: new { clientId, subjectId = subject.Id }, cancellationToken: cancellationToken);
    }

    public async Task AddGrantAsync(string clientId, SqlOSMachineClientGrantRequest request, CancellationToken cancellationToken = default)
    {
        var (_, account, subject) = await RequireAsync(clientId, cancellationToken);
        EnsureDashboardOwned(account);
        var grants = await NormalizeGrantsAsync([new(request.ResourceId, request.RoleId, request.Description)], cancellationToken);
        if (!await _context.Set<SqlOSFgaGrant>().AnyAsync(x => x.SubjectId == subject.Id && x.ResourceId == request.ResourceId && x.RoleId == request.RoleId, cancellationToken))
        {
            AddGrants(subject.Id, grants, DateTime.UtcNow, marker: null);
            await _context.SaveChangesAsync(cancellationToken);
            await _admin.RecordAuditAsync("machine_client.grant_added", "admin", null, organizationId: subject.OrganizationId,
                data: new { clientId, request.ResourceId, request.RoleId }, cancellationToken: cancellationToken);
        }
    }

    public async Task RemoveGrantAsync(string clientId, string grantId, CancellationToken cancellationToken = default)
    {
        var (_, account, subject) = await RequireAsync(clientId, cancellationToken);
        EnsureDashboardOwned(account);
        var grant = await _context.Set<SqlOSFgaGrant>().SingleOrDefaultAsync(x => x.Id == grantId && x.SubjectId == subject.Id, cancellationToken)
            ?? throw new InvalidOperationException("Grant not found.");
        _context.Set<SqlOSFgaGrant>().Remove(grant);
        await _context.SaveChangesAsync(cancellationToken);
        await _admin.RecordAuditAsync("machine_client.grant_removed", "admin", null, organizationId: subject.OrganizationId,
            data: new { clientId, grantId, grant.ResourceId, grant.RoleId }, cancellationToken: cancellationToken);
    }

    private async Task UpsertSeededMachineClientsCoreAsync(CancellationToken cancellationToken)
    {
        var seeds = _options.ClientSeeds.Where(x => x.MachineClient != null).ToArray();
        var keys = seeds.Select(x => x.ClientId.Trim()).ToHashSet(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var orphans = await _context.Set<SqlOSFgaServiceAccount>()
            .Where(x => x.ConfigurationOwner == SqlOSConfigurationOwners.Code && x.ConfigurationSourceKey != null && !keys.Contains(x.ConfigurationSourceKey))
            .ToListAsync(cancellationToken);
        foreach (var orphan in orphans)
        {
            if (orphan.ConfigurationOrphanedAt != null) continue;
            orphan.ConfigurationOrphanedAt = now;
            await _admin.RecordAuditAsync("configuration.orphaned", "system", "startup",
                data: new { resourceType = "machine_client", clientId = orphan.ClientId, sourceKey = orphan.ConfigurationSourceKey }, cancellationToken: cancellationToken);
        }

        foreach (var seed in seeds)
        {
            var machine = seed.MachineClient!;
            var sourceKey = seed.ClientId.Trim();
            var client = await _context.Set<SqlOSClientApplication>().SingleOrDefaultAsync(x => x.ClientId == sourceKey, cancellationToken)
                ?? throw new InvalidOperationException($"Machine client '{sourceKey}' has no reconciled OAuth client.");
            var organizationId = await ResolveOrganizationIdAsync(machine.OrganizationId, machine.OrganizationSlug, cancellationToken);
            var grants = await NormalizeGrantsAsync(machine.Grants, cancellationToken);
            var fingerprint = SqlOSConfigurationOwnershipPolicy.Fingerprint(new
            {
                sourceKey, organizationId, seed.Name, seed.Description, seed.Audience, seed.AllowedScopes, machine.ExpiresAt,
                Grants = grants.Select(x => new { x.ResourceId, x.RoleId, x.Description })
            });
            var account = await _context.Set<SqlOSFgaServiceAccount>().Include(x => x.Subject)
                .SingleOrDefaultAsync(x => x.ConfigurationSourceKey == sourceKey || x.ClientId == sourceKey, cancellationToken);
            if (account != null)
            {
                SqlOSConfigurationOwnershipPolicy.EnsureCodeOwnership(account.ConfigurationOwner, account.ConfigurationSourceKey, sourceKey, $"Machine client '{sourceKey}'");
                if (!string.Equals(account.Subject!.OrganizationId, organizationId, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Machine client '{sourceKey}' cannot move between organizations.");
            }

            var secretHash = ResolveSeedSecretHash(machine, account);
            var isNew = account == null;
            if (account == null)
            {
                var subject = new SqlOSFgaSubject
                {
                    Id = _crypto.GenerateId("sub"), SubjectTypeId = "service_account", OrganizationId = organizationId,
                    DisplayName = seed.Name, ExternalRef = sourceKey, CreatedAt = now, UpdatedAt = now
                };
                account = new SqlOSFgaServiceAccount
                {
                    Id = _crypto.GenerateId("sa"), SubjectId = subject.Id, Subject = subject, ClientId = sourceKey,
                    ClientSecretHash = secretHash, Description = seed.Description, ExpiresAt = machine.ExpiresAt,
                    ConfigurationOwner = SqlOSConfigurationOwners.Code, ConfigurationSourceKey = sourceKey, CreatedAt = now
                };
                _context.Set<SqlOSFgaSubject>().Add(subject);
                _context.Set<SqlOSFgaServiceAccount>().Add(account);
            }
            var marker = $"[sqlos-machine:{sourceKey}]";
            var old = await _context.Set<SqlOSFgaGrant>().Where(x => x.SubjectId == account.SubjectId && x.Description != null && x.Description.StartsWith(marker)).ToListAsync(cancellationToken);
            var desiredGrants = grants.Select(x => (x.ResourceId, x.RoleId, Description: $"{marker} {x.Description}".Trim()))
                .OrderBy(x => x.ResourceId, StringComparer.Ordinal).ThenBy(x => x.RoleId, StringComparer.Ordinal).ToArray();
            var currentGrants = old.Select(x => (x.ResourceId, x.RoleId, x.Description!))
                .OrderBy(x => x.ResourceId, StringComparer.Ordinal).ThenBy(x => x.RoleId, StringComparer.Ordinal).ToArray();
            var grantDrift = !desiredGrants.SequenceEqual(currentGrants);
            var changed = isNew || account.ConfigurationFingerprint != fingerprint || account.ConfigurationOrphanedAt != null
                || account.ClientSecretHash != secretHash || grantDrift;
            if (!changed) continue;

            account.Subject!.DisplayName = seed.Name;
            account.Description = seed.Description;
            account.ClientSecretHash = secretHash;
            account.ExpiresAt = machine.ExpiresAt;
            account.ConfigurationFingerprint = fingerprint;
            account.LastReconciledAt = now;
            account.ConfigurationOrphanedAt = null;
            account.UpdatedAt = now;

            _context.Set<SqlOSFgaGrant>().RemoveRange(old);
            AddGrants(account.SubjectId, grants, now, marker);
            await _admin.RecordAuditAsync("configuration.reconciled", "system", "startup", organizationId: organizationId,
                data: new { resourceType = "machine_client", clientId = sourceKey, subjectId = account.SubjectId, owner = SqlOSConfigurationOwners.Code, fingerprint }, cancellationToken: cancellationToken);
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    private string ResolveSeedSecretHash(SqlOSMachineClientSeedOptions machine, SqlOSFgaServiceAccount? account)
    {
        if ((machine.SecretResolver == null) == (machine.SecretHashResolver == null))
            throw new InvalidOperationException("Machine-client seeds require exactly one secret or secret-hash resolver.");
        if (machine.SecretHashResolver != null)
        {
            var hash = machine.SecretHashResolver()?.Trim();
            if (string.IsNullOrWhiteSpace(hash)) throw new InvalidOperationException("Machine-client secret hash resolution returned no value.");
            if (!IsSupportedPasswordHash(hash)) throw new InvalidOperationException("Machine-client secret hash resolution returned an unsupported PasswordHasher payload.");
            return hash;
        }
        var secret = machine.SecretResolver!()?.Trim();
        if (string.IsNullOrWhiteSpace(secret) || secret.Length is < 43 or > 256)
            throw new InvalidOperationException("Machine-client secret resolution must return 43 to 256 characters.");
        return account != null && _crypto.VerifyPassword(account.ClientSecretHash, secret) ? account.ClientSecretHash : _crypto.HashPassword(secret);
    }

    private async Task<NormalizedCreate> NormalizeCreateAsync(SqlOSCreateMachineClientRequest request, CancellationToken cancellationToken)
    {
        var clientId = Require(request.ClientId, "Client ID is required.", 200);
        var displayName = Require(request.DisplayName, "Display name is required.", 200);
        var audience = Require(request.Audience, "Audience is required.", 500);
        var organizationId = await ResolveOrganizationIdAsync(request.OrganizationId, null, cancellationToken);
        var scopes = request.Scopes.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (scopes.Length == 0) throw new InvalidOperationException("At least one scope is required.");
        var grants = await NormalizeGrantsAsync(request.Grants, cancellationToken);
        return new(clientId, displayName, request.Description?.Trim(), audience, scopes, organizationId, request.ExpiresAt, grants);
    }

    private async Task<string?> ResolveOrganizationIdAsync(string? organizationId, string? organizationSlug, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(organizationId) && !string.IsNullOrWhiteSpace(organizationSlug))
            throw new InvalidOperationException("Specify an organization ID or slug, not both.");
        if (string.IsNullOrWhiteSpace(organizationId) && string.IsNullOrWhiteSpace(organizationSlug)) return null;
        var organization = await _context.Set<SqlOSOrganization>().AsNoTracking().SingleOrDefaultAsync(x =>
            !string.IsNullOrWhiteSpace(organizationId) ? x.Id == organizationId.Trim() : x.Slug == organizationSlug!.Trim(), cancellationToken);
        return organization?.Id ?? throw new InvalidOperationException("Organization not found.");
    }

    private async Task<IReadOnlyList<SqlOSMachineClientGrantSeedOptions>> NormalizeGrantsAsync(IEnumerable<SqlOSMachineClientGrantSeedOptions> grants, CancellationToken cancellationToken)
    {
        var normalized = grants.Select(x => new SqlOSMachineClientGrantSeedOptions(Require(x.ResourceId, "Grant resource is required.", 450), Require(x.RoleId, "Grant role is required.", 450), x.Description?.Trim()))
            .DistinctBy(x => (x.ResourceId, x.RoleId)).ToArray();
        var resourceIds = normalized.Select(x => x.ResourceId).Distinct().ToArray();
        var roleIds = normalized.Select(x => x.RoleId).Distinct().ToArray();
        if (await _context.Set<SqlOSFgaResource>().CountAsync(x => resourceIds.Contains(x.Id), cancellationToken) != resourceIds.Length) throw new InvalidOperationException("One or more grant resources do not exist.");
        if (await _context.Set<SqlOSFgaRole>().CountAsync(x => roleIds.Contains(x.Id), cancellationToken) != roleIds.Length) throw new InvalidOperationException("One or more grant roles do not exist.");
        return normalized;
    }

    private void AddGrants(string subjectId, IReadOnlyList<SqlOSMachineClientGrantSeedOptions> grants, DateTime now, string? marker)
    {
        foreach (var grant in grants) _context.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
        {
            Id = _crypto.GenerateId("grant"), SubjectId = subjectId, ResourceId = grant.ResourceId, RoleId = grant.RoleId,
            Description = marker == null ? grant.Description : $"{marker} {grant.Description}".Trim(), CreatedAt = now, UpdatedAt = now
        });
    }

    private SqlOSClientApplication CreateClient(NormalizedCreate request, DateTime now) => new()
    {
        Id = _crypto.GenerateId("cli"), ClientId = request.ClientId, Name = request.DisplayName, Description = request.Description,
        Audience = request.Audience, ClientType = "confidential", RegistrationSource = "admin", TokenEndpointAuthMethod = "client_secret_basic",
        GrantTypesJson = JsonSerializer.Serialize(new[] { SqlOSOAuthGrantTypes.ClientCredentials }), ResponseTypesJson = "[]", RequirePkce = false,
        AllowedScopesJson = JsonSerializer.Serialize(request.Scopes), RedirectUrisJson = "[]", IsActive = true,
        AccessMode = SqlOSApplicationAccessModes.AllOrganizations, CreatedAt = now
    };

    private async Task<(SqlOSClientApplication Client, SqlOSFgaServiceAccount Account, SqlOSFgaSubject Subject)> RequireAsync(string clientId, CancellationToken cancellationToken)
    {
        var account = await _context.Set<SqlOSFgaServiceAccount>().Include(x => x.Subject).SingleOrDefaultAsync(x => x.ClientId == clientId, cancellationToken)
            ?? throw new InvalidOperationException("Machine client not found.");
        var client = await _context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == clientId, cancellationToken);
        return (client, account, account.Subject!);
    }

    private static void EnsureDashboardOwned(SqlOSFgaServiceAccount account)
    {
        if (!string.Equals(account.ConfigurationOwner, SqlOSConfigurationOwners.Dashboard, StringComparison.Ordinal))
            throw new InvalidOperationException("This machine client is code-owned. Change its seed and secret resolver instead.");
    }

    private static SqlOSMachineClientDto ToDto(SqlOSClientApplication client, SqlOSFgaServiceAccount account, SqlOSFgaSubject subject, int grantCount)
        => new(client.ClientId, subject.DisplayName, account.Description, client.Audience, SqlOSAdminService.DeserializeJsonList(client.AllowedScopesJson), subject.OrganizationId,
            account.ExpiresAt, account.LastUsedAt, client.IsActive && client.DisabledAt == null && (account.ExpiresAt == null || account.ExpiresAt > DateTime.UtcNow),
            account.ConfigurationOwner, account.ConfigurationSourceKey, account.ConfigurationOrphanedAt, grantCount);

    private static string GenerateSecret() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
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
    private static string Require(string? value, string message, int max)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new InvalidOperationException(message);
        if (normalized.Length > max) throw new InvalidOperationException($"{message} Maximum length is {max}.");
        return normalized;
    }

    private sealed record NormalizedCreate(string ClientId, string DisplayName, string? Description, string Audience, IReadOnlyList<string> Scopes, string? OrganizationId, DateTime? ExpiresAt, IReadOnlyList<SqlOSMachineClientGrantSeedOptions> Grants);
}

public sealed record SqlOSCreateMachineClientRequest(string ClientId, string DisplayName, string? Description, string Audience, IReadOnlyList<string> Scopes, string? OrganizationId, DateTime? ExpiresAt, IReadOnlyList<SqlOSMachineClientGrantSeedOptions> Grants);
public sealed record SqlOSMachineClientGrantRequest(string ResourceId, string RoleId, string? Description = null);
public sealed record SqlOSMachineClientDto(string ClientId, string DisplayName, string? Description, string Audience, IReadOnlyList<string> Scopes, string? OrganizationId, DateTime? ExpiresAt, DateTime? LastUsedAt, bool Ready, string ConfigurationOwner, string? ConfigurationSourceKey, DateTime? ConfigurationOrphanedAt, int GrantCount);
public sealed record SqlOSMachineClientCreated(SqlOSMachineClientDto Client, string ClientSecret);
public sealed record SqlOSMachineClientValidation(bool Valid, string Status);
