using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSClientResolutionService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSCimdClientService? _cimdClientService;

    public SqlOSClientResolutionService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options)
        : this(context, options, null)
    {
    }

    public SqlOSClientResolutionService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCimdClientService? cimdClientService)
    {
        _context = context;
        _options = options.Value;
        _cimdClientService = cimdClientService;
    }

    public async Task<SqlOSResolvedClient?> TryResolveClientAsync(
        string? clientId,
        string? redirectUri,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var normalizedClientId = clientId.Trim();

        var localClient = await _context.Set<SqlOSClientApplication>()
            .FirstOrDefaultAsync(x => x.ClientId == normalizedClientId, cancellationToken);

        if (localClient != null
            && !string.Equals(localClient.RegistrationSource, "cimd", StringComparison.OrdinalIgnoreCase))
        {
            ValidateResolvedClient(localClient, redirectUri);
            await TouchLastSeenAsync(localClient, cancellationToken);
            return new SqlOSResolvedClient(localClient, "stored");
        }

        if (_options.ClientRegistration.Cimd.Enabled && LooksLikeMetadataDocumentClientId(normalizedClientId))
        {
            var discoveredClient = await TryResolveDiscoveredClientAsync(
                normalizedClientId,
                redirectUri,
                httpContext,
                localClient,
                cancellationToken);
            if (discoveredClient != null)
            {
                return discoveredClient;
            }
        }

        return null;
    }

    public async Task<SqlOSResolvedClient> ResolveRequiredClientAsync(
        string? clientId,
        string? redirectUri,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => await TryResolveClientAsync(clientId, redirectUri, httpContext, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown client '{clientId}'.");

    private static void ValidateResolvedClient(SqlOSClientApplication client, string? redirectUri)
    {
        if (!client.IsActive || client.DisabledAt != null)
        {
            throw new InvalidOperationException($"Client '{client.ClientId}' is inactive.");
        }

        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return;
        }

        var allowedRedirects = SqlOSAdminService.DeserializeJsonList(client.RedirectUrisJson);
        if (!allowedRedirects.Contains(redirectUri, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Redirect URI '{redirectUri}' is not allowed for client '{client.ClientId}'.");
        }
    }

    private async Task TouchLastSeenAsync(SqlOSClientApplication client, CancellationToken cancellationToken)
    {
        client.LastSeenAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static bool LooksLikeMetadataDocumentClientId(string clientId)
    {
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(uri.AbsolutePath) && !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal);
    }

    private Task<SqlOSResolvedClient?> TryResolveDiscoveredClientAsync(
        string clientId,
        string? redirectUri,
        HttpContext? httpContext,
        SqlOSClientApplication? existingClient,
        CancellationToken cancellationToken)
        => _cimdClientService?.ResolveClientAsync(clientId, redirectUri, existingClient, httpContext, cancellationToken)
            ?? Task.FromResult<SqlOSResolvedClient?>(null);
}

public sealed record SqlOSResolvedClient(
    SqlOSClientApplication Client,
    string ResolutionKind,
    bool RequiresFreshConsent = false);
