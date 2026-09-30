using System.Security.Cryptography;

namespace SqlOS.BehaviorLock.Host.Support;

/// <summary>RFC 6238 time-based one-time passwords (SHA-1, 30-second steps, six digits), as authenticator apps compute them.</summary>
public static class Totp
{
    public const int StepSeconds = 30;

    public static long CurrentStep(DateTimeOffset? now = null)
        => (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / StepSeconds;

    public static string Code(string base32Secret, long step)
    {
        var key = Base32Decode(base32Secret);
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// The current step, after waiting out the last two seconds of a step, so a code computed now
    /// is still inside the same step when the server checks it.
    /// </summary>
    public static async Task<long> StableCurrentStepAsync(CancellationToken cancellationToken = default)
    {
        var secondsLeft = StepSeconds - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 % StepSeconds;
        if (secondsLeft < 2)
        {
            await Task.Delay(TimeSpan.FromSeconds(secondsLeft) + TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return CurrentStep();
    }

    /// <summary>
    /// Waits until a step later than <paramref name="lastUsedStep"/> begins, so a code is never
    /// replayed within its step (SqlOS rejects reuse), and returns that step.
    /// </summary>
    public static async Task<long> NextUnusedStepAsync(long lastUsedStep, CancellationToken cancellationToken = default)
    {
        while (CurrentStep() <= lastUsedStep)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return CurrentStep();
    }

    private static byte[] Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var cleaned = value.Trim().TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var character in cleaned)
        {
            var index = alphabet.IndexOf(character, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new FormatException($"'{character}' is not a base32 character.");
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return bytes.ToArray();
    }
}
