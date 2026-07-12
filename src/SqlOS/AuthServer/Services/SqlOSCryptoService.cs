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
    private readonly PasswordHasher<object> _passwordHasher = new();
    private readonly IDataProtector? _secretProtector;
    private readonly ISqlOSSigningKeyCustody _signingKeyCustody;

    public SqlOSCryptoService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        IDataProtectionProvider? dataProtectionProvider = null,
        ISqlOSSigningKeyCustody? signingKeyCustody = null)
    {
        _context = context;
        _options = options.Value;
        _secretProtector = dataProtectionProvider?.CreateProtector("SqlOS.AuthServer.OidcSecrets");
        _signingKeyCustody = signingKeyCustody
            ?? new SqlOSDataProtectionSigningKeyCustody(options, dataProtectionProvider);
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

    public Task<SqlOSSigningKey> EnsureActiveSigningKeyAsync(CancellationToken cancellationToken = default)
        => EnsureActiveSigningKeyCoreAsync(validateExistingCustody: true, cancellationToken);

    private async Task<SqlOSSigningKey> EnsureActiveSigningKeyCoreAsync(
        bool validateExistingCustody,
        CancellationToken cancellationToken)
    {
        var observedKeys = await _context.Set<SqlOSSigningKey>().ToListAsync(cancellationToken);
        ValidateStoredSigningKeyRows(observedKeys);
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
        await using var transaction = await BeginSigningKeyTransactionAsync(cancellationToken);
        SqlOSSigningKey? createdKey = null;
        try
        {
            var keys = await _context.Set<SqlOSSigningKey>().ToListAsync(cancellationToken);
            ValidateStoredSigningKeyRows(keys);
            var activeKeys = keys.Where(static key => key.IsActive).ToList();
            if (activeKeys.Count > 1)
            {
                throw new InvalidOperationException("SqlOS found multiple active signing keys. Refusing to issue tokens until signing-key state is repaired.");
            }

            if (activeKeys.Count == 1)
            {
                if (validateExistingCustody)
                {
                    await ValidateCustodyCanSignAsync(activeKeys[0], cancellationToken);
                }
                await CommitAsync(transaction, cancellationToken);
                return activeKeys[0];
            }

            createdKey = await CreateSigningKeyAsync(keys, cancellationToken);
            _context.Set<SqlOSSigningKey>().Add(createdKey);
            await _context.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return createdKey;
        }
        catch
        {
            if (createdKey != null)
            {
                await TryDeleteCreatedKeyAsync(createdKey);
            }

            throw;
        }
    }

    public async Task<List<SqlOSSigningKey>> GetValidationSigningKeysAsync(TimeSpan? graceWindow = null, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.Add(-(graceWindow ?? TimeSpan.FromDays(_options.DefaultSigningKeyGraceWindowDays)));
        var keys = await _context.Set<SqlOSSigningKey>()
            .Where(x => x.IsActive || x.RetiredAt == null || x.RetiredAt >= cutoff)
            .ToListAsync(cancellationToken);
        ValidateStoredSigningKeyRows(keys);
        return keys;
    }

    public async Task<SqlOSSigningKey> RotateSigningKeyAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginSigningKeyTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        SqlOSSigningKey? newKey = null;

        try
        {
            var keys = await _context.Set<SqlOSSigningKey>().ToListAsync(cancellationToken);
            ValidateStoredSigningKeyRows(keys);
            var activeKeys = keys.Where(static key => key.IsActive).ToList();
            if (activeKeys.Count > 1)
            {
                throw new InvalidOperationException("SqlOS found multiple active signing keys. Refusing rotation until signing-key state is repaired.");
            }

            if (activeKeys.Count == 1)
            {
                await ValidateCustodyCanSignAsync(activeKeys[0], cancellationToken);
            }

            newKey = await CreateSigningKeyAsync(keys, cancellationToken);
            if (activeKeys.Count == 1)
            {
                activeKeys[0].IsActive = false;
                activeKeys[0].RetiredAt = now;
            }

            _context.Set<SqlOSSigningKey>().Add(newKey);
            await _context.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return newKey;
        }
        catch
        {
            if (newKey != null)
            {
                await TryDeleteCreatedKeyAsync(newKey);
            }

            throw;
        }
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

        ValidateStoredSigningKeyRows(expired);
        foreach (var key in expired)
        {
            await _signingKeyCustody.DeleteKeyAsync(ToDescriptor(key), cancellationToken);
        }

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

            var matchingKeys = keys
                .Where(key => string.Equals(key.Kid, jwt.Header.Kid, StringComparison.Ordinal)
                    && string.Equals(key.Algorithm, jwt.Header.Alg, StringComparison.Ordinal))
                .ToList();
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
        foreach (var key in keys)
        {
            ValidateStoredSigningKeyRow(key);
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

    private async Task<IDbContextTransaction?> BeginSigningKeyTransactionAsync(CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational())
        {
            return null;
        }

        var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            if (string.Equals(
                _context.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.SqlServer",
                StringComparison.Ordinal))
            {
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

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private static async Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
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
