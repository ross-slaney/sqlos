using System.Security.Claims;
using System.Security.Cryptography;
using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using System.IdentityModel.Tokens.Jwt;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSCryptoService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSValidationSigningKeyCache _validationSigningKeyCache;
    private readonly PasswordHasher<object> _passwordHasher = new();
    private readonly IDataProtector? _secretProtector;
    private readonly ITimeLimitedDataProtector? _refreshTokenResponseProtector;
    private readonly ISqlOSSigningKeyCustody _signingKeyCustody;

    public SqlOSCryptoService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        IDataProtectionProvider? dataProtectionProvider = null)
        : this(
            context,
            options,
            new SqlOSDataProtectionSigningKeyCustody(dataProtectionProvider),
            dataProtectionProvider,
            validationSigningKeyCache: null)
    {
    }

    internal SqlOSCryptoService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        ISqlOSSigningKeyCustody signingKeyCustody,
        IDataProtectionProvider? dataProtectionProvider = null,
        SqlOSValidationSigningKeyCache? validationSigningKeyCache = null)
    {
        _context = context;
        _options = options.Value;
        _validationSigningKeyCache = validationSigningKeyCache ?? new SqlOSValidationSigningKeyCache();
        _secretProtector = dataProtectionProvider?.CreateProtector("SqlOS.AuthServer.OidcSecrets");
        _refreshTokenResponseProtector = dataProtectionProvider?
            .CreateProtector("SqlOS.AuthServer.RefreshTokenResponse.v1")
            .ToTimeLimitedDataProtector();
        _signingKeyCustody = signingKeyCustody;
    }

    public string HashPassword(string password) => _passwordHasher.HashPassword(new object(), password);

    public string ProtectSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return string.Empty;
        }

        if (_secretProtector == null)
        {
            return secret;
        }

        return $"dp:{_secretProtector.Protect(secret)}";
    }

    public string UnprotectSecret(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return string.Empty;
        }

        if (!protectedSecret.StartsWith("dp:", StringComparison.Ordinal))
        {
            return protectedSecret;
        }

        if (_secretProtector == null)
        {
            throw new InvalidOperationException("This secret is protected with ASP.NET Core Data Protection, but no Data Protection provider is available.");
        }

        try
        {
            return _secretProtector.Unprotect(protectedSecret[3..]);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("This secret could not be unprotected. Ensure the ASP.NET Core Data Protection key ring is persisted and available to this application instance.", ex);
        }
    }

    /// <summary>
    /// Protects a complete refresh-token response for the retry grace window.
    /// Unlike general secret protection, this operation fails closed when Data
    /// Protection is unavailable and embeds a cryptographic expiry in the
    /// protected payload.
    /// </summary>
    internal string ProtectRefreshTokenResponse(string responseJson, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            throw new ArgumentException("A refresh token response is required.", nameof(responseJson));
        }

        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "The refresh token response lifetime must be positive.");
        }

        if (_refreshTokenResponseProtector == null)
        {
            throw new InvalidOperationException(
                "Refresh token grace-window responses require ASP.NET Core Data Protection. " +
                "Configure a persisted, shared Data Protection key ring or disable the refresh token grace window.");
        }

        return $"dpt:{_refreshTokenResponseProtector.Protect(responseJson, lifetime)}";
    }

    /// <summary>
    /// Unprotects a grace-window response. Plaintext and general-purpose
    /// Data Protection payloads are deliberately rejected so a database row
    /// can never opt out of the purpose-bound, time-limited protection.
    /// </summary>
    internal string UnprotectRefreshTokenResponse(string protectedResponse)
    {
        if (string.IsNullOrWhiteSpace(protectedResponse)
            || !protectedResponse.StartsWith("dpt:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The cached refresh token response is not securely protected.");
        }

        if (_refreshTokenResponseProtector == null)
        {
            throw new InvalidOperationException(
                "This refresh token response is protected with ASP.NET Core Data Protection, " +
                "but no Data Protection provider is available.");
        }

        try
        {
            return _refreshTokenResponseProtector.Unprotect(protectedResponse[4..], out _);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "The cached refresh token response is invalid or its retry window has expired.", ex);
        }
    }

    public bool VerifyPassword(string hashedPassword, string password)
    {
        var result = _passwordHasher.VerifyHashedPassword(new object(), hashedPassword, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }

    public string GenerateId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 1 + 24, prefix.Length + 1 + 32)];

    public string GenerateOpaqueToken(int numBytes = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(numBytes);
        return Base64UrlEncoder.Encode(bytes);
    }

    public string HashToken(string rawToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(hash);
    }

    public string CreatePkceCodeChallenge(string codeVerifier)
    {
        if (!IsValidPkceCodeVerifier(codeVerifier))
        {
            throw new InvalidOperationException(
                "PKCE code verifier must be 43 to 128 RFC 7636 unreserved characters.");
        }

        return Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
    }

    internal bool IsValidPkceCodeVerifier(string? codeVerifier)
        => codeVerifier is { Length: >= 43 and <= 128 }
            && codeVerifier.All(IsPkceUnreservedCharacter);

    internal bool IsValidS256PkceCodeChallenge(string? codeChallenge)
        // SHA-256 always produces 32 bytes, whose unpadded base64url
        // representation is exactly 43 characters.
        => codeChallenge is { Length: 43 }
            && codeChallenge.All(IsBase64UrlCharacter);

    public bool VerifyPkceCodeVerifier(string codeVerifier, string codeChallenge, string codeChallengeMethod)
    {
        if (!string.Equals(codeChallengeMethod, "S256", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only S256 PKCE code challenges are supported.");
        }

        if (!IsValidPkceCodeVerifier(codeVerifier)
            || !IsValidS256PkceCodeChallenge(codeChallenge))
        {
            return false;
        }

        var computed = CreatePkceCodeChallenge(codeVerifier);
        return string.Equals(computed, codeChallenge, StringComparison.Ordinal);
    }

    private static bool IsPkceUnreservedCharacter(char value)
        => IsAsciiAlphaNumeric(value) || value is '-' or '.' or '_' or '~';

    private static bool IsBase64UrlCharacter(char value)
        => IsAsciiAlphaNumeric(value) || value is '-' or '_';

    private static bool IsAsciiAlphaNumeric(char value)
        => value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';

    public async Task<string> CreateTemporaryTokenAsync(
        string purpose,
        string? userId,
        string? clientApplicationId,
        string? organizationId,
        object? payload,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        var rawToken = GenerateOpaqueToken();
        var now = DateTime.UtcNow;
        var token = new SqlOSTemporaryToken
        {
            Id = GenerateId("tmp"),
            Purpose = purpose,
            TokenHash = HashToken(rawToken),
            UserId = userId,
            ClientApplicationId = clientApplicationId,
            OrganizationId = organizationId,
            PayloadJson = payload != null ? JsonSerializer.Serialize(payload) : null,
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime ?? _options.TemporaryTokenLifetime)
        };
        _context.Set<SqlOSTemporaryToken>().Add(token);
        await _context.SaveChangesAsync(cancellationToken);
        return rawToken;
    }

    public async Task<SqlOSTemporaryToken?> FindTemporaryTokenAsync(
        string purpose,
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        var hash = HashToken(rawToken);
        var now = DateTime.UtcNow;
        return await _context.Set<SqlOSTemporaryToken>()
            .FirstOrDefaultAsync(x => x.Purpose == purpose && x.TokenHash == hash && x.ConsumedAt == null && x.ExpiresAt >= now, cancellationToken);
    }

    public async Task<SqlOSTemporaryToken?> ConsumeTemporaryTokenAsync(
        string purpose,
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        var token = await FindTemporaryTokenAsync(purpose, rawToken, cancellationToken);
        if (token == null)
        {
            return null;
        }

        token.ConsumedAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                entry.State = EntityState.Detached;
            }
            return null;
        }

        return token;
    }

    public T? DeserializePayload<T>(SqlOSTemporaryToken token)
        => string.IsNullOrWhiteSpace(token.PayloadJson) ? default : JsonSerializer.Deserialize<T>(token.PayloadJson);

    public Task<SqlOSSigningKey> EnsureActiveSigningKeyAsync(CancellationToken cancellationToken = default)
        => EnsureActiveSigningKeyCoreAsync(validateExistingCustody: true, cancellationToken);

    private async Task<SqlOSSigningKey> EnsureActiveSigningKeyCoreAsync(
        bool validateExistingCustody,
        CancellationToken cancellationToken)
    {
        var observedKeys = await LoadAndValidateSigningKeysAsync(cancellationToken);
        var observedActiveKeys = observedKeys.Where(static key => key.IsActive).ToList();
        if (observedActiveKeys.Count > 1)
        {
            throw new InvalidOperationException("SqlOS found multiple active signing keys. Refusing to issue tokens until signing-key state is repaired.");
        }

        if (observedActiveKeys.Count == 1)
        {
            if (validateExistingCustody)
            {
                await ValidateCustodyCanSignAsync(observedActiveKeys[0], cancellationToken);
            }

            return observedActiveKeys[0];
        }

        return await CreateActiveSigningKeyUnderLockAsync(validateExistingCustody, cancellationToken);
    }

    private async Task<SqlOSSigningKey> CreateActiveSigningKeyUnderLockAsync(
        bool validateExistingCustody,
        CancellationToken cancellationToken)
    {
        var createdKeys = new List<SqlOSSigningKey>();
        SqlOSSigningKey result;
        try
        {
            result = await ExecuteSigningKeyTransactionAsync(async attemptCancellationToken =>
            {
                var keys = await LoadAndValidateSigningKeysAsync(attemptCancellationToken);
                var activeKeys = keys.Where(static key => key.IsActive).ToList();
                if (activeKeys.Count > 1)
                {
                    throw new InvalidOperationException("SqlOS found multiple active signing keys. Refusing to issue tokens until signing-key state is repaired.");
                }

                if (activeKeys.Count == 1)
                {
                    if (validateExistingCustody)
                    {
                        await ValidateCustodyCanSignAsync(activeKeys[0], attemptCancellationToken);
                    }

                    return activeKeys[0];
                }

                var createdKey = await CreateSigningKeyAsync(keys, attemptCancellationToken);
                createdKeys.Add(createdKey);
                _context.Set<SqlOSSigningKey>().Add(createdKey);
                await _context.SaveChangesAsync(attemptCancellationToken);
                return createdKey;
            }, cancellationToken);
        }
        catch
        {
            await CleanupUncommittedCreatedKeysAsync(createdKeys, CancellationToken.None);
            throw;
        }

        await CleanupUncommittedCreatedKeysAsync(createdKeys, CancellationToken.None);
        _validationSigningKeyCache.InvalidateAll();
        return result;
    }

    public async Task<List<SqlOSSigningKey>> GetValidationSigningKeysAsync(CancellationToken cancellationToken = default)
    {
        var graceWindow = await ResolveSigningKeyGraceWindowAsync(cancellationToken);
        var cacheKey = $"{_options.Schema}|{_options.Issuer}|{graceWindow.Ticks}";
        return await _validationSigningKeyCache.GetOrCreateAsync(
            cacheKey,
            _options.AccessTokenValidationSigningKeyCacheTtl,
            ct => LoadValidationSigningKeysAsync(graceWindow, ct),
            cancellationToken);
    }

    private async Task<List<SqlOSSigningKey>> RefreshValidationSigningKeysIfMissingAsync(
        string kid,
        CancellationToken cancellationToken)
    {
        var graceWindow = await ResolveSigningKeyGraceWindowAsync(cancellationToken);
        var cacheKey = $"{_options.Schema}|{_options.Issuer}|{graceWindow.Ticks}";
        return await _validationSigningKeyCache.RefreshIfMissingAsync(
            cacheKey,
            kid,
            _options.AccessTokenValidationSigningKeyCacheTtl,
            ct => LoadValidationSigningKeysAsync(graceWindow, ct),
            cancellationToken);
    }

    public async Task<SqlOSSigningKey> RotateSigningKeyAsync(CancellationToken cancellationToken = default)
    {
        var createdKeys = new List<SqlOSSigningKey>();
        SqlOSSigningKey result;
        try
        {
            result = await ExecuteSigningKeyTransactionAsync(async attemptCancellationToken =>
            {
                var keys = await LoadAndValidateSigningKeysAsync(attemptCancellationToken);
                var activeKeys = keys.Where(static key => key.IsActive).ToList();
                if (activeKeys.Count > 1)
                {
                    throw new InvalidOperationException("SqlOS found multiple active signing keys. Refusing rotation until signing-key state is repaired.");
                }

                if (activeKeys.Count == 1)
                {
                    await ValidateCustodyCanSignAsync(activeKeys[0], attemptCancellationToken);
                }

                var newKey = await CreateSigningKeyAsync(keys, attemptCancellationToken);
                createdKeys.Add(newKey);
                if (activeKeys.Count == 1)
                {
                    activeKeys[0].IsActive = false;
                    activeKeys[0].RetiredAt = DateTime.UtcNow;
                }

                _context.Set<SqlOSSigningKey>().Add(newKey);
                await _context.SaveChangesAsync(attemptCancellationToken);
                return newKey;
            }, cancellationToken);
        }
        catch
        {
            await CleanupUncommittedCreatedKeysAsync(createdKeys, CancellationToken.None);
            throw;
        }

        await CleanupUncommittedCreatedKeysAsync(createdKeys, CancellationToken.None);
        _validationSigningKeyCache.InvalidateAll();
        return result;
    }

    public async Task<bool> ShouldRotateSigningKeyAsync(TimeSpan rotationInterval, CancellationToken cancellationToken = default)
    {
        var keys = await LoadAndValidateSigningKeysAsync(cancellationToken);
        var activeKeys = keys.Where(static key => key.IsActive).ToList();
        if (activeKeys.Count > 1)
        {
            throw new InvalidOperationException("SqlOS found multiple active signing keys. Refusing rotation checks until signing-key state is repaired.");
        }

        var activeKey = activeKeys.SingleOrDefault();
        if (activeKey == null)
            return true;
        return DateTime.UtcNow - activeKey.ActivatedAt >= rotationInterval;
    }

    public async Task<int> CleanupRetiredSigningKeysAsync(TimeSpan retiredCleanupWindow, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.Add(-retiredCleanupWindow);
        var keys = await LoadAndValidateSigningKeysAsync(cancellationToken);
        var expired = keys
            .Where(key => !key.IsActive && key.RetiredAt < cutoff)
            .ToList();
        if (expired.Count == 0)
            return 0;

        foreach (var key in expired)
        {
            await _signingKeyCustody.DeleteKeyAsync(ToDescriptor(key), cancellationToken);
        }

        _context.Set<SqlOSSigningKey>().RemoveRange(expired);
        await _context.SaveChangesAsync(cancellationToken);
        _validationSigningKeyCache.InvalidateAll();
        return expired.Count;
    }

    public async Task<List<SqlOSSigningKey>> ListSigningKeysAsync(CancellationToken cancellationToken = default)
    {
        var keys = await LoadAndValidateSigningKeysAsync(cancellationToken);
        return keys.OrderByDescending(key => key.ActivatedAt).ToList();
    }

    public async Task<string> CreateAccessTokenAsync(
        SqlOSUser user,
        SqlOSSession session,
        SqlOSClientApplication client,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        var key = await EnsureActiveSigningKeyCoreAsync(validateExistingCustody: false, cancellationToken);
        var now = DateTime.UtcNow;
        var authenticationMethods = SqlOSMfaPolicyService
            .SplitAuthenticationMethods(session.AuthenticationMethod ?? "password")
            .ToArray();
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Iss] = _options.Issuer,
            [JwtRegisteredClaimNames.Sub] = user.Id,
            [JwtRegisteredClaimNames.Aud] = string.IsNullOrWhiteSpace(session.EffectiveAudience)
                ? client.Audience
                : session.EffectiveAudience,
            [JwtRegisteredClaimNames.Nbf] = EpochTime.GetIntDate(now),
            [JwtRegisteredClaimNames.Iat] = EpochTime.GetIntDate(now),
            [JwtRegisteredClaimNames.Exp] = EpochTime.GetIntDate(now.Add(_options.AccessTokenLifetime)),
            ["sid"] = session.Id,
            ["client_id"] = client.ClientId,
            ["amr"] = authenticationMethods
        };

        if (!string.IsNullOrWhiteSpace(user.DefaultEmail))
        {
            payload[JwtRegisteredClaimNames.Email] = user.DefaultEmail;
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            payload["org_id"] = organizationId;
        }

        var header = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [JwtHeaderParameterNames.Alg] = SecurityAlgorithms.RsaSha256,
            [JwtHeaderParameterNames.Typ] = "JWT",
            [JwtHeaderParameterNames.Kid] = key.Kid
        };
        var encodedHeader = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(header));
        var encodedPayload = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{encodedHeader}.{encodedPayload}";
        var signature = await _signingKeyCustody.SignAsync(
            ToDescriptor(key),
            Encoding.ASCII.GetBytes(signingInput),
            cancellationToken);
        VerifySignature(key, Encoding.ASCII.GetBytes(signingInput), signature);
        return $"{signingInput}.{Base64UrlEncoder.Encode(signature)}";
    }

    public async Task<string> CreateServiceAccessTokenAsync(
        string subjectId,
        SqlOSClientApplication client,
        string audience,
        IReadOnlyList<string> scopes,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        var key = await EnsureActiveSigningKeyCoreAsync(validateExistingCustody: false, cancellationToken);
        var now = DateTime.UtcNow;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Iss] = _options.Issuer,
            [JwtRegisteredClaimNames.Sub] = subjectId,
            [JwtRegisteredClaimNames.Aud] = audience,
            [JwtRegisteredClaimNames.Nbf] = EpochTime.GetIntDate(now),
            [JwtRegisteredClaimNames.Iat] = EpochTime.GetIntDate(now),
            [JwtRegisteredClaimNames.Exp] = EpochTime.GetIntDate(now.Add(_options.AccessTokenLifetime)),
            ["client_id"] = client.ClientId,
            ["azp"] = client.ClientId,
            ["token_kind"] = "service",
            ["scope"] = string.Join(' ', scopes)
        };
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            payload["org_id"] = organizationId;
        }

        var header = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [JwtHeaderParameterNames.Alg] = SecurityAlgorithms.RsaSha256,
            [JwtHeaderParameterNames.Typ] = "JWT",
            [JwtHeaderParameterNames.Kid] = key.Kid
        };
        var encodedHeader = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(header));
        var encodedPayload = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{encodedHeader}.{encodedPayload}";
        var signature = await _signingKeyCustody.SignAsync(
            ToDescriptor(key),
            Encoding.ASCII.GetBytes(signingInput),
            cancellationToken);
        VerifySignature(key, Encoding.ASCII.GetBytes(signingInput), signature);
        return $"{signingInput}.{Base64UrlEncoder.Encode(signature)}";
    }

    public async Task<SqlOSValidatedToken?> ValidateAccessTokenAsync(
        string rawToken,
        string expectedAudience,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedAudience))
        {
            throw new ArgumentException("An expected audience is required when validating an access token for a resource server.", nameof(expectedAudience));
        }

        return await ValidateAccessTokenCoreAsync(rawToken, expectedAudience.Trim(), validateAudience: true, cancellationToken);
    }

    internal async Task<SqlOSValidatedToken?> ValidateAccessTokenWithoutAudienceForIntrospectionOnlyAsync(
        string rawToken,
        CancellationToken cancellationToken = default)
        => await ValidateAccessTokenCoreAsync(rawToken, expectedAudience: null, validateAudience: false, cancellationToken);

    private async Task<SqlOSValidatedToken?> ValidateAccessTokenCoreAsync(
        string rawToken,
        string? expectedAudience,
        bool validateAudience,
        CancellationToken cancellationToken)
    {
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        };

        try
        {
            var jwt = handler.ReadJwtToken(rawToken);
            if (!string.Equals(jwt.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal)
                || !string.Equals(jwt.Header.Typ, "JWT", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(jwt.Header.Kid))
            {
                return null;
            }

            var keys = await GetValidationSigningKeysAsync(cancellationToken: cancellationToken);
            var matchingKeys = keys
                .Where(key => string.Equals(key.Kid, jwt.Header.Kid, StringComparison.Ordinal)
                    && string.Equals(key.Algorithm, jwt.Header.Alg, StringComparison.Ordinal))
                .ToList();
            if (matchingKeys.Count == 0
                && _options.AccessTokenValidationSigningKeyCacheTtl > TimeSpan.Zero)
            {
                keys = await RefreshValidationSigningKeysIfMissingAsync(jwt.Header.Kid, cancellationToken);
                matchingKeys = keys
                    .Where(key => string.Equals(key.Kid, jwt.Header.Kid, StringComparison.Ordinal)
                        && string.Equals(key.Algorithm, jwt.Header.Alg, StringComparison.Ordinal))
                    .ToList();
            }

            if (matchingKeys.Count != 1)
            {
                return null;
            }

            var principal = handler.ValidateToken(rawToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.Issuer,
                ValidateAudience = validateAudience,
                ValidAudience = validateAudience ? expectedAudience : null,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = ToSecurityKey(matchingKeys[0]),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidTypes = ["JWT"],
                RequireSignedTokens = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            }, out _);

            var sessionId = principal.FindFirstValue("sid");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                if (!string.Equals(principal.FindFirstValue("token_kind"), "service", StringComparison.Ordinal))
                {
                    return null;
                }

                var serviceSubjectId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
                var serviceClientId = principal.FindFirstValue("client_id");
                if (string.IsNullOrWhiteSpace(serviceSubjectId) || string.IsNullOrWhiteSpace(serviceClientId))
                {
                    return null;
                }

                var serviceClient = await _context.Set<SqlOSClientApplication>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ClientId == serviceClientId && x.IsActive && x.DisabledAt == null, cancellationToken);
                if (serviceClient == null
                    || !SqlOSAdminService.DeserializeJsonList(serviceClient.GrantTypesJson)
                        .Contains(SqlOSOAuthGrantTypes.ClientCredentials, StringComparer.Ordinal))
                {
                    return null;
                }

                if (!string.Equals(serviceSubjectId, serviceClientId, StringComparison.Ordinal))
                {
                    var serviceNow = DateTime.UtcNow;
                    var serviceAccount = await _context.Set<SqlOS.Fga.Models.SqlOSFgaServiceAccount>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.SubjectId == serviceSubjectId
                            && x.ClientId == serviceClientId
                            && (x.ExpiresAt == null || x.ExpiresAt > serviceNow), cancellationToken);
                    var serviceSubject = await _context.Set<SqlOS.Fga.Models.SqlOSFgaSubject>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == serviceSubjectId && x.SubjectTypeId == "service_account", cancellationToken);
                    if (serviceAccount == null || serviceSubject == null)
                    {
                        return null;
                    }
                }

                return new SqlOSValidatedToken(
                    principal,
                    string.Empty,
                    null,
                    principal.FindFirstValue("org_id"),
                    serviceClientId,
                    principal.FindFirstValue("aud"));
            }

            var now = DateTime.UtcNow;
            var session = await _context.Set<SqlOSSession>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
            if (session == null || session.RevokedAt != null || session.AbsoluteExpiresAt <= now)
            {
                return null;
            }

            var userId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (string.IsNullOrWhiteSpace(userId)
                || !string.Equals(userId, session.UserId, StringComparison.Ordinal))
            {
                return null;
            }

            var organizationId = principal.FindFirstValue("org_id");
            var lifecycle = await SqlOSAuthLifecyclePolicy.EvaluateAsync(
                _context,
                session.UserId,
                organizationId,
                cancellationToken);
            if (session.IdleExpiresAt <= now || !lifecycle.IsActive)
            {
                var denial = session.IdleExpiresAt <= now
                    ? SqlOSAuthLifecycleDecision.Denied("session_idle_expired")
                    : lifecycle;
                SqlOSAuthLifecyclePolicy.AddDeniedAudit(
                    _context,
                    GenerateId("aud"),
                    "access_token_validation",
                    denial,
                    session.UserId,
                    organizationId,
                    session.Id);

                if (session.IdleExpiresAt <= now)
                {
                    var trackedSession = _context.Set<SqlOSSession>().Local.FirstOrDefault(x => x.Id == session.Id)
                        ?? await _context.Set<SqlOSSession>().FirstAsync(x => x.Id == session.Id, cancellationToken);
                    await SqlOSAuthLifecyclePolicy.RevokeSessionAsync(
                        _context,
                        trackedSession,
                        "session_idle_expired",
                        now,
                        cancellationToken);
                }
                else
                {
                    await SqlOSAuthLifecyclePolicy.RevokeForDenialAsync(
                        _context,
                        session.UserId,
                        organizationId,
                        lifecycle,
                        now,
                        cancellationToken);
                }

                await _context.SaveChangesAsync(cancellationToken);
                return null;
            }

            var idleTimeoutMinutes = await _context.Set<SqlOSSettings>()
                .AsNoTracking()
                .Where(x => x.Id == "default")
                .Select(x => (int?)x.SessionIdleTimeoutMinutes)
                .FirstOrDefaultAsync(cancellationToken);
            var idleTimeout = idleTimeoutMinutes is > 0
                ? TimeSpan.FromMinutes(idleTimeoutMinutes.Value)
                : _options.SessionIdleTimeout;
            var nextIdleExpiry = now.Add(idleTimeout);
            if (nextIdleExpiry > session.AbsoluteExpiresAt)
            {
                nextIdleExpiry = session.AbsoluteExpiresAt;
            }

            var shouldSaveActivity = false;
            if (ShouldPersistValidationLastSeen(session.LastSeenAt, now))
            {
                var sessionToUpdate = _context.Set<SqlOSSession>().Local.FirstOrDefault(x => x.Id == session.Id)
                    ?? await _context.Set<SqlOSSession>().FirstAsync(x => x.Id == session.Id, cancellationToken);
                sessionToUpdate.LastSeenAt = now;
                sessionToUpdate.IdleExpiresAt = nextIdleExpiry;
                shouldSaveActivity = true;
            }
            if (!string.IsNullOrWhiteSpace(session.ClientApplicationId))
            {
                var client = await _context.Set<SqlOSClientApplication>()
                    .FirstOrDefaultAsync(x => x.Id == session.ClientApplicationId, cancellationToken);
                if (client != null && ShouldPersistValidationLastSeen(client.LastSeenAt, now))
                {
                    client.LastSeenAt = now;
                    shouldSaveActivity = true;
                }
            }
            if (shouldSaveActivity)
            {
                await _context.SaveChangesAsync(cancellationToken);
            }

            return new SqlOSValidatedToken(
                principal,
                session.Id,
                userId,
                organizationId,
                principal.FindFirstValue("client_id"),
                principal.FindFirstValue("aud"));
        }
        catch
        {
            return null;
        }
    }

    public object GetJwksDocument(IEnumerable<SqlOSSigningKey> keys)
    {
        var jwks = keys.Select(key =>
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(key.PublicKeyPem);
            var parameters = rsa.ExportParameters(false);
            return new
            {
                kty = "RSA",
                use = "sig",
                alg = key.Algorithm,
                kid = key.Kid,
                n = Base64UrlEncoder.Encode(parameters.Modulus),
                e = Base64UrlEncoder.Encode(parameters.Exponent)
            };
        });

        return new { keys = jwks };
    }

    private static SecurityKey ToSecurityKey(SqlOSSigningKey key)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.PublicKeyPem);
        var parameters = rsa.ExportParameters(false);
        return new RsaSecurityKey(parameters) { KeyId = key.Kid };
    }

    private bool ShouldPersistValidationLastSeen(DateTime? currentLastSeenAt, DateTime now)
    {
        var debounceInterval = _options.AccessTokenValidationLastSeenDebounceInterval;
        return debounceInterval <= TimeSpan.Zero
            || currentLastSeenAt == null
            || currentLastSeenAt.Value.Add(debounceInterval) <= now;
    }

    private async Task<SqlOSSigningKey> CreateSigningKeyAsync(
        IReadOnlyCollection<SqlOSSigningKey> existingKeys,
        CancellationToken cancellationToken)
    {
        var kid = GenerateOpaqueToken(16);
        var created = await _signingKeyCustody.CreateKeyAsync(
            kid,
            SecurityAlgorithms.RsaSha256,
            cancellationToken);
        var key = new SqlOSSigningKey
        {
            Id = GenerateId("key"),
            Kid = kid,
            Algorithm = created.Algorithm,
            PublicKeyPem = created.PublicKeyPem,
            CustodyProvider = _signingKeyCustody.ProviderId,
            KeyReference = created.KeyReference,
            ActivatedAt = DateTime.UtcNow,
            IsActive = true
        };
        var reusedReference = existingKeys.FirstOrDefault(existing =>
            string.Equals(existing.KeyReference, key.KeyReference, StringComparison.Ordinal));
        if (reusedReference != null)
        {
            throw BuildReusedSigningKeyException(key, reusedReference);
        }

        try
        {
            ValidateStoredSigningKeyRow(key);
            await ValidateCustodyCanSignAsync(key, cancellationToken);
        }
        catch
        {
            await TryDeleteCreatedKeyAsync(key);
            throw;
        }

        var reusedPublicKey = existingKeys.FirstOrDefault(existing =>
            PublicKeysMatch(existing.PublicKeyPem, key.PublicKeyPem));
        if (reusedPublicKey != null)
        {
            throw BuildReusedSigningKeyException(key, reusedPublicKey);
        }

        return key;
    }

    private void ValidateStoredSigningKeyRows(IEnumerable<SqlOSSigningKey> keys)
    {
        var rows = keys.ToList();
        foreach (var key in rows)
        {
            ValidateStoredSigningKeyRow(key);
        }

        for (var firstIndex = 0; firstIndex < rows.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < rows.Count; secondIndex++)
            {
                var first = rows[firstIndex];
                var second = rows[secondIndex];
                if (string.Equals(first.KeyReference, second.KeyReference, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Signing keys '{first.Kid}' and '{second.Kid}' share a custody reference. " +
                        "Refusing ambiguous provider ownership.");
                }

                if (PublicKeysMatch(first.PublicKeyPem, second.PublicKeyPem))
                {
                    throw new InvalidOperationException(
                        $"Signing keys '{first.Kid}' and '{second.Kid}' share the same canonical RSA public key. " +
                        "Refusing ambiguous provider ownership.");
                }
            }
        }
    }

    private void ValidateStoredSigningKeyRow(SqlOSSigningKey key)
    {
        if (ContainsPrivateKeyPem(key.KeyReference) || ContainsPrivateKeyPem(key.PublicKeyPem))
        {
            throw new InvalidOperationException(
                $"Signing key '{key.Kid}' contains plaintext private key material in the application database. " +
                "SqlOS refuses to start or publish this key. Remove legacy signing-key rows and provision keys through configured custody.");
        }

        if (string.IsNullOrWhiteSpace(key.Kid)
            || string.IsNullOrWhiteSpace(key.PublicKeyPem)
            || string.IsNullOrWhiteSpace(key.KeyReference)
            || string.IsNullOrWhiteSpace(key.CustodyProvider))
        {
            throw new InvalidOperationException("A SqlOS signing-key row has incomplete custody metadata.");
        }

        if (key.IsActive && key.RetiredAt != null)
        {
            throw new InvalidOperationException(
                $"Signing key '{key.Kid}' is active but has a retirement timestamp. Refusing inconsistent signing-key lifecycle state.");
        }

        if (!key.IsActive && key.RetiredAt == null)
        {
            throw new InvalidOperationException(
                $"Signing key '{key.Kid}' is inactive but has no retirement timestamp. Refusing inconsistent signing-key lifecycle state.");
        }

        if (!string.Equals(key.Algorithm, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Signing key '{key.Kid}' uses unsupported algorithm '{key.Algorithm}'. SqlOS requires RS256.");
        }

        if (!string.Equals(key.CustodyProvider, _signingKeyCustody.ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Signing key '{key.Kid}' is bound to custody provider '{key.CustodyProvider}', but '{_signingKeyCustody.ProviderId}' is configured.");
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(key.PublicKeyPem);
            if (rsa.KeySize < 2048)
            {
                throw new InvalidOperationException($"Signing key '{key.Kid}' is smaller than 2048 bits.");
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException($"Signing key '{key.Kid}' does not contain a valid RSA public key.", ex);
        }
    }

    private async Task ValidateCustodyCanSignAsync(SqlOSSigningKey key, CancellationToken cancellationToken)
    {
        var challenge = RandomNumberGenerator.GetBytes(32);
        var signature = await _signingKeyCustody.SignAsync(ToDescriptor(key), challenge, cancellationToken);
        VerifySignature(key, challenge, signature);
    }

    private static void VerifySignature(SqlOSSigningKey key, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.PublicKeyPem);
        if (!rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            throw new InvalidOperationException(
                $"Signing custody provider '{key.CustodyProvider}' produced a signature that does not match key '{key.Kid}'.");
        }
    }

    private static bool PublicKeysMatch(string firstPublicKeyPem, string secondPublicKeyPem)
    {
        using var first = RSA.Create();
        first.ImportFromPem(firstPublicKeyPem);
        using var second = RSA.Create();
        second.ImportFromPem(secondPublicKeyPem);
        var firstFingerprint = SHA256.HashData(first.ExportSubjectPublicKeyInfo());
        var secondFingerprint = SHA256.HashData(second.ExportSubjectPublicKeyInfo());
        return CryptographicOperations.FixedTimeEquals(firstFingerprint, secondFingerprint);
    }

    private static InvalidOperationException BuildReusedSigningKeyException(
        SqlOSSigningKey createdKey,
        SqlOSSigningKey existingKey)
        => new(
            $"Signing custody provider '{createdKey.CustodyProvider}' reused existing key material from '{existingKey.Kid}' while creating '{createdKey.Kid}'. " +
            "Refusing rotation without deleting the ambiguous provider reference.");

    private static SqlOSSigningKeyDescriptor ToDescriptor(SqlOSSigningKey key)
        => new(key.Kid, key.Algorithm, key.PublicKeyPem, key.KeyReference, key.CustodyProvider);

    private static bool ContainsPrivateKeyPem(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && (value.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)
                || value.Contains("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal)
                || value.Contains("-----BEGIN EC PRIVATE KEY-----", StringComparison.Ordinal));

    private async Task<List<SqlOSSigningKey>> LoadAndValidateSigningKeysAsync(CancellationToken cancellationToken)
    {
        var keys = await _context.Set<SqlOSSigningKey>().ToListAsync(cancellationToken);
        ValidateStoredSigningKeyRows(keys);
        return keys;
    }

    private async Task<List<SqlOSSigningKey>> LoadValidationSigningKeysAsync(
        TimeSpan graceWindow,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.Subtract(graceWindow);
        var keys = await _context.Set<SqlOSSigningKey>()
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        ValidateStoredSigningKeyRows(keys);
        return keys
            .Where(key => key.IsActive || key.RetiredAt >= cutoff)
            .ToList();
    }

    private async Task<TimeSpan> ResolveSigningKeyGraceWindowAsync(CancellationToken cancellationToken)
    {
        var persistedGraceDays = await _context.Set<SqlOSSettings>()
            .AsNoTracking()
            .Where(settings => settings.Id == "default")
            .Select(settings => (int?)settings.SigningKeyGraceWindowDays)
            .SingleOrDefaultAsync(cancellationToken);
        var graceDays = persistedGraceDays ?? _options.DefaultSigningKeyGraceWindowDays;
        if (graceDays <= 0)
        {
            throw new InvalidOperationException(
                "SqlOS signing-key grace configuration is invalid. SigningKeyGraceWindowDays must be positive.");
        }

        try
        {
            return TimeSpan.FromDays(graceDays);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                "SqlOS signing-key grace configuration exceeds the supported duration.",
                ex);
        }
    }

    private async Task<SqlOSSigningKey> ExecuteSigningKeyTransactionAsync(
        Func<CancellationToken, Task<SqlOSSigningKey>> operation,
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational())
        {
            return await operation(cancellationToken);
        }

        SqlOSSigningKey? attemptedResult = null;
        var attemptStarted = false;
        var strategy = _context.Database.CreateExecutionStrategy();

        async Task<SqlOSSigningKey> ExecuteAttemptAsync(CancellationToken attemptCancellationToken)
        {
            if (attemptStarted)
            {
                DetachTrackedSigningKeys();
            }

            attemptStarted = true;
            await AcquireSigningKeyLockAsync(attemptCancellationToken);
            attemptedResult = await operation(attemptCancellationToken);
            return attemptedResult;
        }

        async Task<bool> VerifySucceededAsync(CancellationToken verifyCancellationToken)
        {
            DetachTrackedSigningKeys();
            return attemptedResult != null
                && await HasSingleActiveSigningKeyAsync(attemptedResult.Id, verifyCancellationToken);
        }

        return await strategy.ExecuteInTransactionAsync(
            ExecuteAttemptAsync,
            VerifySucceededAsync,
            IsolationLevel.Serializable,
            cancellationToken);
    }

    private async Task AcquireSigningKeyLockAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(
            _context.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.Ordinal))
        {
            return;
        }

        await _context.Database.ExecuteSqlRawAsync(
            """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = 'SqlOS.SigningKeys',
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 30000;
            IF @result < 0
                THROW 51000, 'SqlOS could not acquire the signing-key custody lock.', 1;
            """,
            cancellationToken);
    }

    private async Task<bool> HasSingleActiveSigningKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        var activeIds = await _context.Set<SqlOSSigningKey>()
            .AsNoTracking()
            .Where(static key => key.IsActive)
            .Select(static key => key.Id)
            .ToListAsync(cancellationToken);
        return activeIds.Count == 1 && string.Equals(activeIds[0], keyId, StringComparison.Ordinal);
    }

    private async Task CleanupUncommittedCreatedKeysAsync(
        IReadOnlyCollection<SqlOSSigningKey> createdKeys,
        CancellationToken cancellationToken)
    {
        if (createdKeys.Count == 0)
        {
            return;
        }

        HashSet<string> persistedIds;
        try
        {
            var createdIds = createdKeys.Select(static key => key.Id).ToList();
            persistedIds = (await _context.Set<SqlOSSigningKey>()
                    .AsNoTracking()
                    .Where(key => createdIds.Contains(key.Id))
                    .Select(static key => key.Id)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch
        {
            // A failed verification cannot safely distinguish an orphan from a key whose
            // commit response was lost. Preserve custody material rather than deleting a
            // key that may now be active in the database.
            return;
        }

        foreach (var createdKey in createdKeys.Where(key => !persistedIds.Contains(key.Id)))
        {
            await TryDeleteCreatedKeyAsync(createdKey);
        }
    }

    private void DetachTrackedSigningKeys()
    {
        if (_context is not DbContext dbContext)
        {
            return;
        }

        foreach (var entry in dbContext.ChangeTracker.Entries<SqlOSSigningKey>().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task TryDeleteCreatedKeyAsync(SqlOSSigningKey key)
    {
        try
        {
            await _signingKeyCustody.DeleteKeyAsync(ToDescriptor(key), CancellationToken.None);
        }
        catch
        {
            // Preserve the original persistence/custody exception. External providers should surface
            // orphaned-key cleanup through their own operational telemetry.
        }
    }
}
