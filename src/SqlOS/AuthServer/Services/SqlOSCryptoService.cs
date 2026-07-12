using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
    private readonly PasswordHasher<object> _passwordHasher = new();
    private readonly IDataProtector? _secretProtector;

    public SqlOSCryptoService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        IDataProtectionProvider? dataProtectionProvider = null)
    {
        _context = context;
        _options = options.Value;
        _secretProtector = dataProtectionProvider?.CreateProtector("SqlOS.AuthServer.OidcSecrets");
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

    public bool IsValidPkceCodeVerifier(string? codeVerifier)
        => codeVerifier is { Length: >= 43 and <= 128 }
            && codeVerifier.All(IsPkceUnreservedCharacter);

    public bool IsValidS256PkceCodeChallenge(string? codeChallenge)
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
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }

        return token;
    }

    public T? DeserializePayload<T>(SqlOSTemporaryToken token)
        => string.IsNullOrWhiteSpace(token.PayloadJson) ? default : JsonSerializer.Deserialize<T>(token.PayloadJson);

    public async Task<SqlOSSigningKey> EnsureActiveSigningKeyAsync(CancellationToken cancellationToken = default)
    {
        var activeKey = await _context.Set<SqlOSSigningKey>()
            .FirstOrDefaultAsync(x => x.IsActive, cancellationToken);
        if (activeKey != null)
        {
            await ProtectSigningKeyAtRestIfNeededAsync(activeKey, cancellationToken);
            return activeKey;
        }

        using var rsa = RSA.Create(2048);
        activeKey = new SqlOSSigningKey
        {
            Id = GenerateId("key"),
            Kid = GenerateOpaqueToken(16),
            PublicKeyPem = rsa.ExportRSAPublicKeyPem(),
            PrivateKeyPem = ProtectSigningPrivateKey(rsa.ExportPkcs8PrivateKeyPem()),
            ActivatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _context.Set<SqlOSSigningKey>().Add(activeKey);
        await _context.SaveChangesAsync(cancellationToken);
        return activeKey;
    }

    public async Task<List<SqlOSSigningKey>> GetValidationSigningKeysAsync(TimeSpan? graceWindow = null, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.Add(-(graceWindow ?? TimeSpan.FromDays(7)));
        return await _context.Set<SqlOSSigningKey>()
            .Where(x => x.IsActive || x.RetiredAt == null || x.RetiredAt >= cutoff)
            .ToListAsync(cancellationToken);
    }

    public async Task<SqlOSSigningKey> RotateSigningKeyAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var activeKey = await _context.Set<SqlOSSigningKey>()
            .FirstOrDefaultAsync(x => x.IsActive, cancellationToken);
        if (activeKey != null)
        {
            activeKey.IsActive = false;
            activeKey.RetiredAt = now;
        }

        using var rsa = RSA.Create(2048);
        var newKey = new SqlOSSigningKey
        {
            Id = GenerateId("key"),
            Kid = GenerateOpaqueToken(16),
            PublicKeyPem = rsa.ExportRSAPublicKeyPem(),
            PrivateKeyPem = ProtectSigningPrivateKey(rsa.ExportPkcs8PrivateKeyPem()),
            ActivatedAt = now,
            IsActive = true
        };
        _context.Set<SqlOSSigningKey>().Add(newKey);
        await _context.SaveChangesAsync(cancellationToken);
        return newKey;
    }

    public async Task<bool> ShouldRotateSigningKeyAsync(TimeSpan rotationInterval, CancellationToken cancellationToken = default)
    {
        var activeKey = await _context.Set<SqlOSSigningKey>()
            .FirstOrDefaultAsync(x => x.IsActive, cancellationToken);
        if (activeKey == null)
            return true;
        return DateTime.UtcNow - activeKey.ActivatedAt >= rotationInterval;
    }

    public async Task<int> CleanupRetiredSigningKeysAsync(TimeSpan retiredCleanupWindow, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.Add(-retiredCleanupWindow);
        var expired = await _context.Set<SqlOSSigningKey>()
            .Where(x => !x.IsActive && x.RetiredAt != null && x.RetiredAt < cutoff)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
            return 0;
        _context.Set<SqlOSSigningKey>().RemoveRange(expired);
        await _context.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }

    public async Task<List<SqlOSSigningKey>> ListSigningKeysAsync(CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSSigningKey>()
            .OrderByDescending(x => x.ActivatedAt)
            .ToListAsync(cancellationToken);

    public async Task<string> CreateAccessTokenAsync(
        SqlOSUser user,
        SqlOSSession session,
        SqlOSClientApplication client,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        var key = await EnsureActiveSigningKeyAsync(cancellationToken);
        var signingMaterial = await GetSigningMaterialAsync(key, cancellationToken);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(signingMaterial.PrivateKeyPem);
        var signingKey = new RsaSecurityKey(rsa.ExportParameters(true)) { KeyId = signingMaterial.Key.Kid };

        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new("sid", session.Id),
            new("client_id", client.ClientId)
        };

        foreach (var method in SqlOSMfaPolicyService.SplitAuthenticationMethods(session.AuthenticationMethod ?? "password"))
        {
            claims.Add(new Claim("amr", method));
        }

        if (!string.IsNullOrWhiteSpace(user.DefaultEmail))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.DefaultEmail));
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            claims.Add(new Claim("org_id", organizationId));
        }

        var audience = string.IsNullOrWhiteSpace(session.EffectiveAudience)
            ? client.Audience
            : session.EffectiveAudience;
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(_options.AccessTokenLifetime),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<(SqlOSSigningKey Key, string PrivateKeyPem)> GetSigningMaterialAsync(
        SqlOSSigningKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            return (key, UnprotectSecret(key.PrivateKeyPem));
        }
        catch (InvalidOperationException) when (key.PrivateKeyPem.StartsWith("dp:", StringComparison.Ordinal))
        {
            var replacement = await ReplaceUnreadableActiveSigningKeyAsync(key, cancellationToken);
            return (replacement, UnprotectSecret(replacement.PrivateKeyPem));
        }
    }

    private async Task<SqlOSSigningKey> ReplaceUnreadableActiveSigningKeyAsync(
        SqlOSSigningKey unreadableKey,
        CancellationToken cancellationToken)
    {
        var activeKey = await _context.Set<SqlOSSigningKey>()
            .FirstOrDefaultAsync(x => x.Id == unreadableKey.Id && x.IsActive, cancellationToken);
        if (activeKey == null)
        {
            return await EnsureActiveSigningKeyAsync(cancellationToken);
        }

        var now = DateTime.UtcNow;
        activeKey.IsActive = false;
        activeKey.RetiredAt = now;

        using var rsa = RSA.Create(2048);
        var replacement = new SqlOSSigningKey
        {
            Id = GenerateId("key"),
            Kid = GenerateOpaqueToken(16),
            PublicKeyPem = rsa.ExportRSAPublicKeyPem(),
            PrivateKeyPem = ProtectSigningPrivateKey(rsa.ExportPkcs8PrivateKeyPem()),
            ActivatedAt = now,
            IsActive = true
        };
        _context.Set<SqlOSSigningKey>().Add(replacement);
        await _context.SaveChangesAsync(cancellationToken);
        return replacement;
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
        var keys = await GetValidationSigningKeysAsync(cancellationToken: cancellationToken);
        if (keys.Count == 0)
        {
            return null;
        }

        var securityKeys = keys.Select(ToSecurityKey).ToList();
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        };

        try
        {
            var principal = handler.ValidateToken(rawToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.Issuer,
                ValidateAudience = validateAudience,
                ValidAudience = validateAudience ? expectedAudience : null,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = securityKeys,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            }, out _);

            var sessionId = principal.FindFirstValue("sid");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            var session = await _context.Set<SqlOSSession>().FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
            if (session == null || session.RevokedAt != null || session.AbsoluteExpiresAt <= DateTime.UtcNow)
            {
                return null;
            }

            session.LastSeenAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(session.ClientApplicationId))
            {
                var client = await _context.Set<SqlOSClientApplication>()
                    .FirstOrDefaultAsync(x => x.Id == session.ClientApplicationId, cancellationToken);
                if (client != null)
                {
                    client.LastSeenAt = DateTime.UtcNow;
                }
            }
            await _context.SaveChangesAsync(cancellationToken);

            return new SqlOSValidatedToken(
                principal,
                session.Id,
                principal.FindFirstValue(JwtRegisteredClaimNames.Sub),
                principal.FindFirstValue("org_id"),
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

    private async Task ProtectSigningKeyAtRestIfNeededAsync(SqlOSSigningKey key, CancellationToken cancellationToken)
    {
        if (!_options.ProtectSigningKeysWithDataProtection
            || string.IsNullOrWhiteSpace(key.PrivateKeyPem)
            || key.PrivateKeyPem.StartsWith("dp:", StringComparison.Ordinal))
        {
            return;
        }

        var protectedPrivateKey = ProtectSigningPrivateKey(key.PrivateKeyPem);
        if (string.Equals(protectedPrivateKey, key.PrivateKeyPem, StringComparison.Ordinal))
        {
            return;
        }

        key.PrivateKeyPem = protectedPrivateKey;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private string ProtectSigningPrivateKey(string privateKeyPem)
        => _options.ProtectSigningKeysWithDataProtection
            ? ProtectSecret(privateKeyPem)
            : privateKeyPem;
}
