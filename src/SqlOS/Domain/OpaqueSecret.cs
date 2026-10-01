using System.Buffers.Text;
using System.Security.Cryptography;

namespace SqlOS.Domain;

/// <summary>
/// Random opaque secrets: temporary tokens, challenge tokens and other bearer handles SqlOS hands
/// out once and stores only as a hash.
/// </summary>
/// <remarks>
/// A value is <see cref="DefaultByteLength"/> random bytes in unpadded base64url, the 7.x format of
/// <c>SqlOSCryptoService.GenerateOpaqueToken</c>, which delegates here.
/// </remarks>
internal static class OpaqueSecret
{
    public const int DefaultByteLength = 32;

    /// <summary>A new random value of <paramref name="byteLength"/> bytes, base64url without padding.</summary>
    public static string NewValue(int byteLength = DefaultByteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);
        return Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(byteLength));
    }

    /// <summary>A new secret and the SHA-256 hash SqlOS stores for it.</summary>
    public static IssuedSecret Issue()
    {
        var value = NewValue();
        return new IssuedSecret(value, HashedSecret.Sha256(value));
    }
}

/// <summary>
/// A secret at the moment it is issued: the value to hand out once, and the hash to store.
/// <see cref="ToString"/> never shows the value.
/// </summary>
internal readonly record struct IssuedSecret(string Value, HashedSecret Hash)
{
    public override string ToString() => $"{nameof(IssuedSecret)}({Hash.Scheme})";
}
