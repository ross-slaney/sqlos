using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Serves OpenID Connect UserInfo responses. Claim release is gated by the scope
/// granted to the session at authorization time: <c>openid</c> is required at all,
/// <c>profile</c> releases <c>name</c>/<c>preferred_username</c>/<c>updated_at</c>,
/// and <c>email</c> releases <c>email</c>/<c>email_verified</c>. Token validation
/// runs the shared access-token core, so session revocation, idle/absolute expiry,
/// and user/organization lifecycle denials stop claim release before JWT expiry.
/// </summary>
public sealed class SqlOSUserInfoService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _cryptoService;

    public SqlOSUserInfoService(ISqlOSAuthServerDbContext context, SqlOSCryptoService cryptoService)
    {
        _context = context;
        _cryptoService = cryptoService;
    }

    public async Task<SqlOSUserInfoResult> GetUserInfoAsync(string? bearerToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return SqlOSUserInfoResult.Challenge(
                StatusCodes401Unauthorized,
                error: null,
                errorDescription: null);
        }

        var validated = await _cryptoService.ValidateAccessTokenForUserInfoAsync(bearerToken, cancellationToken);
        if (validated?.UserId == null)
        {
            return SqlOSUserInfoResult.Challenge(
                StatusCodes401Unauthorized,
                "invalid_token",
                "The access token is invalid, expired, or its session is no longer active.");
        }

        var session = await _context.Set<SqlOSSession>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == validated.SessionId, cancellationToken);
        if (session == null)
        {
            return SqlOSUserInfoResult.Challenge(
                StatusCodes401Unauthorized,
                "invalid_token",
                "The access token is invalid, expired, or its session is no longer active.");
        }

        var grantedScopes = SqlOSScopePolicy.Split(session.Scope);
        if (!grantedScopes.Contains(SqlOSOpenIdScopeWarnings.OpenIdScope, StringComparer.Ordinal))
        {
            return SqlOSUserInfoResult.Challenge(
                StatusCodes403Forbidden,
                "insufficient_scope",
                "The access token was not granted the openid scope.");
        }

        var user = await _context.Set<SqlOSUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == validated.UserId && x.IsActive, cancellationToken);
        if (user == null)
        {
            return SqlOSUserInfoResult.Challenge(
                StatusCodes401Unauthorized,
                "invalid_token",
                "The access token is invalid, expired, or its session is no longer active.");
        }

        var claims = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sub"] = user.Id
        };

        if (grantedScopes.Contains("profile", StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(user.DisplayName))
            {
                claims["name"] = user.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(user.DefaultEmail))
            {
                claims["preferred_username"] = user.DefaultEmail;
            }

            claims["updated_at"] = SqlOSCryptoService.ToUtcEpochSeconds(user.UpdatedAt);
        }

        if (grantedScopes.Contains("email", StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(user.DefaultEmail))
        {
            claims["email"] = user.DefaultEmail;
            claims["email_verified"] = await _cryptoService.IsDefaultEmailVerifiedAsync(user, cancellationToken);
        }

        var authenticationMethods = SqlOSMfaPolicyService
            .SplitAuthenticationMethods(session.AuthenticationMethod ?? "password")
            .ToArray();
        claims["amr"] = authenticationMethods;

        // The presented access token is authoritative for the organization: an
        // organization-switch refresh deliberately leaves the session on the
        // source organization while minting tokens for the target, so projecting
        // the session here would contradict both the access token and ID token.
        if (!string.IsNullOrWhiteSpace(validated.OrganizationId))
        {
            claims["org_id"] = validated.OrganizationId;
        }

        return SqlOSUserInfoResult.Success(claims);
    }

    private const int StatusCodes401Unauthorized = 401;
    private const int StatusCodes403Forbidden = 403;
}

/// <summary>
/// Typed outcome of a UserInfo request: either a claims document or an RFC 6750
/// Bearer challenge with a status code, error code, and description.
/// </summary>
public sealed record SqlOSUserInfoResult(
    IReadOnlyDictionary<string, object?>? Claims,
    int StatusCode,
    string? Error,
    string? ErrorDescription)
{
    public static SqlOSUserInfoResult Success(IReadOnlyDictionary<string, object?> claims)
        => new(claims, 200, null, null);

    public static SqlOSUserInfoResult Challenge(int statusCode, string? error, string? errorDescription)
        => new(null, statusCode, error, errorDescription);
}
