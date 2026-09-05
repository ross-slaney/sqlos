using System.Globalization;
using System.Security.Cryptography;

namespace SqlOS.Example.E2eTests;

/// <summary>
/// RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30-second step) — the same parameters
/// SqlOS uses by default — so the tests can complete authenticator enrollment
/// from the secret the headless UI displays, exactly like an authenticator app.
/// </summary>
internal static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Now(string base32Secret, int digits = 6, int periodSeconds = 30)
    {
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / periodSeconds;
        return Compute(DecodeBase32(base32Secret), step, digits);
    }

    private static string Compute(byte[] secret, long timeStep, int digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BitConverter.TryWriteBytes(counter, timeStep);
        if (BitConverter.IsLittleEndian)
        {
            counter.Reverse();
        }

        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0f;
        var binary =
            ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        var modulo = (int)Math.Pow(10, digits);
        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    private static byte[] DecodeBase32(string input)
    {
        var cleaned = input.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);
        var output = new List<byte>(cleaned.Length * 5 / 8);
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var character in cleaned)
        {
            var value = Base32Alphabet.IndexOf(character, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException($"'{character}' is not a base32 character.");
            }

            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xff));
                bitsLeft -= 8;
            }
        }

        return output.ToArray();
    }
}
