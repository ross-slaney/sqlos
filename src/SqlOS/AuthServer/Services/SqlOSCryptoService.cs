using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    public SqlOSCryptoService(ISqlOSAuthServerDbContext context, IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _options = options.Value;
    }

    public string HashPassword(string password) => _passwordHasher.HashPassword(new object(), password);

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
        => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier)));

    public bool VerifyPkceCodeVerifier(string codeVerifier, string codeChallenge, string codeChallengeMethod)
    {
        if (!string.Equals(codeChallengeMethod, "S256", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only S256 PKCE code challenges are supported.");
        }

        var computed = CreatePkceCodeChallenge(codeVerifier);
        return string.Equals(computed, codeChallenge, StringComparison.Ordinal);
    }

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
        await _context.SaveChangesAsync(cancellationToken);
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
            return activeKey;
        }

        using var rsa = RSA.Create(2048);
        activeKey = new SqlOSSigningKey
        {
            Id = GenerateId("key"),
            Kid = GenerateOpaqueToken(16),
            PublicKeyPem = rsa.ExportRSAPublicKeyPem(),
            PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem(),
            ActivatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _context.Set<SqlOSSigningKey>().Add(activeKey);
        await _context.SaveChangesAsync(cancellationToken);
        return activeKey;
    }

    public async Task<List<SqlOSSigningKey>> GetValidationSigningKeysAsync(CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSSigningKey>()
            .Where(x => x.IsActive || x.RetiredAt == null || x.RetiredAt >= DateTime.UtcNow.AddDays(-7))
            .ToListAsync(cancellationToken);

    public async Task<string> CreateAccessTokenAsync(
        SqlOSUser user,
        SqlOSSession session,
        SqlOSClientApplication client,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        var key = await EnsureActiveSigningKeyAsync(cancellationToken);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKeyPem);
        var signingKey = new RsaSecurityKey(rsa.ExportParameters(true)) { KeyId = key.Kid };

        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new("sid", session.Id),
            new("client_id", client.ClientId),
            new("amr", session.AuthenticationMethod ?? "password")
        };

        if (!string.IsNullOrWhiteSpace(user.DefaultEmail))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.DefaultEmail));
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            claims.Add(new Claim("org_id", organizationId));
        }

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: client.Audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(_options.AccessTokenLifetime),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<SqlOSValidatedToken?> ValidateAccessTokenAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var keys = await GetValidationSigningKeysAsync(cancellationToken);
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
                ValidateAudience = false,
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
            await _context.SaveChangesAsync(cancellationToken);

            return new SqlOSValidatedToken(
                principal,
                session.Id,
                principal.FindFirstValue(JwtRegisteredClaimNames.Sub),
                principal.FindFirstValue("org_id"),
                principal.FindFirstValue("client_id"));
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
}
