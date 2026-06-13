using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed partial class SqlOSAdminService
{
    public async Task UpsertSeededScimConnectionsAsync(CancellationToken cancellationToken = default)
    {
        if (_options.ScimConnectionSeeds.Count == 0)
        {
            return;
        }

        foreach (var seed in _options.ScimConnectionSeeds)
        {
            var organization = await ResolveScimSeedOrganizationAsync(seed, cancellationToken);
            var seedKey = RequireTrimmed(seed.Key, "SCIM seed key is required.");
            var displayName = string.IsNullOrWhiteSpace(seed.DisplayName)
                ? $"{organization.Name} SCIM"
                : seed.DisplayName.Trim();

            var existing = await _context.Set<SqlOSScimConnection>()
                .FirstOrDefaultAsync(x => x.OrganizationId == organization.Id && x.SeedKey == seedKey, cancellationToken);

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
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _context.Set<SqlOSScimConnection>().Add(existing);
            }
            else
            {
                existing.DisplayName = displayName;
                existing.Source = SqlOSScimSources.Seeded;
                existing.UpdatedAt = now;
            }

            var rawToken = ResolveSeedToken(seed);
            if (!string.IsNullOrWhiteSpace(rawToken))
            {
                ApplyScimToken(existing, rawToken.Trim(), now);
            }

            foreach (var mappingSeed in seed.GroupMappings)
            {
                var sourceKey = string.IsNullOrWhiteSpace(mappingSeed.SourceKey)
                    ? BuildScimMappingSourceKey(mappingSeed.MatchType, mappingSeed.GroupDisplayName, mappingSeed.GroupExternalId, mappingSeed.GroupPattern)
                    : mappingSeed.SourceKey.Trim();

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

                ApplyScimMapping(mapping, new SqlOSUpdateScimGroupMappingRequest(
                    mappingSeed.MatchType,
                    mappingSeed.GroupDisplayName,
                    mappingSeed.GroupExternalId,
                    mappingSeed.GroupPattern,
                    mappingSeed.RoleKey,
                    mappingSeed.ResourceId,
                    mappingSeed.ResourceIdTemplate,
                    mappingSeed.Description,
                    mappingSeed.Enabled), SqlOSScimSources.Seeded, now);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<object> ListOrganizationScimConnectionsAsync(string organizationId, int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSScimConnection>()
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.DisplayName)
            .Select(x => new
            {
                x.Id,
                x.OrganizationId,
                x.DisplayName,
                x.IsEnabled,
                x.Source,
                x.SeedKey,
                x.TokenPrefix,
                x.TokenRotatedAt,
                x.TokenLastUsedAt,
                x.LastSyncAt,
                x.CreatedAt,
                x.UpdatedAt,
                MappingCount = x.GroupMappings.Count,
                SyncEventCount = x.SyncEvents.Count
            });

        return await PaginateAsync(query, resolvedPage, resolvedPageSize, cancellationToken);
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

    public async Task<SqlOSScimConnection> CreateScimConnectionAsync(SqlOSCreateScimConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var organization = await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(x => x.Id == request.OrganizationId, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");

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

    public async Task<SqlOSScimConnection> UpdateScimConnectionAsync(string connectionId, SqlOSUpdateScimConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await GetRequiredScimConnectionAsync(connectionId, cancellationToken);
        connection.DisplayName = RequireTrimmed(request.DisplayName, "SCIM display name is required.");
        connection.IsEnabled = request.Enabled;
        connection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(request.Enabled ? "scim.connection.enabled" : "scim.connection.disabled", "scim_connection", connection.Id, organizationId: connection.OrganizationId, cancellationToken: cancellationToken);
        return connection;
    }

    public async Task<SqlOSScimConnection> SetScimConnectionEnabledAsync(string connectionId, bool enabled, CancellationToken cancellationToken = default)
    {
        var connection = await GetRequiredScimConnectionAsync(connectionId, cancellationToken);
        connection.IsEnabled = enabled;
        connection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(enabled ? "scim.connection.enabled" : "scim.connection.disabled", "scim_connection", connection.Id, organizationId: connection.OrganizationId, cancellationToken: cancellationToken);
        return connection;
    }

    public async Task<SqlOSRotateScimTokenResult> RotateScimTokenAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await GetRequiredScimConnectionAsync(connectionId, cancellationToken);
        var token = $"scim_{_cryptoService.GenerateOpaqueToken(32)}";
        var now = DateTime.UtcNow;
        ApplyScimToken(connection, token, now);
        connection.UpdatedAt = now;
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync("scim.token.rotated", "scim_connection", connection.Id, organizationId: connection.OrganizationId, data: new { connection.TokenPrefix }, cancellationToken: cancellationToken);
        return new SqlOSRotateScimTokenResult(connection.Id, token, connection.TokenPrefix!, now);
    }

    public async Task<object> ListScimGroupMappingsAsync(string connectionId, int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSScimGroupMapping>()
            .AsNoTracking()
            .Where(x => x.ConnectionId == connectionId)
            .OrderBy(x => x.GroupDisplayName ?? x.GroupExternalId ?? x.GroupPattern ?? x.Id)
            .Select(x => new
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
                ActiveGrantCount = x.ManagedGrants.Count(g => g.RevokedAt == null)
            });

        return await PaginateAsync(query, resolvedPage, resolvedPageSize, cancellationToken);
    }

    public async Task<SqlOSScimGroupMapping> CreateScimGroupMappingAsync(string connectionId, SqlOSCreateScimGroupMappingRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await GetRequiredScimConnectionAsync(connectionId, cancellationToken);
        var now = DateTime.UtcNow;
        var mapping = new SqlOSScimGroupMapping
        {
            Id = _cryptoService.GenerateId("scmap"),
            ConnectionId = connection.Id,
            Source = SqlOSScimSources.Dashboard,
            CreatedAt = now
        };
        ApplyScimMapping(mapping, new SqlOSUpdateScimGroupMappingRequest(
            request.MatchType,
            request.GroupDisplayName,
            request.GroupExternalId,
            request.GroupPattern,
            request.RoleKey,
            request.ResourceId,
            request.ResourceIdTemplate,
            request.Description,
            request.Enabled), SqlOSScimSources.Dashboard, now);
        _context.Set<SqlOSScimGroupMapping>().Add(mapping);
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync("scim.mapping.created", "scim_group_mapping", mapping.Id, organizationId: connection.OrganizationId, data: new { mapping.MatchType, mapping.RoleKey, mapping.ResourceId, mapping.ResourceIdTemplate }, cancellationToken: cancellationToken);
        return mapping;
    }

    public async Task<SqlOSScimGroupMapping> UpdateScimGroupMappingAsync(string mappingId, SqlOSUpdateScimGroupMappingRequest request, CancellationToken cancellationToken = default)
    {
        var mapping = await _context.Set<SqlOSScimGroupMapping>()
            .Include(x => x.Connection)
            .FirstOrDefaultAsync(x => x.Id == mappingId, cancellationToken)
            ?? throw new InvalidOperationException("SCIM mapping not found.");

        ApplyScimMapping(mapping, request, mapping.Source, DateTime.UtcNow);
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync("scim.mapping.updated", "scim_group_mapping", mapping.Id, organizationId: mapping.Connection!.OrganizationId, data: new { mapping.MatchType, mapping.RoleKey, mapping.ResourceId, mapping.ResourceIdTemplate, mapping.IsEnabled }, cancellationToken: cancellationToken);
        return mapping;
    }

    public async Task<SqlOSScimGroupMapping> SetScimGroupMappingEnabledAsync(string mappingId, bool enabled, CancellationToken cancellationToken = default)
    {
        var mapping = await _context.Set<SqlOSScimGroupMapping>()
            .Include(x => x.Connection)
            .FirstOrDefaultAsync(x => x.Id == mappingId, cancellationToken)
            ?? throw new InvalidOperationException("SCIM mapping not found.");
        mapping.IsEnabled = enabled;
        mapping.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(enabled ? "scim.mapping.enabled" : "scim.mapping.disabled", "scim_group_mapping", mapping.Id, organizationId: mapping.Connection!.OrganizationId, cancellationToken: cancellationToken);
        return mapping;
    }

    public async Task<object> ListScimSyncEventsAsync(string connectionId, int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSScimSyncEvent>()
            .AsNoTracking()
            .Where(x => x.ConnectionId == connectionId)
            .OrderByDescending(x => x.OccurredAt)
            .Select(x => new
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
            });

        return await PaginateAsync(query, resolvedPage, resolvedPageSize, cancellationToken);
    }

    private async Task<SqlOSScimConnection> GetRequiredScimConnectionAsync(string connectionId, CancellationToken cancellationToken)
        => await _context.Set<SqlOSScimConnection>()
            .FirstOrDefaultAsync(x => x.Id == connectionId, cancellationToken)
        ?? throw new InvalidOperationException("SCIM connection not found.");

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

    private void ApplyScimToken(SqlOSScimConnection connection, string rawToken, DateTime now)
    {
        connection.TokenHash = _cryptoService.HashToken(rawToken);
        connection.TokenPrefix = rawToken.Length <= 12 ? rawToken : rawToken[..12];
        connection.TokenRotatedAt = now;
    }

    private static void ApplyScimMapping(SqlOSScimGroupMapping mapping, SqlOSUpdateScimGroupMappingRequest request, string source, DateTime now)
    {
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

        _ = BuildScimMappingSourceKey(matchType, mapping.GroupDisplayName, mapping.GroupExternalId, mapping.GroupPattern);
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
