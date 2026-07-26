using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Fga.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSClientCredentialsService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _crypto;
    private readonly SqlOSAdminService _admin;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSClientCredentialsService(
        ISqlOSAuthServerDbContext context,
        SqlOSCryptoService crypto,
        SqlOSAdminService admin,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _crypto = crypto;
        _admin = admin;
        _options = options.Value;
    }

    public async Task<SqlOSClientCredentialsTokenResult> ExchangeAsync(
        string clientId,
        string clientSecret,
        string resource,
        string? requestedScope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var client = await _context.Set<SqlOSClientApplication>()
            .SingleOrDefaultAsync(x => x.ClientId == clientId, cancellationToken);
        var now = DateTime.UtcNow;
        var credentials = client == null
            ? []
            : await _context.Set<SqlOSClientCredential>()
                .Where(x => x.ClientApplicationId == client.Id
                    && x.RevokedAt == null
                    && (x.ExpiresAt == null || x.ExpiresAt > now))
                .ToListAsync(cancellationToken);
        var candidateSecret = clientSecret.Length <= 256 ? clientSecret : string.Empty;
        var credentialVerified = false;
        if (credentials.Count == 0)
        {
            _ = _crypto.VerifyPassword(SqlOSClientAuthenticationService.DummyCredentialHash, candidateSecret);
        }
        else
        {
            foreach (var credential in credentials)
            {
                credentialVerified |= _crypto.VerifyPassword(credential.SecretHash, candidateSecret);
            }
        }
        if (client == null
            || !client.IsActive
            || client.DisabledAt != null
            || !string.Equals(client.ClientType, "confidential", StringComparison.Ordinal)
            || !string.Equals(client.TokenEndpointAuthMethod, "client_secret_basic", StringComparison.Ordinal)
            || clientSecret.Length is < 43 or > 256
            || !credentialVerified)
        {
            await AuditAsync("oauth.client_credentials.failed", clientId, null, httpContext, cancellationToken);
            throw new SqlOSClientCredentialsException("invalid_client", "Client authentication failed.", StatusCodes.Status401Unauthorized);
        }

        return await ExchangeAsync(client, resource, requestedScope, httpContext, cancellationToken);
    }

    public async Task<SqlOSClientCredentialsTokenResult> ExchangeAsync(
        SqlOSClientApplication client,
        string resource,
        string? requestedScope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var grants = SqlOSAdminService.DeserializeJsonList(client.GrantTypesJson);
        var now = DateTime.UtcNow;
        if (!client.IsActive
            || client.DisabledAt != null
            || !string.Equals(client.ClientType, "confidential", StringComparison.Ordinal)
            || !string.Equals(client.TokenEndpointAuthMethod, "client_secret_basic", StringComparison.Ordinal)
            || !grants.Contains(SqlOSOAuthGrantTypes.ClientCredentials, StringComparer.Ordinal))
        {
            await AuditAsync("oauth.client_credentials.failed", client.ClientId, null, httpContext, cancellationToken);
            throw new SqlOSClientCredentialsException("unauthorized_client", "The client is not authorized to use this grant.");
        }

        var account = await _context.Set<SqlOSFgaServiceAccount>()
            .Include(x => x.Subject)
            .SingleOrDefaultAsync(x => x.ClientId == client.ClientId, cancellationToken);
        if (account?.ExpiresAt <= now)
        {
            await AuditAsync("oauth.client_credentials.failed", client.ClientId, account.SubjectId, httpContext, cancellationToken);
            throw new SqlOSClientCredentialsException("invalid_client", "Client authentication failed.", StatusCodes.Status401Unauthorized);
        }

        if (string.IsNullOrWhiteSpace(resource)
            || !string.Equals(resource, client.Audience, StringComparison.Ordinal))
        {
            throw new SqlOSClientCredentialsException("invalid_target", "An authorized resource is required.");
        }

        var allowed = SqlOSAdminService.DeserializeJsonList(client.AllowedScopesJson);
        var scopes = (requestedScope ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (scopes.Any(scope => !allowed.Contains(scope, StringComparer.Ordinal)))
        {
            throw new SqlOSClientCredentialsException("invalid_scope", "The requested scope is not authorized.");
        }

        var tokenSubject = account?.SubjectId ?? client.ClientId;
        var token = await _crypto.CreateServiceAccessTokenAsync(
            tokenSubject,
            client,
            resource,
            scopes,
            account?.Subject?.OrganizationId,
            cancellationToken);
        if (account != null)
        {
            account.LastUsedAt = now;
            account.UpdatedAt = now;
        }
        client.LastSeenAt = now;
        await _context.SaveChangesAsync(cancellationToken);
        await AuditAsync("oauth.client_credentials.issued", client.ClientId, account?.SubjectId, httpContext, cancellationToken);
        return new SqlOSClientCredentialsTokenResult(token, now.Add(_options.AccessTokenLifetime), scopes);
    }

    public async Task RotateSecretAsync(
        string clientId,
        string newSecret,
        string? actorId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newSecret) || newSecret.Length is < 43 or > 256)
        {
            throw new ArgumentException("A service-client secret must contain 43 to 256 characters.", nameof(newSecret));
        }

        var account = await RequireServiceAccountAsync(clientId, cancellationToken);
        SqlOSConfigurationOwnershipPolicy.EnsureDashboardEditable(
            account.ConfigurationOwner,
            $"Machine client '{clientId}'");
        var client = await _context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == clientId, cancellationToken);
        var now = DateTime.UtcNow;
        var secretHash = _crypto.HashPassword(newSecret);
        account.ClientSecretHash = secretHash;
        account.UpdatedAt = now;
        var activeCredentials = await _context.Set<SqlOSClientCredential>()
            .Where(x => x.ClientApplicationId == client.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var credential in activeCredentials)
        {
            credential.RevokedAt = now;
        }
        _context.Set<SqlOSClientCredential>().Add(new SqlOSClientCredential
        {
            Id = _crypto.GenerateId("clcred"),
            ClientApplicationId = client.Id,
            SecretHash = secretHash,
            DisplayName = "Machine client credential",
            CreatedAt = now,
            ConfigurationOwner = account.ConfigurationOwner,
            ConfigurationSourceKey = account.ConfigurationOwner == SqlOSConfigurationOwners.Code ? "primary" : null,
            LastReconciledAt = account.LastReconciledAt
        });
        await _context.SaveChangesAsync(cancellationToken);
        await _admin.RecordAuditAsync(
            "oauth.client_credentials.rotated",
            "admin",
            actorId,
            organizationId: account.Subject?.OrganizationId,
            data: new { clientId, subjectId = account.SubjectId },
            cancellationToken: cancellationToken);
    }

    public async Task RevokeAsync(
        string clientId,
        string? actorId = null,
        CancellationToken cancellationToken = default)
    {
        var account = await RequireServiceAccountAsync(clientId, cancellationToken);
        var client = await _context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == clientId, cancellationToken);
        account.ExpiresAt = DateTime.UtcNow;
        account.UpdatedAt = account.ExpiresAt.Value;
        var credentials = await _context.Set<SqlOSClientCredential>()
            .Where(x => x.ClientApplicationId == client.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var credential in credentials)
        {
            credential.RevokedAt = account.ExpiresAt;
        }
        await _context.SaveChangesAsync(cancellationToken);
        await _admin.RecordAuditAsync(
            "oauth.client_credentials.revoked",
            "admin",
            actorId,
            organizationId: account.Subject?.OrganizationId,
            data: new { clientId, subjectId = account.SubjectId },
            cancellationToken: cancellationToken);
    }

    private async Task<SqlOSFgaServiceAccount> RequireServiceAccountAsync(
        string clientId,
        CancellationToken cancellationToken)
    {
        var account = await _context.Set<SqlOSFgaServiceAccount>()
            .Include(x => x.Subject)
            .SingleOrDefaultAsync(x => x.ClientId == clientId, cancellationToken);
        if (account == null)
        {
            throw new InvalidOperationException("The service client does not exist.");
        }
        return account;
    }

    private Task AuditAsync(
        string action,
        string clientId,
        string? subjectId,
        HttpContext httpContext,
        CancellationToken cancellationToken)
        => _admin.RecordAuditAsync(
            action,
            "oauth_client",
            clientId,
            organizationId: null,
            ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
            data: new { clientId, subjectId },
            cancellationToken: cancellationToken);
}

public sealed record SqlOSClientCredentialsTokenResult(
    string AccessToken,
    DateTime ExpiresAt,
    IReadOnlyList<string> Scopes);

public sealed class SqlOSClientCredentialsException : Exception
{
    public SqlOSClientCredentialsException(string error, string description, int statusCode = StatusCodes.Status400BadRequest)
        : base(description)
    {
        Error = error;
        StatusCode = statusCode;
    }

    public string Error { get; }
    public int StatusCode { get; }
}
