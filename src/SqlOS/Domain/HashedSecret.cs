using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace SqlOS.Domain;

/// <summary>
/// A secret SqlOS stores only as a one-way hash. It holds the stored hash, never the raw value,
/// and compares a presented secret in constant time.
/// </summary>
/// <remarks>
/// <para>
/// Both schemes are exactly what SqlOS 7.x stores, so no stored value changes:
/// </para>
/// <list type="bullet">
/// <item><see cref="HashedSecretScheme.Sha256"/>: the uppercase hex SHA-256 of the UTF-8 secret
/// (<c>SqlOSCryptoService.HashToken</c>), for high-entropy opaque tokens and codes that SqlOS also
/// looks up by their hash.</item>
/// <item><see cref="HashedSecretScheme.Pbkdf2"/>: the ASP.NET Core Identity
/// <see cref="PasswordHasher{TUser}"/> payload (salted PBKDF2, versioned header), for passwords
/// and client secrets (<c>SqlOSCryptoService.HashPassword</c>). Older payloads (Identity v2, and v3
/// with HMAC-SHA256 and fewer iterations) still match without being rehashed, as in 7.x.</item>
/// </list>
/// <para>
/// A stored value is wrapped as is. A SHA-256 hash that is not the hash of the presented secret
/// (including a malformed one) never matches; a PBKDF2 payload behaves exactly like
/// <see cref="PasswordHasher{TUser}.VerifyHashedPassword"/>. A <see langword="null"/> secret or
/// stored hash throws the <see cref="ArgumentNullException"/> of the underlying hash function, as
/// 7.x did (the behavior lock records its message). The default value has no scheme and never
/// matches. <see cref="ToString"/> names the scheme only, so a hash never reaches a log by
/// accident.
/// </para>
/// </remarks>
internal readonly record struct HashedSecret
{
    private static readonly PasswordHasher<object> PasswordHasher = new();
    private static readonly object PasswordHasherUser = new();

    private HashedSecret(HashedSecretScheme scheme, string hash)
    {
        Scheme = scheme;
        Hash = hash;
    }

    public HashedSecretScheme Scheme { get; }

    /// <summary>The hash exactly as it is stored.</summary>
    public string Hash { get; }

    /// <summary>Hashes an opaque token or code with SHA-256.</summary>
    public static HashedSecret Sha256(string secret)
        => new(HashedSecretScheme.Sha256, ComputeSha256Hex(secret));

    /// <summary>Hashes a password or client secret with the ASP.NET Core Identity password hasher.</summary>
    public static HashedSecret Pbkdf2(string secret)
        => new(HashedSecretScheme.Pbkdf2, PasswordHasher.HashPassword(PasswordHasherUser, secret));

    /// <summary>Wraps a stored hash without changing it.</summary>
    public static HashedSecret FromStored(HashedSecretScheme scheme, string hash)
        => scheme is HashedSecretScheme.Sha256 or HashedSecretScheme.Pbkdf2
            ? new HashedSecret(scheme, hash)
            : throw new ArgumentOutOfRangeException(nameof(scheme), scheme, "Unknown hashed secret scheme.");

    /// <summary>
    /// True when <paramref name="hash"/> is a payload the password hasher can verify: base64 with
    /// a version 2 or 3 header. Hosts may supply such hashes for seeded client secrets.
    /// </summary>
    public static bool IsPbkdf2Payload(string? hash)
    {
        if (hash is null)
        {
            return false;
        }

        try
        {
            var payload = Convert.FromBase64String(hash);
            return payload.Length >= 13 && payload[0] is 0x00 or 0x01;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="secret"/> is the secret this hash was made from.</summary>
    public bool Matches(string secret)
        => Scheme switch
        {
            HashedSecretScheme.Sha256 => CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(ComputeSha256Hex(secret)),
                Encoding.ASCII.GetBytes(Hash)),
            HashedSecretScheme.Pbkdf2 => PasswordHasher.VerifyHashedPassword(PasswordHasherUser, Hash, secret)
                is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded,
            _ => false
        };

    public override string ToString() => $"{nameof(HashedSecret)}({Scheme})";

    private static string ComputeSha256Hex(string secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

/// <summary>How a <see cref="HashedSecret"/> was hashed.</summary>
internal enum HashedSecretScheme
{
    /// <summary>Uppercase hex SHA-256 of the UTF-8 secret.</summary>
    Sha256 = 1,

    /// <summary>ASP.NET Core Identity <see cref="PasswordHasher{TUser}"/> payload (PBKDF2).</summary>
    Pbkdf2 = 2
}
