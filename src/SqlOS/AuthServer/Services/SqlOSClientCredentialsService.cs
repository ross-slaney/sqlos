using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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
    internal static readonly string DummyCredentialHash =
        new PasswordHasher<object>().HashPassword(new object(), "sqlos-invalid-client-dummy-secret");
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
        var grants = client == null ? [] : SqlOSAdminService.DeserializeJsonList(client.GrantTypesJson);
        var account = await _context.Set<SqlOSFgaServiceAccount>()
            .SingleOrDefaultAsync(x => x.ClientId == clientId, cancellationToken);
        var subjectIdForLookup = account?.SubjectId ?? "__invalid_service_subject__";
        var subject = await _context.Set<SqlOSFgaSubject>()
            .SingleOrDefaultAsync(x => x.Id == subjectIdForLookup, cancellationToken);
        var candidateSecret = clientSecret.Length <= 256 ? clientSecret : string.Empty;
        var credentialVerified = _crypto.VerifyPassword(
            ResolveCredentialHashForVerification(account),
            candidateSecret);
        var now = DateTime.UtcNow;
        var valid = client != null
            && client.IsActive
            && client.DisabledAt == null
            && string.Equals(client.ClientType, "confidential", StringComparison.Ordinal)
            && string.Equals(client.TokenEndpointAuthMethod, "client_secret_basic", StringComparison.Ordinal)
            && grants.Contains(SqlOSOAuthGrantTypes.ClientCredentials, StringComparer.Ordinal)
            && account != null
            && subject != null
            && string.Equals(subject.SubjectTypeId, "service_account", StringComparison.Ordinal)
            && (account.ExpiresAt == null || account.ExpiresAt > now)
            && clientSecret.Length is >= 43 and <= 256
            && credentialVerified;
        if (!valid)
        {
            await AuditAsync("oauth.client_credentials.failed", clientId, account?.SubjectId, httpContext, cancellationToken);
            throw new SqlOSClientCredentialsException("invalid_client", "Client authentication failed.", StatusCodes.Status401Unauthorized);
        }

        if (string.IsNullOrWhiteSpace(resource)
            || !string.Equals(resource, client!.Audience, StringComparison.Ordinal))
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

        var token = await _crypto.CreateServiceAccessTokenAsync(
            account!.SubjectId,
            client,
            resource,
            scopes,
            subject!.OrganizationId,
            cancellationToken);
        account.LastUsedAt = now;
        account.UpdatedAt = now;
        client.LastSeenAt = now;
        await _context.SaveChangesAsync(cancellationToken);
        await AuditAsync("oauth.client_credentials.issued", clientId, account.SubjectId, httpContext, cancellationToken);
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
        account.ClientSecretHash = _crypto.HashPassword(newSecret);
        account.UpdatedAt = DateTime.UtcNow;
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
        account.ExpiresAt = DateTime.UtcNow;
        account.UpdatedAt = account.ExpiresAt.Value;
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

    internal static string ResolveCredentialHashForVerification(SqlOSFgaServiceAccount? account)
        => string.IsNullOrWhiteSpace(account?.ClientSecretHash)
            ? DummyCredentialHash
            : account.ClientSecretHash;

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
