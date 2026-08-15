using System.Data;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Pagination;

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

    /// <summary>
    /// Resolves the OAuth client for a refresh-token grant.
    /// </summary>
    /// <remarks>
    /// A refresh token is already bound to a client. Public clients
    /// (<c>token_endpoint_auth_method=none</c>) therefore do not authenticate
    /// at this gate: <c>client_id</c> is optional and, when sent, is only a
    /// consistency check in <see cref="SqlOSAuthService.RefreshAsync"/>.
    /// Confidential clients still must present <c>client_secret_basic</c>.
    /// Presenting a secret or Authorization header always uses the same
    /// authentication path as authorization-code exchange.
    /// </remarks>
    public async Task<string?> AuthenticateRefreshGrantClientAsync(
        IFormCollection form,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var presented = ParsePresentedCredentials(form, httpContext.Request.Headers.Authorization);
        var boundClient = await TryGetRefreshTokenBoundClientAsync(
            form["refresh_token"].ToString(),
            cancellationToken);
        var publicBoundClient = IsActivePublicTokenEndpointClient(boundClient);
        var presentedSecret = presented.HasAuthorizationHeader
            || presented.HasBodySecret
            || presented.HasBasicCredentials;

        if (publicBoundClient && !presentedSecret)
        {
            if (!presented.IsUnambiguous)
            {
                await AuditAsync("oauth.client_authentication.failed", presented.BodyClientId, httpContext, cancellationToken);
                throw new SqlOSClientAuthenticationException();
            }

            return FirstNonEmpty(presented.BodyClientId, presented.BasicClientId);
        }

        var authenticated = await AuthenticateTokenEndpointClientAsync(form, httpContext, cancellationToken);
        return authenticated.ClientId;
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

    public async Task<object> ListCredentialsAsync(
        string clientApplicationId,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        var client = await RequireClientAsync(clientApplicationId, cancellationToken);
        SqlOSCursorPagination.RejectLegacyOffset(page);
        var size = SqlOSCursorPagination.NormalizePageSize(pageSize, 10);
        var pageResult = await SqlOSCursorPagination.ToPageAsync(
            _context.Set<SqlOSClientCredential>().AsNoTracking().Where(x => x.ClientApplicationId == client.Id),
            SqlOSKeyset<SqlOSClientCredential>.Create().Descending(x => x.CreatedAt).ThenDescending(x => x.Id),
            "auth.client-credentials",
            SqlOSCursorCodec.Fingerprint(client.Id),
            cursor,
            size,
            cancellationToken);
        return pageResult.ToResponse(x => new SqlOSClientCredentialDto(
            x.Id,
            x.DisplayName,
            x.CreatedAt,
            x.ExpiresAt,
            x.RevokedAt,
            x.LastUsedAt,
            x.ConfigurationOwner,
            x.ConfigurationSourceKey));
    }

    public async Task<SqlOSClientCredentialCreated> CreateCredentialAsync(
        string clientApplicationId,
        string? displayName = null,
        DateTime? expiresAt = null,
        string? actorId = null,
        CancellationToken cancellationToken = default)
    {
        var secret = _crypto.GenerateOpaqueToken(48);
        var prepared = new PreparedCredential(
            _crypto.GenerateId("clcred"),
            _crypto.HashPassword(secret),
            secret,
            NormalizeDisplayName(displayName),
            DateTime.UtcNow,
            expiresAt);
        if (!_context.Database.IsRelational())
        {
            return await CreateCredentialCoreAsync(
                clientApplicationId,
                prepared,
                actorId,
                cancellationToken);
        }

        if (_context.Database.CurrentTransaction != null)
        {
            return await CreateCredentialCoreAsync(
                clientApplicationId,
                prepared,
                actorId,
                cancellationToken);
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
                IsolationLevel.Serializable,
                cancellationToken);
            var created = await CreateCredentialCoreAsync(
                clientApplicationId,
                prepared,
                actorId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return created;
        });
    }

    private async Task<SqlOSClientCredentialCreated> CreateCredentialCoreAsync(
        string clientApplicationId,
        PreparedCredential prepared,
        string? actorId,
        CancellationToken cancellationToken)
    {
        var client = await RequireClientAsync(clientApplicationId, cancellationToken);
        await AcquireCredentialCreationLockAsync(client.Id, cancellationToken);
        SqlOSConfigurationOwnershipPolicy.EnsureDashboardEditable(client.ConfigurationOwner, $"OAuth client '{client.ClientId}'");
        if (!string.Equals(client.TokenEndpointAuthMethod, "client_secret_basic", StringComparison.Ordinal)
            || !string.Equals(client.ClientType, "confidential", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Client credentials can only be issued to confidential clients using client_secret_basic.");
        }

        var existing = await _context.Set<SqlOSClientCredential>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == prepared.Id, cancellationToken);
        if (existing != null)
        {
            return new SqlOSClientCredentialCreated(ToDto(existing), prepared.Secret);
        }

        var now = prepared.CreatedAt;
        var activeCount = await _context.Set<SqlOSClientCredential>()
            .CountAsync(x => x.ClientApplicationId == client.Id
                && x.RevokedAt == null
                && (x.ExpiresAt == null || x.ExpiresAt > now), cancellationToken);
        if (activeCount >= MaximumActiveCredentials)
        {
            throw new InvalidOperationException($"A client can have at most {MaximumActiveCredentials} active credentials.");
        }

        var credential = new SqlOSClientCredential
        {
            Id = prepared.Id,
            ClientApplicationId = client.Id,
            SecretHash = prepared.SecretHash,
            DisplayName = prepared.DisplayName,
            CreatedAt = now,
            ExpiresAt = prepared.ExpiresAt,
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
        return new SqlOSClientCredentialCreated(ToDto(credential), prepared.Secret);
    }

    private async Task AcquireCredentialCreationLockAsync(
        string clientApplicationId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
            _context.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.Ordinal))
        {
            return;
        }

        var resource = $"SqlOS:ClientCredentialCreation:{clientApplicationId}";
        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 30000;
            IF @result < 0 THROW 51000, 'Could not acquire the client-credential creation lock.', 1;
            """, cancellationToken);
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

    private async Task<SqlOSClientApplication?> TryGetRefreshTokenBoundClientAsync(
        string rawRefreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return null;
        }

        var hashedToken = _crypto.HashToken(rawRefreshToken);
        var refreshToken = await _context.Set<SqlOSRefreshToken>()
            .AsNoTracking()
            .Include(x => x.Session)
            .ThenInclude(x => x!.ClientApplication)
            .FirstOrDefaultAsync(x => x.TokenHash == hashedToken, cancellationToken);
        return refreshToken?.Session?.ClientApplication;
    }

    private static bool IsActivePublicTokenEndpointClient(SqlOSClientApplication? client)
        => client != null
            && client.IsActive
            && client.DisabledAt == null
            && string.Equals(client.TokenEndpointAuthMethod, "none", StringComparison.Ordinal);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

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

    private sealed record PreparedCredential(
        string Id,
        string SecretHash,
        string Secret,
        string? DisplayName,
        DateTime CreatedAt,
        DateTime? ExpiresAt);
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
