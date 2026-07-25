using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Owns OAuth client credentials and enforces the registered token-endpoint
/// authentication method independently from any FGA subject.
/// </summary>
public sealed class SqlOSClientAuthenticationService
{
    internal static readonly string DummyCredentialHash =
        new PasswordHasher<object>().HashPassword(new object(), "sqlos-invalid-client-dummy-secret");
    private const int MaximumActiveCredentials = 5;
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _crypto;
    private readonly SqlOSAdminService _admin;

    public SqlOSClientAuthenticationService(
        ISqlOSAuthServerDbContext context,
        SqlOSCryptoService crypto,
        SqlOSAdminService admin)
    {
        _context = context;
        _crypto = crypto;
        _admin = admin;
    }

    public async Task<SqlOSClientApplication> AuthenticateTokenEndpointClientAsync(
        IFormCollection form,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var presented = ParsePresentedCredentials(form, httpContext.Request.Headers.Authorization);
        var candidateClientId = presented.BasicClientId ?? presented.BodyClientId;
        var client = string.IsNullOrWhiteSpace(candidateClientId)
            ? null
            : await _context.Set<SqlOSClientApplication>()
                .SingleOrDefaultAsync(x => x.ClientId == candidateClientId, cancellationToken);
        var now = DateTime.UtcNow;

        if (client != null
            && client.IsActive
            && client.DisabledAt == null
            && string.Equals(client.TokenEndpointAuthMethod, "none", StringComparison.Ordinal)
            && !presented.HasAuthorizationHeader
            && !presented.HasBodySecret
            && presented.IsUnambiguous
            && !string.IsNullOrWhiteSpace(presented.BodyClientId))
        {
            return client;
        }

        var credentials = client == null
            ? []
            : await _context.Set<SqlOSClientCredential>()
                .Where(x => x.ClientApplicationId == client.Id
                    && x.RevokedAt == null
                    && (x.ExpiresAt == null || x.ExpiresAt > now))
                .OrderBy(x => x.CreatedAt)
                .Take(MaximumActiveCredentials + 1)
                .ToListAsync(cancellationToken);
        var candidateSecret = presented.BasicSecret is { Length: >= 43 and <= 256 }
            ? presented.BasicSecret
            : string.Empty;
        SqlOSClientCredential? matchedCredential = null;
        if (credentials.Count == 0)
        {
            _ = _crypto.VerifyPassword(DummyCredentialHash, candidateSecret);
        }
        else
        {
            foreach (var credential in credentials)
            {
                if (_crypto.VerifyPassword(credential.SecretHash, candidateSecret))
                {
                    matchedCredential ??= credential;
                }
            }
        }

        var valid = client != null
            && client.IsActive
            && client.DisabledAt == null
            && string.Equals(client.ClientType, "confidential", StringComparison.Ordinal)
            && string.Equals(client.TokenEndpointAuthMethod, "client_secret_basic", StringComparison.Ordinal)
            && presented.IsUnambiguous
            && presented.HasBasicCredentials
            && presented.BasicSecret is { Length: >= 43 and <= 256 }
            && !presented.HasBodySecret
            && (string.IsNullOrWhiteSpace(presented.BodyClientId)
                || string.Equals(presented.BodyClientId, presented.BasicClientId, StringComparison.Ordinal))
            && credentials.Count <= MaximumActiveCredentials
            && matchedCredential != null;
        if (!valid)
        {
            await AuditAsync("oauth.client_authentication.failed", candidateClientId, httpContext, cancellationToken);
            throw new SqlOSClientAuthenticationException();
        }

        matchedCredential!.LastUsedAt = now;
        client!.LastSeenAt = now;
        await _context.SaveChangesAsync(cancellationToken);
        await AuditAsync("oauth.client_authentication.succeeded", client.ClientId, httpContext, cancellationToken);
        return client;
    }

    public async Task<IReadOnlyList<SqlOSClientCredentialDto>> ListCredentialsAsync(
        string clientApplicationId,
        CancellationToken cancellationToken = default)
    {
        await RequireClientAsync(clientApplicationId, cancellationToken);
        return await _context.Set<SqlOSClientCredential>()
            .AsNoTracking()
            .Where(x => x.ClientApplicationId == clientApplicationId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new SqlOSClientCredentialDto(
                x.Id,
                x.DisplayName,
                x.CreatedAt,
                x.ExpiresAt,
                x.RevokedAt,
                x.LastUsedAt,
                x.ConfigurationOwner,
                x.ConfigurationSourceKey))
            .ToListAsync(cancellationToken);
    }

