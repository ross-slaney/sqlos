using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.Pagination;

namespace SqlOS.AuthServer.Services;

public sealed partial class SqlOSAdminService
{
    private const int MinimumScimBearerTokenLength = 32;
    private const int ScimTokenPrefixLength = 12;
    private const int ScimOperationCommitCleanupBatchSize = 256;
    private const int ScimOperationCommitStartupCleanupBatchSize = 1000;
    private const int ScimRevocationEvidenceLimit = 32;
    private static readonly TimeSpan ScimOperationCommitRetention = TimeSpan.FromDays(1);

    public async Task CleanupExpiredScimOperationCommitsAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - ScimOperationCommitRetention;
        while (true)
        {
            var staged = await StageExpiredScimOperationCommitsAsync(
                cutoff,
                ScimOperationCommitStartupCleanupBatchSize,
                cancellationToken);
            if (staged == 0)
            {
                return;
            }
            await _context.SaveChangesAsync(cancellationToken);
            if (staged < ScimOperationCommitStartupCleanupBatchSize)
            {
                return;
            }
        }
    }

    private async Task<int> StageExpiredScimOperationCommitsAsync(
        DateTime cutoff,
        int maxRows,
        CancellationToken cancellationToken)
    {
        var expired = await _context.Set<SqlOSScimOperationCommit>()
            .Where(marker => marker.OccurredAt < cutoff)
            .OrderBy(marker => marker.OccurredAt)
            .ThenBy(marker => marker.Id)
            .Take(maxRows)
            .ToListAsync(cancellationToken);
        _context.Set<SqlOSScimOperationCommit>().RemoveRange(expired);
        return expired.Count;
    }

    public async Task ReconcileDisabledScimManagedGrantsAsync(CancellationToken cancellationToken = default)
    {
        var connectionIds = await _context.Set<SqlOSScimManagedGrant>()
            .AsNoTracking()
            .Where(managed => managed.RevokedAt == null && managed.Connection != null && !managed.Connection.IsEnabled)
            .Select(managed => managed.ConnectionId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (connectionIds.Count == 0)
        {
            return;
        }

        await RunScimAdminAtomicAsync(async () =>
        {
            foreach (var connectionId in connectionIds)
            {
                await RevokeManagedGrantsAsync(connectionId, mappingId: null, cancellationToken: cancellationToken);
            }
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task UpsertSeededScimConnectionsAsync(CancellationToken cancellationToken = default)
    {
        if (_options.ScimConnectionSeeds.Count == 0
            && !await _context.Set<SqlOSScimConnection>()
                .AnyAsync(connection => connection.Source == SqlOSScimSources.Seeded, cancellationToken))
        {
            return;
        }

        await RunScimAdminAtomicAsync(async () =>
        {
            await UpsertSeededScimConnectionsCoreAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task UpsertSeededScimConnectionsCoreAsync(CancellationToken cancellationToken)
    {
        var resolvedSeeds = new List<(SqlOSScimConnectionSeedOptions Seed, SqlOSOrganization Organization, string SeedKey)>();
        var configuredConnections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in _options.ScimConnectionSeeds)
        {
            var organization = await ResolveScimSeedOrganizationAsync(seed, cancellationToken);
            var seedKey = RequireTrimmed(seed.Key, "SCIM seed key is required.");
            var configuredKey = BuildConfiguredScimConnectionKey(organization.Id, seedKey);
            if (!configuredConnections.Add(configuredKey))
            {
                throw new InvalidOperationException($"SCIM seed '{seedKey}' is configured more than once for organization '{organization.Id}'.");
            }
            resolvedSeeds.Add((seed, organization, seedKey));
        }

        var persistedSeeds = await LockSeededScimConnectionsForReconciliationAsync(cancellationToken);
        foreach (var orphaned in persistedSeeds.Where(connection =>
            string.IsNullOrWhiteSpace(connection.SeedKey)
                || !configuredConnections.Contains(BuildConfiguredScimConnectionKey(connection.OrganizationId, connection.SeedKey))))
        {
            orphaned.ConfigurationOrphanedAt ??= DateTime.UtcNow;
        }
        await _context.SaveChangesAsync(cancellationToken);

        foreach (var (seed, organization, seedKey) in resolvedSeeds)
        {
            var displayName = string.IsNullOrWhiteSpace(seed.DisplayName)
                ? $"{organization.Name} SCIM"
                : seed.DisplayName.Trim();

            if (!string.IsNullOrWhiteSpace(seed.Token) && !string.IsNullOrWhiteSpace(seed.TokenSecretName))
            {
                throw new InvalidOperationException($"Seeded SCIM connection '{seedKey}' must configure either Token or TokenSecretName, not both.");
            }
            var rawToken = ResolveSeedToken(seed);
            if (!string.IsNullOrWhiteSpace(rawToken))
            {
                rawToken = NormalizeScimToken(rawToken);
            }

            foreach (var mappingSeed in seed.GroupMappings)
            {
                ValidateScimMappingRequest(ToScimMappingRequest(mappingSeed));
            }

            var existing = persistedSeeds.FirstOrDefault(x =>
                string.Equals(x.OrganizationId, organization.Id, StringComparison.Ordinal)
                && string.Equals(x.SeedKey, seedKey, StringComparison.OrdinalIgnoreCase));

            if (seed.Enabled && rawToken == null)
            {
                var tokenSource = string.IsNullOrWhiteSpace(seed.TokenSecretName)
                    ? "Token or TokenSecretName"
                    : $"environment variable '{seed.TokenSecretName.Trim()}'";
                throw new InvalidOperationException($"Seeded SCIM connection '{seedKey}' is enabled but {tokenSource} did not provide a token.");
            }

            if (seed.Enabled)
            {
                await EnsureCanEnableScimConnectionAsync(organization.Id, existing?.Id, cancellationToken);
            }

            var now = DateTime.UtcNow;
            if (existing == null)
            {
                existing = new SqlOSScimConnection
                {
                    Id = _cryptoService.GenerateId("scim"),
                    OrganizationId = organization.Id,
                    SeedKey = seedKey,
                    DisplayName = displayName,
                    IsEnabled = seed.Enabled,
                    Source = SqlOSScimSources.Seeded,
                    ConfigurationOwner = SqlOSConfigurationOwners.Code,
                    ConfigurationSourceKey = seedKey,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _context.Set<SqlOSScimConnection>().Add(existing);
            }
            else
            {
                if (existing.ConfigurationSourceKey == null && existing.Source == SqlOSScimSources.Seeded)
                {
                    existing.ConfigurationOwner = SqlOSConfigurationOwners.Code;
                    existing.ConfigurationSourceKey = seedKey;
                }
                SqlOSConfigurationOwnershipPolicy.EnsureCodeOwnership(existing.ConfigurationOwner, existing.ConfigurationSourceKey, seedKey, $"SCIM connection '{displayName}'");
                if (existing.IsEnabled && !seed.Enabled)
                {
                    await RevokeManagedGrantsAsync(existing.Id, mappingId: null, cancellationToken: cancellationToken);
                }
                existing.DisplayName = displayName;
                // Preserve an operator emergency disable. Code may disable an existing
                // connection, but does not silently re-enable one at startup.
                if (!seed.Enabled)
                {
                    existing.IsEnabled = false;
                }
                existing.Source = SqlOSScimSources.Seeded;
                existing.UpdatedAt = now;
            }

            existing.ConfigurationFingerprint = SqlOSConfigurationOwnershipPolicy.Fingerprint(new { OrganizationId = organization.Id, SourceKey = seedKey, DisplayName = displayName, seed.Enabled, Mappings = seed.GroupMappings.Select(mapping => new { mapping.SourceKey, mapping.MatchType, mapping.GroupDisplayName, mapping.GroupExternalId, mapping.GroupPattern, mapping.RoleKey, mapping.ResourceId, mapping.ResourceIdTemplate, mapping.Description, mapping.Enabled }).ToArray() });
            existing.LastReconciledAt = now;
            existing.ConfigurationOrphanedAt = null;

            if (rawToken != null)
            {
                ApplyScimToken(existing, rawToken, now);
            }

            var configuredMappingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mappingSeed in seed.GroupMappings)
            {
                var sourceKey = string.IsNullOrWhiteSpace(mappingSeed.SourceKey)
                    ? BuildScimMappingSourceKey(mappingSeed.MatchType, mappingSeed.GroupDisplayName, mappingSeed.GroupExternalId, mappingSeed.GroupPattern)
                    : mappingSeed.SourceKey.Trim();
                configuredMappingKeys.Add(sourceKey);

                var mapping = await _context.Set<SqlOSScimGroupMapping>()
                    .FirstOrDefaultAsync(x => x.ConnectionId == existing.Id && x.SourceKey == sourceKey, cancellationToken);

                if (mapping == null)
                {
                    mapping = new SqlOSScimGroupMapping
                    {
                        Id = _cryptoService.GenerateId("scmap"),
                        ConnectionId = existing.Id,
                        SourceKey = sourceKey,
                        Source = SqlOSScimSources.Seeded,
                        CreatedAt = now
                    };
                    _context.Set<SqlOSScimGroupMapping>().Add(mapping);
                }
                else if (mapping.IsEnabled && MappingSeedChangesAuthorization(mapping, mappingSeed))
                {
                    await RevokeManagedGrantsAsync(existing.Id, mapping.Id, cancellationToken);
                }

                ApplyScimMapping(mapping, ToScimMappingRequest(mappingSeed), SqlOSScimSources.Seeded, now);
            }

            if (existing.Id != null)
            {
                var removedMappings = await _context.Set<SqlOSScimGroupMapping>()
                    .Where(mapping => mapping.ConnectionId == existing.Id
                        && mapping.Source == SqlOSScimSources.Seeded
                        && mapping.IsEnabled)
                    .ToListAsync(cancellationToken);
                foreach (var mapping in removedMappings.Where(mapping =>
                    string.IsNullOrWhiteSpace(mapping.SourceKey)
                    || !configuredMappingKeys.Contains(mapping.SourceKey)))
                {
                    await RevokeManagedGrantsAsync(existing.Id, mapping.Id, cancellationToken);
                    mapping.IsEnabled = false;
                    mapping.UpdatedAt = now;
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        foreach (var (seed, organization, seedKey) in resolvedSeeds)
        {
            var reconciled = await _context.Set<SqlOSScimConnection>()
                .AsNoTracking()
                .SingleAsync(x => x.OrganizationId == organization.Id && x.ConfigurationSourceKey == seedKey, cancellationToken);
            await RecordAuditAsync(
                "configuration.reconciled",
                "system",
                "startup",
                organizationId: organization.Id,
                data: new
                {
                    resourceType = "scim_connection",
                    resourceId = reconciled.Id,
                    owner = SqlOSConfigurationOwners.Code,
                    sourceKey = seedKey,
                    outcome = "reconciled",
                    fingerprint = reconciled.ConfigurationFingerprint
                },
                cancellationToken: cancellationToken);
        }
    }

    private async Task<List<SqlOSScimConnection>> LockSeededScimConnectionsForReconciliationAsync(
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational()
            || !string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            return await _context.Set<SqlOSScimConnection>()
                .Where(connection => connection.Source == SqlOSScimSources.Seeded)
                .OrderBy(connection => connection.Id)
                .ToListAsync(cancellationToken);
        }

        var schema = string.IsNullOrWhiteSpace(_options.Schema) ? "dbo" : _options.Schema.Trim();
        var quotedSchema = schema.Replace("]", "]]", StringComparison.Ordinal);
#pragma warning disable EF1002 // The schema is an escaped identifier; the source remains a SQL parameter.
        return await _context.Set<SqlOSScimConnection>()
            .FromSqlRaw(
                $"SELECT * FROM [{quotedSchema}].[SqlOSScimConnections] WITH (UPDLOCK, HOLDLOCK) WHERE [Source] = @source ORDER BY [Id] OPTION (MAXDOP 1)",
                new SqlParameter("@source", SqlOSScimSources.Seeded))
            .ToListAsync(cancellationToken);
#pragma warning restore EF1002
    }

    private static string BuildConfiguredScimConnectionKey(string organizationId, string seedKey)
        => $"{organizationId}\u001F{seedKey}";

    private static bool MappingSeedChangesAuthorization(
        SqlOSScimGroupMapping mapping,
        SqlOSScimGroupMappingSeedOptions seed)
    {
        var request = ToScimMappingRequest(seed);
        return !string.Equals(mapping.MatchType, NormalizeScimMatchType(request.MatchType), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mapping.GroupDisplayName, NormalizeOptional(request.GroupDisplayName), StringComparison.Ordinal)
            || !string.Equals(mapping.GroupExternalId, NormalizeOptional(request.GroupExternalId), StringComparison.Ordinal)
            || !string.Equals(mapping.GroupPattern, NormalizeOptional(request.GroupPattern), StringComparison.Ordinal)
            || !string.Equals(mapping.RoleKey, NormalizeOptional(request.RoleKey), StringComparison.Ordinal)
            || !string.Equals(mapping.ResourceId, NormalizeOptional(request.ResourceId), StringComparison.Ordinal)
            || !string.Equals(mapping.ResourceIdTemplate, NormalizeOptional(request.ResourceIdTemplate), StringComparison.Ordinal)
            || mapping.IsEnabled != request.Enabled;
    }

    public async Task<object> ListOrganizationScimConnectionsAsync(
        string organizationId,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        SqlOSCursorPagination.RejectLegacyOffset(page);
        var size = SqlOSCursorPagination.NormalizePageSize(pageSize, 10);
        var pageResult = await SqlOSCursorPagination.ToPageAsync(
            _context.Set<SqlOSScimConnection>().AsNoTracking().Where(x => x.OrganizationId == organizationId),
            SqlOSKeyset<SqlOSScimConnection>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id),
            "auth.scim-connections",
            SqlOSCursorCodec.Fingerprint(organizationId),
            cursor,
            size,
            cancellationToken);
        var ids = pageResult.Data.Select(x => x.Id).ToList();
        var mappingCounts = ids.Count == 0
            ? []
            : await _context.Set<SqlOSScimGroupMapping>()
                .AsNoTracking()
                .Where(x => ids.Contains(x.ConnectionId))
                .GroupBy(x => x.ConnectionId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var syncEventCounts = ids.Count == 0
            ? []
            : await _context.Set<SqlOSScimSyncEvent>()
                .AsNoTracking()
                .Where(x => ids.Contains(x.ConnectionId))
                .GroupBy(x => x.ConnectionId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return pageResult.ToResponse(x => new
        {
            x.Id,
            x.OrganizationId,
            x.DisplayName,
            x.IsEnabled,
            x.Source,
            x.SeedKey,
            Ownership = SqlOSConfigurationOwnershipPolicy.ToDto(
                x.ConfigurationOwner,
                x.ConfigurationSourceKey,
                x.LastReconciledAt,
                x.ConfigurationFingerprint,
                x.ConfigurationOrphanedAt,
                true),
            x.TokenPrefix,
            x.TokenRotatedAt,
            x.TokenLastUsedAt,
            x.LastSyncAt,
            x.CreatedAt,
            x.UpdatedAt,
            MappingCount = mappingCounts.GetValueOrDefault(x.Id),
            SyncEventCount = syncEventCounts.GetValueOrDefault(x.Id)
        });
    }

    public async Task<object> GetScimConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await GetRequiredScimConnectionAsync(connectionId, cancellationToken);
        var baseUrl = BuildScimBaseUrl();
        return new
        {
            connection.Id,
            connection.OrganizationId,
            connection.DisplayName,
            connection.IsEnabled,
            connection.Source,
            connection.SeedKey,
            Ownership = SqlOSConfigurationOwnershipPolicy.ToDto(
                connection.ConfigurationOwner,
                connection.ConfigurationSourceKey,
                connection.LastReconciledAt,
                connection.ConfigurationFingerprint,
                connection.ConfigurationOrphanedAt),
            connection.TokenPrefix,
            connection.TokenRotatedAt,
            connection.TokenLastUsedAt,
            connection.LastSyncAt,
            connection.CreatedAt,
            connection.UpdatedAt,
            BaseUrl = baseUrl,
            UsersUrl = $"{baseUrl}/Users",
            GroupsUrl = $"{baseUrl}/Groups"
        };
    }

    public async Task<SqlOSScimConnection> CreateScimConnectionDraftAsync(
        SqlOSCreateScimConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Enabled)
        {
            throw new InvalidOperationException(
                "Enabled SCIM connections must be created with CreateScimConnectionAsync so they are never usable without a bearer token.");
        }

        return await RunScimAdminAtomicAsync(
            () => CreateScimConnectionCoreAsync(request, cancellationToken),
            cancellationToken);
    }

    private async Task<SqlOSScimConnection> CreateScimConnectionCoreAsync(
        SqlOSCreateScimConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var organization = await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(x => x.Id == request.OrganizationId, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");

        if (request.Enabled)
        {
            await EnsureCanEnableScimConnectionAsync(organization.Id, null, cancellationToken);
        }

        var now = DateTime.UtcNow;
        var connection = new SqlOSScimConnection
        {
            Id = _cryptoService.GenerateId("scim"),
            OrganizationId = organization.Id,
            DisplayName = RequireTrimmed(request.DisplayName, "SCIM display name is required."),
            IsEnabled = request.Enabled,
            Source = SqlOSScimSources.Dashboard,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.Set<SqlOSScimConnection>().Add(connection);
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync("scim.connection.created", "scim_connection", connection.Id, organizationId: organization.Id, data: new { connection.DisplayName }, cancellationToken: cancellationToken);
        return connection;
    }

    public async Task<SqlOSCreateScimConnectionResult> CreateScimConnectionAsync(
        SqlOSCreateScimConnectionRequest request,
        CancellationToken cancellationToken = default)
        => await RunScimAdminAtomicAsync(async () =>
        {
            var connection = await CreateScimConnectionCoreAsync(request, cancellationToken);
            var rotation = await RotateScimTokenAsync(connection.Id, cancellationToken);
            var baseUrl = BuildScimBaseUrl();
            return new SqlOSCreateScimConnectionResult(
                connection.Id,
                connection.OrganizationId,
                connection.DisplayName,
                connection.IsEnabled,
                rotation.Token,
                rotation.TokenPrefix,
                rotation.TokenRotatedAt,
                baseUrl,
                $"{baseUrl}/Users",
                $"{baseUrl}/Groups");
        }, cancellationToken);

    public async Task<SqlOSScimConnection> UpdateScimConnectionAsync(string connectionId, SqlOSUpdateScimConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var displayName = RequireTrimmed(request.DisplayName, "SCIM display name is required.");
        return await RunScimAdminAtomicAsync(async () =>
        {
            var connection = await GetRequiredScimConnectionForUpdateAsync(connectionId, cancellationToken);
            EnsureScimConnectionIsDashboardManaged(connection);
            if (request.Enabled)
            {
                EnsureScimConnectionHasToken(connection);
                await EnsureCanEnableScimConnectionAsync(connection.OrganizationId, connection.Id, cancellationToken);
            }
            else if (connection.IsEnabled)
            {
                await RevokeManagedGrantsAsync(connection.Id, mappingId: null, cancellationToken: cancellationToken);
            }
            connection.DisplayName = displayName;
            connection.IsEnabled = request.Enabled;
            connection.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            await RecordAuditAsync(request.Enabled ? "scim.connection.enabled" : "scim.connection.disabled", "scim_connection", connection.Id, organizationId: connection.OrganizationId, cancellationToken: cancellationToken);
            return connection;
        }, cancellationToken);
    }

    public async Task<SqlOSScimConnection> SetScimConnectionEnabledAsync(string connectionId, bool enabled, CancellationToken cancellationToken = default)
        => await RunScimAdminAtomicAsync(async () =>
        {
            var connection = await GetRequiredScimConnectionForUpdateAsync(connectionId, cancellationToken);
            // Enable/disable is the explicit emergency control available for every owner.
            if (enabled)
            {
                EnsureScimConnectionHasToken(connection);
                await EnsureCanEnableScimConnectionAsync(connection.OrganizationId, connection.Id, cancellationToken);
            }
            else if (connection.IsEnabled)
            {
                await RevokeManagedGrantsAsync(connection.Id, mappingId: null, cancellationToken: cancellationToken);
            }
            connection.IsEnabled = enabled;
            connection.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            await RecordAuditAsync(enabled ? "scim.connection.enabled" : "scim.connection.disabled", "scim_connection", connection.Id, organizationId: connection.OrganizationId, cancellationToken: cancellationToken);
            return connection;
        }, cancellationToken);

    public async Task<SqlOSRotateScimTokenResult> RotateScimTokenAsync(string connectionId, CancellationToken cancellationToken = default)
        => await RunScimAdminAtomicAsync(async () =>
        {
            var connection = await GetRequiredScimConnectionForUpdateAsync(connectionId, cancellationToken);
            EnsureScimConnectionIsDashboardManaged(connection);
            var token = $"scim_{_cryptoService.GenerateOpaqueToken(32)}";
            var now = DateTime.UtcNow;
            ApplyScimToken(connection, token, now);
            connection.UpdatedAt = now;
            await _context.SaveChangesAsync(cancellationToken);
            await RecordAuditAsync("scim.token.rotated", "scim_connection", connection.Id, organizationId: connection.OrganizationId, data: new { connection.TokenPrefix }, cancellationToken: cancellationToken);
            return new SqlOSRotateScimTokenResult(connection.Id, token, connection.TokenPrefix!, now);
        }, cancellationToken);

    public async Task<object> ListScimGroupMappingsAsync(
        string connectionId,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        SqlOSCursorPagination.RejectLegacyOffset(page);
        var size = SqlOSCursorPagination.NormalizePageSize(pageSize, 10);
        var pageResult = await SqlOSCursorPagination.ToPageAsync(
            _context.Set<SqlOSScimGroupMapping>().AsNoTracking().Where(x => x.ConnectionId == connectionId),
            SqlOSKeyset<SqlOSScimGroupMapping>.Create().Descending(x => x.CreatedAt).ThenDescending(x => x.Id),
            "auth.scim-group-mappings",
            SqlOSCursorCodec.Fingerprint(connectionId),
            cursor,
            size,
            cancellationToken);
        var ids = pageResult.Data.Select(x => x.Id).ToList();
        var grantCounts = ids.Count == 0
            ? []
            : await _context.Set<SqlOSScimManagedGrant>()
                .AsNoTracking()
                .Where(x => ids.Contains(x.MappingId) && x.RevokedAt == null)
                .GroupBy(x => x.MappingId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return pageResult.ToResponse(x => new
        {
            x.Id,
            x.ConnectionId,
            x.Source,
            x.SourceKey,
            x.MatchType,
            x.GroupDisplayName,
            x.GroupExternalId,
            x.GroupPattern,
            x.RoleKey,
            x.ResourceId,
            x.ResourceIdTemplate,
            x.Description,
            x.IsEnabled,
            x.CreatedAt,
            x.UpdatedAt,
            ActiveGrantCount = grantCounts.GetValueOrDefault(x.Id)
        });
    }

    public async Task<SqlOSScimGroupMapping> CreateScimGroupMappingAsync(string connectionId, SqlOSCreateScimGroupMappingRequest request, CancellationToken cancellationToken = default)
    {
        var updateRequest = new SqlOSUpdateScimGroupMappingRequest(
            request.MatchType,
            request.GroupDisplayName,
            request.GroupExternalId,
            request.GroupPattern,
            request.RoleKey,
            request.ResourceId,
            request.ResourceIdTemplate,
            request.Description,
            request.Enabled);
        ValidateScimMappingRequest(updateRequest);
        return await RunScimAdminAtomicAsync(async () =>
        {
            var connection = await GetRequiredScimConnectionForUpdateAsync(connectionId, cancellationToken);
            var now = DateTime.UtcNow;
            var mapping = new SqlOSScimGroupMapping
            {
                Id = _cryptoService.GenerateId("scmap"),
                ConnectionId = connection.Id,
                Source = SqlOSScimSources.Dashboard,
                CreatedAt = now
            };
            ApplyScimMapping(mapping, updateRequest, SqlOSScimSources.Dashboard, now);
            _context.Set<SqlOSScimGroupMapping>().Add(mapping);
            await _context.SaveChangesAsync(cancellationToken);
            await RecordAuditAsync("scim.mapping.created", "scim_group_mapping", mapping.Id, organizationId: connection.OrganizationId, data: new { mapping.MatchType, mapping.RoleKey, mapping.ResourceId, mapping.ResourceIdTemplate }, cancellationToken: cancellationToken);
            return mapping;
        }, cancellationToken);
    }

    public async Task<SqlOSScimGroupMapping> UpdateScimGroupMappingAsync(string mappingId, SqlOSUpdateScimGroupMappingRequest request, CancellationToken cancellationToken = default)
    {
        ValidateScimMappingRequest(request);
        return await RunScimAdminAtomicAsync(async () =>
        {
            var mapping = await _context.Set<SqlOSScimGroupMapping>()
                .Include(x => x.Connection)
                .FirstOrDefaultAsync(x => x.Id == mappingId, cancellationToken)
                ?? throw new InvalidOperationException("SCIM mapping not found.");
            EnsureScimMappingIsDashboardManaged(mapping);

            await GetRequiredScimConnectionForUpdateAsync(mapping.ConnectionId, cancellationToken);
            await RevokeManagedGrantsAsync(mapping.ConnectionId, mapping.Id, cancellationToken);
            ApplyScimMapping(mapping, request, mapping.Source, DateTime.UtcNow);
            await _context.SaveChangesAsync(cancellationToken);
            await RecordAuditAsync("scim.mapping.updated", "scim_group_mapping", mapping.Id, organizationId: mapping.Connection!.OrganizationId, data: new { mapping.MatchType, mapping.RoleKey, mapping.ResourceId, mapping.ResourceIdTemplate, mapping.IsEnabled }, cancellationToken: cancellationToken);
            return mapping;
        }, cancellationToken);
    }

    public async Task<SqlOSScimGroupMapping> SetScimGroupMappingEnabledAsync(string mappingId, bool enabled, CancellationToken cancellationToken = default)
        => await RunScimAdminAtomicAsync(async () =>
        {
            var mapping = await _context.Set<SqlOSScimGroupMapping>()
                .Include(x => x.Connection)
                .FirstOrDefaultAsync(x => x.Id == mappingId, cancellationToken)
                ?? throw new InvalidOperationException("SCIM mapping not found.");
            EnsureScimMappingIsDashboardManaged(mapping);
            await GetRequiredScimConnectionForUpdateAsync(mapping.ConnectionId, cancellationToken);
            if (!enabled && mapping.IsEnabled)
            {
                await RevokeManagedGrantsAsync(mapping.ConnectionId, mapping.Id, cancellationToken);
            }
            mapping.IsEnabled = enabled;
            mapping.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            await RecordAuditAsync(enabled ? "scim.mapping.enabled" : "scim.mapping.disabled", "scim_group_mapping", mapping.Id, organizationId: mapping.Connection!.OrganizationId, cancellationToken: cancellationToken);
            return mapping;
        }, cancellationToken);

    public async Task<object> ListScimSyncEventsAsync(
        string connectionId,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
        => await PaginateByCursorAsync(
            _context.Set<SqlOSScimSyncEvent>().AsNoTracking().Where(x => x.ConnectionId == connectionId),
            SqlOSKeyset<SqlOSScimSyncEvent>.Create().Descending(x => x.OccurredAt).ThenDescending(x => x.Id),
            "auth.scim-sync-events",
            SqlOSCursorCodec.Fingerprint(connectionId),
            cursor,
            pageSize,
            page,
            x => new
            {
                x.Id,
                x.ConnectionId,
                x.OrganizationId,
                x.ResourceType,
                x.ResourceId,
                x.ExternalId,
                x.Action,
                x.Result,
                x.Error,
                x.DataJson,
                x.RequestId,
                x.OccurredAt
            },
            cancellationToken: cancellationToken);

    private async Task<SqlOSScimConnection> GetRequiredScimConnectionAsync(string connectionId, CancellationToken cancellationToken)
        => await _context.Set<SqlOSScimConnection>()
            .FirstOrDefaultAsync(x => x.Id == connectionId, cancellationToken)
        ?? throw new InvalidOperationException("SCIM connection not found.");

    private async Task<SqlOSScimConnection> GetRequiredScimConnectionForUpdateAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational()
            || !string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            return await GetRequiredScimConnectionAsync(connectionId, cancellationToken);
        }

        var schema = string.IsNullOrWhiteSpace(_options.Schema) ? "dbo" : _options.Schema.Trim();
        var quotedSchema = schema.Replace("]", "]]", StringComparison.Ordinal);
#pragma warning disable EF1002 // The schema is an escaped identifier; the connection id remains a SQL parameter.
        var connection = await _context.Set<SqlOSScimConnection>()
            .FromSqlRaw(
                $"SELECT * FROM [{quotedSchema}].[SqlOSScimConnections] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @connectionId",
                new SqlParameter("@connectionId", connectionId))
            .SingleOrDefaultAsync(cancellationToken);
#pragma warning restore EF1002
        return connection ?? throw new InvalidOperationException("SCIM connection not found.");
    }

    private async Task EnsureCanEnableScimConnectionAsync(string organizationId, string? exceptConnectionId, CancellationToken cancellationToken)
    {
        if (await _context.Set<SqlOSScimConnection>().AnyAsync(x => x.OrganizationId == organizationId
            && x.IsEnabled
            && x.Id != exceptConnectionId, cancellationToken))
        {
            throw new InvalidOperationException("Only one SCIM directory connection can be enabled for an organization at a time.");
        }
    }

    private static void EnsureScimConnectionHasToken(SqlOSScimConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.TokenHash))
        {
            throw new InvalidOperationException("Rotate a bearer token before enabling the SCIM connection.");
        }
    }

    private static void EnsureScimConnectionIsDashboardManaged(SqlOSScimConnection connection)
    {
        if (string.Equals(connection.Source, SqlOSScimSources.Seeded, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This SCIM connection is managed by startup configuration. Update its seed Enabled value or token secret and restart SqlOS.");
        }
    }

    private static void EnsureScimMappingIsDashboardManaged(SqlOSScimGroupMapping mapping)
    {
        if (string.Equals(mapping.Source, SqlOSScimSources.Seeded, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This SCIM mapping is managed by startup configuration. Update its seed definition and restart SqlOS.");
        }
    }

    private async Task RevokeManagedGrantsAsync(string connectionId, string? mappingId, CancellationToken cancellationToken)
    {
        var connection = await GetRequiredScimConnectionAsync(connectionId, cancellationToken);
        var managedGrants = await _context.Set<SqlOSScimManagedGrant>()
            .Where(x => x.ConnectionId == connectionId
                && (mappingId == null || x.MappingId == mappingId)
                && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        if (managedGrants.Count == 0)
        {
            return;
        }

        var grantIds = managedGrants.Select(x => x.GrantId).ToList();
        var grants = await _context.Set<SqlOS.Fga.Models.SqlOSFgaGrant>()
            .Where(x => grantIds.Contains(x.Id))
            .ToListAsync(cancellationToken);
        _context.Set<SqlOS.Fga.Models.SqlOSFgaGrant>().RemoveRange(grants);
        var now = DateTime.UtcNow;
        var orderedManagedGrants = managedGrants
            .OrderBy(managed => managed.Id, StringComparer.Ordinal)
            .ToList();
        foreach (var managed in orderedManagedGrants)
        {
            managed.RevokedAt = now;
        }

        var evidence = orderedManagedGrants
            .Take(ScimRevocationEvidenceLimit)
            .Select(managed => new
            {
                managedGrantId = managed.Id,
                mappingId = managed.MappingId,
                grantId = managed.GrantId,
                groupId = managed.FgaGroupId,
                groupExternalId = managed.GroupExternalId,
                roleId = managed.RoleId,
                resourceId = managed.ResourceId
            })
            .ToList();
        var data = new
        {
            connectionId = connection.Id,
            requestedMappingId = mappingId,
            revokedManagedGrantCount = orderedManagedGrants.Count,
            deletedGrantCount = grants.Count,
            evidenceTruncated = orderedManagedGrants.Count > evidence.Count,
            evidence
        };
        var dataJson = JsonSerializer.Serialize(data);
        var groupIds = orderedManagedGrants
            .Select(managed => managed.FgaGroupId)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();
        var groupExternalIds = orderedManagedGrants
            .Select(managed => managed.GroupExternalId)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();
        _context.Set<SqlOSScimSyncEvent>().Add(new SqlOSScimSyncEvent
        {
            Id = _cryptoService.GenerateId("scevt"),
            ConnectionId = connection.Id,
            OrganizationId = connection.OrganizationId,
            ResourceType = "Group",
            ResourceId = groupIds.Count == 1 ? groupIds[0] : null,
            ExternalId = groupExternalIds.Count == 1 ? groupExternalIds[0] : null,
            Action = "scim.grant.revoked",
            Result = "success",
            DataJson = dataJson,
            OccurredAt = now
        });

        var targets = new List<SqlOSAuditTarget>
        {
            new("scim_connection", connection.Id)
        };
        targets.AddRange(orderedManagedGrants
            .Select(managed => managed.MappingId)
            .Distinct(StringComparer.Ordinal)
            .Take(ScimRevocationEvidenceLimit)
            .Select(id => new SqlOSAuditTarget("scim_group_mapping", id)));
        var audit = new SqlOSAuditLogService(_context, _cryptoService);
        await audit.RecordAsync(new SqlOSAuditLogRecordRequest(
            Action: "scim.grant.revoked",
            OrganizationId: connection.OrganizationId,
            Source: "scim",
            Actor: new SqlOSAuditActor("admin", null),
            Targets: targets,
            Metadata: JsonSerializer.Deserialize<Dictionary<string, object?>>(
                dataJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            OccurredAt: now),
            cancellationToken);
    }

    private async Task<T> RunScimAdminAtomicAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        if (_context.Database.CurrentTransaction != null)
        {
            return await action();
        }

        if (!_context.Database.IsRelational())
        {
            var result = await action();
            if (await StageExpiredScimOperationCommitsAsync(
                DateTime.UtcNow - ScimOperationCommitRetention,
                ScimOperationCommitCleanupBatchSize,
                cancellationToken) > 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            return result;
        }

        var executionStrategy = _context.Database.CreateExecutionStrategy();
        var commitMarkerId = _cryptoService.GenerateId("evt");
        var attempt = 0;
        return await executionStrategy.ExecuteInTransactionAsync(
            async _ =>
            {
                if (attempt++ > 0 && _context is DbContext retryContext)
                {
                    retryContext.ChangeTracker.Clear();
                }
                var result = await action();
                await StageExpiredScimOperationCommitsAsync(
                    DateTime.UtcNow - ScimOperationCommitRetention,
                    ScimOperationCommitCleanupBatchSize,
                    cancellationToken);
                _context.Set<SqlOSScimOperationCommit>().Add(CreateScimAdminCommitMarker(commitMarkerId));
                await _context.SaveChangesAsync(cancellationToken);
                return result;
            },
            async _ =>
            {
                if (_context is DbContext verificationContext)
                {
                    verificationContext.ChangeTracker.Clear();
                }
                return await _context.Set<SqlOSScimOperationCommit>()
                    .AsNoTracking()
                    .AnyAsync(item => item.Id == commitMarkerId, cancellationToken);
            },
            IsolationLevel.Serializable,
            cancellationToken);
    }

    private static SqlOSScimOperationCommit CreateScimAdminCommitMarker(string id)
        => new()
        {
            Id = id,
            OccurredAt = DateTime.UtcNow
        };

    private async Task<SqlOSOrganization> ResolveScimSeedOrganizationAsync(SqlOSScimConnectionSeedOptions seed, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(seed.OrganizationId))
        {
            return await _context.Set<SqlOSOrganization>()
                .FirstOrDefaultAsync(x => x.Id == seed.OrganizationId.Trim(), cancellationToken)
                ?? throw new InvalidOperationException($"Seeded SCIM organization '{seed.OrganizationId}' was not found.");
        }

        var slug = string.IsNullOrWhiteSpace(seed.OrganizationSlug) ? seed.Key : seed.OrganizationSlug;
        return await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(x => x.Slug == slug.Trim(), cancellationToken)
            ?? throw new InvalidOperationException($"Seeded SCIM organization slug '{slug}' was not found.");
    }

    private static string? ResolveSeedToken(SqlOSScimConnectionSeedOptions seed)
    {
        if (!string.IsNullOrWhiteSpace(seed.Token))
        {
            return seed.Token;
        }

        return string.IsNullOrWhiteSpace(seed.TokenSecretName)
            ? null
            : Environment.GetEnvironmentVariable(seed.TokenSecretName.Trim());
    }

    private static string NormalizeScimToken(string rawToken)
    {
        var normalized = RequireTrimmed(rawToken, "SCIM bearer token is required.");
        if (normalized.Length < MinimumScimBearerTokenLength)
        {
            throw new InvalidOperationException($"SCIM bearer tokens must contain at least {MinimumScimBearerTokenLength} characters.");
        }
        if (normalized.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("SCIM bearer tokens cannot contain whitespace.");
        }
        return normalized;
    }

    private void ApplyScimToken(SqlOSScimConnection connection, string rawToken, DateTime now)
    {
        var normalized = NormalizeScimToken(rawToken);
        connection.TokenHash = _cryptoService.HashToken(normalized);
        connection.TokenPrefix = normalized[..ScimTokenPrefixLength];
        connection.TokenRotatedAt = now;
    }

    private static SqlOSUpdateScimGroupMappingRequest ToScimMappingRequest(SqlOSScimGroupMappingSeedOptions seed)
        => new(
            seed.MatchType,
            seed.GroupDisplayName,
            seed.GroupExternalId,
            seed.GroupPattern,
            seed.RoleKey,
            seed.ResourceId,
            seed.ResourceIdTemplate,
            seed.Description,
            seed.Enabled);

    private static void ValidateScimMappingRequest(SqlOSUpdateScimGroupMappingRequest request)
    {
        var matchType = NormalizeScimMatchType(request.MatchType);
        _ = RequireTrimmed(request.RoleKey, "SCIM mapping role key is required.");
        if (NormalizeOptional(request.ResourceId) == null && NormalizeOptional(request.ResourceIdTemplate) == null)
        {
            throw new InvalidOperationException("SCIM mapping requires a resource ID or resource ID template.");
        }

        _ = BuildScimMappingSourceKey(matchType, request.GroupDisplayName, request.GroupExternalId, request.GroupPattern);
        if (matchType != SqlOSScimGroupMappingMatchTypes.Pattern)
        {
            return;
        }

        var pattern = RequireTrimmed(request.GroupPattern, "Pattern SCIM mappings require a group pattern.");
        try
        {
            _ = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("SCIM group mapping pattern is not a valid regular expression.", ex);
        }
    }

    private static void ApplyScimMapping(SqlOSScimGroupMapping mapping, SqlOSUpdateScimGroupMappingRequest request, string source, DateTime now)
    {
        ValidateScimMappingRequest(request);
        var matchType = NormalizeScimMatchType(request.MatchType);
        var roleKey = RequireTrimmed(request.RoleKey, "SCIM mapping role key is required.");
        var resourceId = NormalizeOptional(request.ResourceId);
        var resourceIdTemplate = NormalizeOptional(request.ResourceIdTemplate);
        if (resourceId == null && resourceIdTemplate == null)
        {
            throw new InvalidOperationException("SCIM mapping requires a resource ID or resource ID template.");
        }

        mapping.MatchType = matchType;
        mapping.GroupDisplayName = NormalizeOptional(request.GroupDisplayName);
        mapping.GroupExternalId = NormalizeOptional(request.GroupExternalId);
        mapping.GroupPattern = NormalizeOptional(request.GroupPattern);
        mapping.RoleKey = roleKey;
        mapping.ResourceId = resourceId;
        mapping.ResourceIdTemplate = resourceIdTemplate;
        mapping.Description = NormalizeOptional(request.Description);
        mapping.IsEnabled = request.Enabled;
        mapping.Source = source;
        mapping.UpdatedAt = now;
    }

    private static string BuildScimMappingSourceKey(string matchType, string? displayName, string? externalId, string? pattern)
        => NormalizeScimMatchType(matchType) switch
        {
            SqlOSScimGroupMappingMatchTypes.DisplayName => $"name:{RequireTrimmed(displayName, "Display-name SCIM mappings require a group display name.")}",
            SqlOSScimGroupMappingMatchTypes.ExternalId => $"external:{RequireTrimmed(externalId, "External-id SCIM mappings require a group external ID.")}",
            SqlOSScimGroupMappingMatchTypes.Pattern => $"pattern:{RequireTrimmed(pattern, "Pattern SCIM mappings require a group pattern.")}",
            _ => throw new InvalidOperationException("Unsupported SCIM mapping match type.")
        };

    private static string NormalizeScimMatchType(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" => SqlOSScimGroupMappingMatchTypes.DisplayName,
            SqlOSScimGroupMappingMatchTypes.DisplayName => SqlOSScimGroupMappingMatchTypes.DisplayName,
            "name" => SqlOSScimGroupMappingMatchTypes.DisplayName,
            SqlOSScimGroupMappingMatchTypes.ExternalId => SqlOSScimGroupMappingMatchTypes.ExternalId,
            "externalid" => SqlOSScimGroupMappingMatchTypes.ExternalId,
            SqlOSScimGroupMappingMatchTypes.Pattern => SqlOSScimGroupMappingMatchTypes.Pattern,
            "regex" => SqlOSScimGroupMappingMatchTypes.Pattern,
            _ => throw new InvalidOperationException("Unsupported SCIM mapping match type.")
        };

    private string BuildScimBaseUrl()
    {
        var path = string.IsNullOrWhiteSpace(_options.ScimBasePath) ? "/sqlos/scim/v2" : _options.ScimBasePath.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }
        path = path.TrimEnd('/');

        return string.IsNullOrWhiteSpace(_options.PublicOrigin)
            ? path
            : $"{_options.PublicOrigin.TrimEnd('/')}{path}";
    }

    private static string RequireTrimmed(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
