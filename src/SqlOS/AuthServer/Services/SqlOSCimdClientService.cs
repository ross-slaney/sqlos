using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSCimdClientService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SqlOSCryptoService _cryptoService;

    public SqlOSCimdClientService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        IHttpClientFactory httpClientFactory,
        SqlOSCryptoService cryptoService)
    {
        _context = context;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _cryptoService = cryptoService;
    }

    public async Task<SqlOSResolvedClient?> ResolveClientAsync(
        string clientId,
        string? redirectUri,
        SqlOSClientApplication? existingClient,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseMetadataClientId(clientId, out var clientIdUri))
        {
            return null;
        }

        if (existingClient != null && (!existingClient.IsActive || existingClient.DisabledAt != null))
        {
            throw new InvalidOperationException($"Client '{clientId}' is inactive.");
        }

        if (existingClient != null && !ShouldRefreshMetadata(existingClient))
        {
            ValidateRedirectUri(existingClient.RedirectUrisJson, redirectUri, clientId);
            existingClient.LastSeenAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return new SqlOSResolvedClient(existingClient, "cimd");
        }

        var httpClient = _httpClientFactory.CreateClient(nameof(SqlOSCimdClientService));
        using var request = new HttpRequestMessage(HttpMethod.Get, clientIdUri);

        if (!string.IsNullOrWhiteSpace(existingClient?.MetadataEtag)
            && EntityTagHeaderValue.TryParse(existingClient.MetadataEtag, out var etag))
        {
            request.Headers.IfNoneMatch.Add(etag);
        }

        if (existingClient?.MetadataLastModifiedAt is DateTime lastModified)
        {
            request.Headers.IfModifiedSince = DateTime.SpecifyKind(lastModified, DateTimeKind.Utc);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.ClientRegistration.Cimd.HttpTimeout);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        var now = DateTime.UtcNow;

        if (response.StatusCode == HttpStatusCode.NotModified && existingClient != null)
        {
            existingClient.MetadataFetchedAt = now;
            existingClient.MetadataExpiresAt = ResolveMetadataExpiry(response, now);
            existingClient.LastSeenAt = now;
            await _context.SaveChangesAsync(cancellationToken);
            ValidateRedirectUri(existingClient.RedirectUrisJson, redirectUri, clientId);
            return new SqlOSResolvedClient(existingClient, "cimd");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            await RecordAuditAsync(
                "client.cimd.fetch-failed",
                clientId,
                new
                {
                    status_code = (int)response.StatusCode
                },
                cancellationToken);
            throw new InvalidOperationException($"Client metadata document fetch failed for '{clientId}' with status {(int)response.StatusCode}.");
        }

        if (!HasJsonMediaType(response.Content.Headers.ContentType?.MediaType))
        {
            throw new InvalidOperationException($"Client metadata document for '{clientId}' must be JSON.");
        }

        var payload = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);
        if (payload.Length > _options.ClientRegistration.Cimd.MaxMetadataBytes)
        {
            throw new InvalidOperationException($"Client metadata document for '{clientId}' exceeds the allowed size.");
        }

        var rawJson = Encoding.UTF8.GetString(payload);
        using var document = JsonDocument.Parse(payload);
        ParsedCimdDocument parsed;
        try
        {
            parsed = ParseMetadataDocument(document.RootElement, clientId, redirectUri);
            await EnforceTrustPolicyAsync(clientIdUri, parsed, httpContext, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            await RecordAuditAsync(
                "client.cimd.validation-failed",
                clientId,
                new
                {
                    error = ex.Message
                },
                cancellationToken);
            throw;
        }

        var requiresFreshConsent = existingClient != null && HasSecuritySensitiveMetadataChange(existingClient, parsed);
        var client = await UpsertDiscoveredClientAsync(existingClient, parsed, rawJson, response, now, cancellationToken);

        if (requiresFreshConsent)
        {
            await RevokeActiveSessionsAsync(client.Id, cancellationToken);
            await RecordAuditAsync(
                "client.cimd.metadata-changed",
                client.ClientId,
                new
                {
                    requires_fresh_consent = true
                },
                cancellationToken);
        }

        return new SqlOSResolvedClient(client, "cimd", requiresFreshConsent);
    }

    private async Task EnforceTrustPolicyAsync(
        Uri clientIdUri,
        ParsedCimdDocument parsed,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        if (_options.ClientRegistration.Cimd.TrustedHosts.Count > 0
            && !_options.ClientRegistration.Cimd.TrustedHosts.Contains(clientIdUri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Client metadata host '{clientIdUri.Host}' is not trusted.");
        }

        var trustPolicy = _options.ClientRegistration.Cimd.TrustPolicy;
        if (trustPolicy == null)
        {
            return;
        }

        var decision = await trustPolicy(
            new SqlOSCimdTrustContext
            {
                HttpContext = httpContext ?? new DefaultHttpContext(),
                ClientId = parsed.ClientId,
                ClientIdUri = clientIdUri,
                ClientName = parsed.ClientName,
                RedirectUris = parsed.RedirectUris,
                TokenEndpointAuthMethod = parsed.TokenEndpointAuthMethod
            },
            cancellationToken);

        if (!decision.Allowed)
        {
            throw new InvalidOperationException(decision.Reason ?? $"Client metadata host '{clientIdUri.Host}' was rejected by trust policy.");
        }
    }

    private async Task<SqlOSClientApplication> UpsertDiscoveredClientAsync(
        SqlOSClientApplication? existingClient,
        ParsedCimdDocument parsed,
        string rawJson,
        HttpResponseMessage response,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var client = existingClient ?? new SqlOSClientApplication
        {
            Id = _cryptoService.GenerateId("cli"),
            ClientId = parsed.ClientId,
            CreatedAt = now,
            IsActive = true
        };

        if (existingClient == null)
        {
            _context.Set<SqlOSClientApplication>().Add(client);
        }

        client.ClientId = parsed.ClientId;
        client.Name = parsed.ClientName;
        client.Description = parsed.Description;
        client.Audience = string.IsNullOrWhiteSpace(client.Audience) ? _options.DefaultAudience : client.Audience;
        client.ClientType = "public_pkce";
        client.RegistrationSource = "cimd";
        client.TokenEndpointAuthMethod = parsed.TokenEndpointAuthMethod;
        client.GrantTypesJson = JsonSerializer.Serialize(parsed.GrantTypes);
        client.ResponseTypesJson = JsonSerializer.Serialize(parsed.ResponseTypes);
        client.RequirePkce = true;
        client.AllowedScopesJson = JsonSerializer.Serialize(parsed.Scopes);
        client.IsFirstParty = false;
        client.RedirectUrisJson = JsonSerializer.Serialize(parsed.RedirectUris);
        client.MetadataDocumentUrl = parsed.ClientId;
        client.ClientUri = parsed.ClientUri;
        client.LogoUri = parsed.LogoUri;
        client.SoftwareId = parsed.SoftwareId;
        client.SoftwareVersion = parsed.SoftwareVersion;
        client.MetadataJson = rawJson;
        client.MetadataFetchedAt = now;
        client.MetadataExpiresAt = ResolveMetadataExpiry(response, now);
        client.MetadataEtag = response.Headers.ETag?.ToString();
        client.MetadataLastModifiedAt = response.Content.Headers.LastModified?.UtcDateTime;
        client.LastSeenAt = now;

        await _context.SaveChangesAsync(cancellationToken);
        return client;
    }

    private static bool ShouldRefreshMetadata(SqlOSClientApplication client)
    {
        if (!string.Equals(client.RegistrationSource, "cimd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (client.MetadataFetchedAt == null || client.MetadataExpiresAt == null)
        {
            return true;
        }

        return client.MetadataExpiresAt <= DateTime.UtcNow;
    }

    private DateTime ResolveMetadataExpiry(HttpResponseMessage response, DateTime now)
    {
        var cacheControl = response.Headers.CacheControl;
        if (cacheControl?.NoStore == true || cacheControl?.NoCache == true)
        {
            return now;
        }

        if (cacheControl?.MaxAge is TimeSpan maxAge && maxAge > TimeSpan.Zero)
        {
            return now.Add(maxAge);
        }

        if (response.Content.Headers.Expires is DateTimeOffset expires)
        {
            var candidate = expires.UtcDateTime;
            if (candidate > now)
            {
                return candidate;
            }
        }

        return now.Add(_options.ClientRegistration.Cimd.DefaultCacheTtl);
    }

    private static void ValidateRedirectUri(string redirectUrisJson, string? redirectUri, string clientId)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return;
        }

        var redirectUris = SqlOSAdminService.DeserializeJsonList(redirectUrisJson);
        if (!redirectUris.Contains(redirectUri, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Redirect URI '{redirectUri}' is not allowed for client '{clientId}'.");
        }
    }

    private static bool HasSecuritySensitiveMetadataChange(SqlOSClientApplication existingClient, ParsedCimdDocument parsed)
    {
        var existingRedirects = SqlOSAdminService.DeserializeJsonList(existingClient.RedirectUrisJson);
        var redirectsChanged = !existingRedirects.SequenceEqual(parsed.RedirectUris, StringComparer.OrdinalIgnoreCase);
        var authMethodChanged = !string.Equals(existingClient.TokenEndpointAuthMethod, parsed.TokenEndpointAuthMethod, StringComparison.Ordinal);
        var grantTypesChanged = !SqlOSAdminService.DeserializeJsonList(existingClient.GrantTypesJson)
            .SequenceEqual(parsed.GrantTypes, StringComparer.Ordinal);
        var responseTypesChanged = !SqlOSAdminService.DeserializeJsonList(existingClient.ResponseTypesJson)
            .SequenceEqual(parsed.ResponseTypes, StringComparer.Ordinal);

        return redirectsChanged || authMethodChanged || grantTypesChanged || responseTypesChanged;
    }

    private async Task RevokeActiveSessionsAsync(string clientApplicationId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var sessions = await _context.Set<SqlOSSession>()
            .Where(x => x.ClientApplicationId == clientApplicationId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
        {
            return;
        }

        var sessionIds = sessions.Select(x => x.Id).ToList();
        var refreshTokens = await _context.Set<SqlOSRefreshToken>()
            .Where(x => sessionIds.Contains(x.SessionId) && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.RevokedAt = now;
            session.RevocationReason = "cimd_metadata_changed";
        }

        foreach (var refreshToken in refreshTokens)
        {
            refreshToken.RevokedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static ParsedCimdDocument ParseMetadataDocument(JsonElement root, string clientId, string? redirectUri)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Client metadata document for '{clientId}' must be a JSON object.");
        }

        var documentClientId = GetRequiredString(root, "client_id", clientId);
        if (!string.Equals(documentClientId, clientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Client metadata document for '{clientId}' must declare the exact same client_id.");
        }

        var clientName = GetRequiredString(root, "client_name", clientId);
        var redirectUris = GetRequiredStringArray(root, "redirect_uris", clientId);
        if (redirectUris.Count == 0)
        {
            throw new InvalidOperationException($"Client metadata document for '{clientId}' must include at least one redirect URI.");
        }

        foreach (var uri in redirectUris)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"Client metadata document for '{clientId}' contains an invalid redirect URI '{uri}'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(redirectUri)
            && !redirectUris.Contains(redirectUri, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Redirect URI '{redirectUri}' is not allowed for client '{clientId}'.");
        }

        var tokenEndpointAuthMethod = GetOptionalString(root, "token_endpoint_auth_method") ?? "none";
        if (!string.Equals(tokenEndpointAuthMethod, "none", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Client metadata document for '{clientId}' must use token_endpoint_auth_method 'none' in this SqlOS version.");
        }

        var grantTypes = GetOptionalStringArray(root, "grant_types");
        if (grantTypes.Count > 0 && !grantTypes.Contains("authorization_code", StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Client metadata document for '{clientId}' must support the authorization_code grant.");
        }

        var responseTypes = GetOptionalStringArray(root, "response_types");
        if (responseTypes.Count > 0 && !responseTypes.Contains("code", StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Client metadata document for '{clientId}' must support response type 'code'.");
        }

        var normalizedGrantTypes = grantTypes.Count > 0
            ? grantTypes
            : new List<string> { "authorization_code", "refresh_token" };
        var normalizedResponseTypes = responseTypes.Count > 0
            ? responseTypes
            : new List<string> { "code" };
        var scopes = SplitScope(GetOptionalString(root, "scope"));

        return new ParsedCimdDocument(
            documentClientId,
            clientName,
            redirectUris,
            tokenEndpointAuthMethod,
            normalizedGrantTypes,
            normalizedResponseTypes,
            scopes,
            GetOptionalString(root, "description"),
            GetOptionalString(root, "client_uri"),
            GetOptionalString(root, "logo_uri"),
            GetOptionalString(root, "software_id"),
            GetOptionalString(root, "software_version"));
    }

    private static bool TryParseMetadataClientId(string clientId, out Uri uri)
    {
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out uri!))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(uri.AbsolutePath) && !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal);
    }

    private static string GetRequiredString(JsonElement root, string propertyName, string clientId)
    {
        var value = GetOptionalString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Client metadata document for '{clientId}' is missing required field '{propertyName}'.");
        }

        return value;
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() : null;
    }

    private static List<string> GetRequiredStringArray(JsonElement root, string propertyName, string clientId)
    {
        var result = GetOptionalStringArray(root, propertyName);
        if (result.Count == 0)
        {
            throw new InvalidOperationException($"Client metadata document for '{clientId}' is missing required field '{propertyName}'.");
        }

        return result;
    }

    private static List<string> GetOptionalStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element.EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.String)
            .Select(static value => value.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> SplitScope(string? scope)
        => string.IsNullOrWhiteSpace(scope)
            ? []
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    private static bool HasJsonMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return true;
        }

        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RecordAuditAsync(string eventType, string clientId, object data, CancellationToken cancellationToken)
    {
        _context.Set<SqlOSAuditEvent>().Add(new SqlOSAuditEvent
        {
            Id = _cryptoService.GenerateId("evt"),
            EventType = eventType,
            ActorType = "client",
            ActorId = clientId,
            DataJson = JsonSerializer.Serialize(data),
            OccurredAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    private sealed record ParsedCimdDocument(
        string ClientId,
        string ClientName,
        List<string> RedirectUris,
        string TokenEndpointAuthMethod,
        List<string> GrantTypes,
        List<string> ResponseTypes,
        List<string> Scopes,
        string? Description,
        string? ClientUri,
        string? LogoUri,
        string? SoftwareId,
        string? SoftwareVersion);
}