    public async Task<SqlOSClientCredentialCreated> CreateCredentialAsync(
        string clientApplicationId,
        string? displayName = null,
        DateTime? expiresAt = null,
        string? actorId = null,
        CancellationToken cancellationToken = default)
    {
        var client = await RequireClientAsync(clientApplicationId, cancellationToken);
        SqlOSConfigurationOwnershipPolicy.EnsureDashboardEditable(client.ConfigurationOwner, $"OAuth client '{client.ClientId}'");
        if (!string.Equals(client.TokenEndpointAuthMethod, "client_secret_basic", StringComparison.Ordinal)
            || !string.Equals(client.ClientType, "confidential", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Client credentials can only be issued to confidential clients using client_secret_basic.");
        }

        var now = DateTime.UtcNow;
        var activeCount = await _context.Set<SqlOSClientCredential>()
            .CountAsync(x => x.ClientApplicationId == client.Id
                && x.RevokedAt == null
                && (x.ExpiresAt == null || x.ExpiresAt > now), cancellationToken);
        if (activeCount >= MaximumActiveCredentials)
        {
            throw new InvalidOperationException($"A client can have at most {MaximumActiveCredentials} active credentials.");
        }

        var secret = _crypto.GenerateOpaqueToken(48);
        var credential = new SqlOSClientCredential
        {
            Id = _crypto.GenerateId("clcred"),
            ClientApplicationId = client.Id,
            SecretHash = _crypto.HashPassword(secret),
            DisplayName = NormalizeDisplayName(displayName),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            ConfigurationOwner = SqlOSConfigurationOwners.Dashboard
        };
        _context.Set<SqlOSClientCredential>().Add(credential);
        await _context.SaveChangesAsync(cancellationToken);
        await _admin.RecordAuditAsync(
            "oauth.client_credential.created",
            "admin",
            actorId,
            data: new { clientId = client.ClientId, credentialId = credential.Id, credential.ExpiresAt },
            cancellationToken: cancellationToken);
        return new SqlOSClientCredentialCreated(ToDto(credential), secret);
    }

    public async Task RevokeCredentialAsync(
        string clientApplicationId,
        string credentialId,
        string? actorId = null,
        CancellationToken cancellationToken = default)
    {
        var client = await RequireClientAsync(clientApplicationId, cancellationToken);
        var credential = await _context.Set<SqlOSClientCredential>()
            .SingleOrDefaultAsync(x => x.Id == credentialId && x.ClientApplicationId == client.Id, cancellationToken)
            ?? throw new InvalidOperationException("Client credential not found.");
        SqlOSConfigurationOwnershipPolicy.EnsureDashboardEditable(
            credential.ConfigurationOwner,
            $"OAuth client credential '{credential.Id}'");
        credential.RevokedAt ??= DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await _admin.RecordAuditAsync(
            "oauth.client_credential.revoked",
            "admin",
            actorId,
            data: new { clientId = client.ClientId, credentialId = credential.Id },
            cancellationToken: cancellationToken);
    }

    private async Task<SqlOSClientApplication> RequireClientAsync(
        string clientApplicationId,
        CancellationToken cancellationToken)
        => await _context.Set<SqlOSClientApplication>()
            .SingleOrDefaultAsync(
                x => x.Id == clientApplicationId || x.ClientId == clientApplicationId,
                cancellationToken)
            ?? throw new InvalidOperationException("OAuth client not found.");

    private Task AuditAsync(
        string action,
        string? clientId,
        HttpContext httpContext,
        CancellationToken cancellationToken)
        => _admin.RecordAuditAsync(
            action,
            "oauth_client",
            string.IsNullOrWhiteSpace(clientId) ? null : clientId,
            ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
            data: new { clientId },
            cancellationToken: cancellationToken);

    private static PresentedCredentials ParsePresentedCredentials(
        IFormCollection form,
        Microsoft.Extensions.Primitives.StringValues authorizationHeaders)
    {
        var bodyClientIds = form["client_id"];
        var bodySecrets = form["client_secret"];
        var isUnambiguous = bodyClientIds.Count <= 1
            && bodySecrets.Count <= 1
            && authorizationHeaders.Count <= 1;
        var authorization = authorizationHeaders.ToString();
        var hasAuthorizationHeader = !string.IsNullOrWhiteSpace(authorization);
        string? basicClientId = null;
        string? basicSecret = null;
        var hasBasicCredentials = false;

        if (hasAuthorizationHeader
            && AuthenticationHeaderValue.TryParse(authorization, out var parsed)
            && string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(parsed.Parameter))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
                var separator = decoded.IndexOf(':');
                if (separator > 0)
                {
                    basicClientId = WebUtility.UrlDecode(decoded[..separator]);
                    basicSecret = WebUtility.UrlDecode(decoded[(separator + 1)..]);
                    hasBasicCredentials = !string.IsNullOrWhiteSpace(basicClientId);
                }
            }
            catch (FormatException)
            {
            }
        }

        return new PresentedCredentials(
            bodyClientIds.Count == 1 ? bodyClientIds.ToString() : null,
            bodySecrets.Count == 1,
            basicClientId,
            basicSecret,
            hasAuthorizationHeader,
            hasBasicCredentials,
            isUnambiguous);
    }

    private static string? NormalizeDisplayName(string? displayName)
    {
        var normalized = displayName?.Trim();
        if (normalized?.Length > 200)
        {
            throw new InvalidOperationException("Credential display name cannot exceed 200 characters.");
        }
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static SqlOSClientCredentialDto ToDto(SqlOSClientCredential credential)
        => new(
            credential.Id,
            credential.DisplayName,
            credential.CreatedAt,
            credential.ExpiresAt,
            credential.RevokedAt,
            credential.LastUsedAt,
            credential.ConfigurationOwner,
            credential.ConfigurationSourceKey);

    private sealed record PresentedCredentials(
        string? BodyClientId,
        bool HasBodySecret,
        string? BasicClientId,
        string? BasicSecret,
        bool HasAuthorizationHeader,
        bool HasBasicCredentials,
        bool IsUnambiguous);
}

public sealed record SqlOSClientCredentialDto(
    string Id,
    string? DisplayName,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    DateTime? LastUsedAt,
    string ConfigurationOwner,
    string? ConfigurationSourceKey);

public sealed record SqlOSClientCredentialCreated(
    SqlOSClientCredentialDto Credential,
    string ClientSecret);

public sealed record SqlOSCreateClientCredentialRequest(
    string? DisplayName = null,
    DateTime? ExpiresAt = null);

public sealed class SqlOSClientAuthenticationException : Exception
{
    public SqlOSClientAuthenticationException()
        : base("Client authentication failed.")
    {
    }

    public string Error => "invalid_client";
}
