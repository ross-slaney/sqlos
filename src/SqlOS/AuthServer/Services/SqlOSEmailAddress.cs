using System.Globalization;
using System.Text;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// The single canonical key SqlOS uses wherever an email address identifies an account.
/// </summary>
/// <remarks>
/// <para>
/// Two different mailboxes must never share a key, because the key decides whose account a
/// login code, magic link, upstream identity, SAML assertion, SCIM record, or invitation lands
/// in. The key is therefore built with rules that do not depend on culture, database collation,
/// or Unicode case tables:
/// </para>
/// <list type="bullet">
/// <item>Surrounding whitespace is trimmed; control, format, separator, private-use, unassigned,
/// and unpaired-surrogate characters are rejected, as is any character that is only a disguise
/// for ASCII (its compatibility decomposition is entirely ASCII, such as the long s, the Kelvin
/// sign, fullwidth letters, and ligatures).</item>
/// <item>The address is normalized to Unicode NFC and must contain exactly one <c>@</c> with a
/// non-empty local part and domain.</item>
/// <item>A non-ASCII domain is converted to its IDNA ASCII form. It must already be in its
/// lower-cased IDNA form apart from letter case; mappings that would silently change it (for
/// example removing a zero-width character or folding a compatibility form) reject the address.
/// </item>
/// <item>Only ASCII letters are case-folded (to upper case, matching the historical key for
/// ASCII addresses). Non-ASCII local-part characters are kept exactly as written.</item>
/// </list>
/// <para>
/// SQL equality on the stored key is only a candidate filter. Callers confirm a match in C# by
/// ordinal equality of canonical keys (<see cref="MatchesStoredEmail"/>), so a collation that
/// treats two different strings as equal can never select another person's account.
/// </para>
/// </remarks>
internal static class SqlOSEmailAddress
{
    public const int MaxLength = 320;
    public const string InvalidEmailMessage = "Enter a valid email address.";

    /// <summary>Computes the canonical key, or returns false when the address is not valid.</summary>
    public static bool TryNormalize(string? value, out string normalizedEmail)
        => TryCanonicalize(value, out _, out normalizedEmail);

    /// <summary>
    /// Computes the canonical key and the canonical address (trimmed, NFC) to store and display.
    /// </summary>
    public static bool TryCanonicalize(string? value, out string address, out string normalizedEmail)
    {
        address = string.Empty;
        normalizedEmail = string.Empty;
        if (value == null)
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxLength || !HasOnlyAllowedCharacters(trimmed))
        {
            return false;
        }

        string composed;
        try
        {
            composed = trimmed.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var at = composed.IndexOf('@');
        if (at <= 0 || at != composed.LastIndexOf('@') || at == composed.Length - 1)
        {
            return false;
        }

        if (!TryNormalizeDomain(composed[(at + 1)..], out var asciiDomain))
        {
            return false;
        }

        var key = $"{ToUpperAscii(composed[..at])}@{ToUpperAscii(asciiDomain)}";
        if (key.Length > MaxLength)
        {
            return false;
        }

        address = composed;
        normalizedEmail = key;
        return true;
    }

    /// <summary>
    /// Normalizes a DNS domain the same way the email key does: ASCII domains are case-folded,
    /// internationalized domains become their lower-case IDNA ASCII form.
    /// </summary>
    public static bool TryNormalizeDomain(string? domain, out string asciiDomain)
    {
        asciiDomain = string.Empty;
        if (string.IsNullOrEmpty(domain) || !HasOnlyAllowedCharacters(domain))
        {
            return false;
        }

        if (IsAscii(domain))
        {
            asciiDomain = ToLowerAscii(domain);
            return true;
        }

        string composed;
        try
        {
            composed = domain.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var lower = new StringBuilder(composed.Length);
        foreach (var character in composed)
        {
            var lowered = char.ToLowerInvariant(character);
            if (character >= 0x80 && lowered < 0x80)
            {
                // A non-ASCII letter that only case-folds into ASCII is a look-alike.
                return false;
            }

            lower.Append(lowered);
        }

        var lowerDomain = lower.ToString();
        var idn = new IdnMapping { UseStd3AsciiRules = true, AllowUnassigned = false };
        try
        {
            var ascii = idn.GetAscii(lowerDomain);
            if (!string.Equals(idn.GetUnicode(ascii), lowerDomain, StringComparison.Ordinal))
            {
                return false;
            }

            asciiDomain = ToLowerAscii(ascii);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The lower-case ASCII domain of a canonical key.</summary>
    public static string GetDomain(string normalizedEmail)
        => ToLowerAscii(normalizedEmail[(normalizedEmail.LastIndexOf('@') + 1)..]);

    /// <summary>
    /// True when the stored row is the same mailbox as the canonical key. The stored address is
    /// re-canonicalized rather than trusting the stored key, so rows keyed by an older algorithm
    /// match only when they really are the same mailbox.
    /// </summary>
    public static bool MatchesStoredEmail(SqlOSUserEmail row, string normalizedEmail)
        => TryNormalize(row.Email, out var key)
            && string.Equals(key, normalizedEmail, StringComparison.Ordinal);

    /// <summary>True when both values are valid addresses for the same mailbox.</summary>
    public static bool IsSameMailbox(string? left, string? right)
        => TryNormalize(left, out var leftKey)
            && TryNormalize(right, out var rightKey)
            && string.Equals(leftKey, rightKey, StringComparison.Ordinal);

    /// <summary>
    /// The key used for input that is not a valid address (for example rate-limit buckets). Only
    /// ASCII letters are folded, so it keeps the characters that made the address invalid and can
    /// never equal the canonical key of a valid address.
    /// </summary>
    public static string FallbackKey(string? value) => ToUpperAscii((value ?? string.Empty).Trim());

    /// <summary>
    /// SQL candidate keys for a lookup: the canonical key plus the keys SqlOS 7.2.0 and earlier
    /// stored (<c>Trim().ToUpperInvariant()</c>) so existing rows stay reachable. Every candidate
    /// row is confirmed with <see cref="MatchesStoredEmail"/> before it is used.
    /// </summary>
    public static IReadOnlyList<string> LookupKeys(string normalizedEmail, string? rawEmail)
    {
        var keys = new List<string>(3) { normalizedEmail };
        if (!string.IsNullOrWhiteSpace(rawEmail))
        {
            AddDistinct(keys, LegacyKey(rawEmail));
            try
            {
                AddDistinct(keys, LegacyKey(rawEmail.Trim().Normalize(NormalizationForm.FormC)));
            }
            catch (ArgumentException)
            {
            }
        }

        return keys;
    }

    internal static string LegacyKey(string value) => value.Trim().ToUpperInvariant();

    private static void AddDistinct(List<string> keys, string key)
    {
        if (!keys.Contains(key, StringComparer.Ordinal))
        {
            keys.Add(key);
        }
    }

    private static bool HasOnlyAllowedCharacters(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                return false;
            }

            index += consumed - 1;
            if (!IsAllowed(rune))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowed(Rune rune)
    {
        if (rune.IsAscii)
        {
            return rune.Value > 0x20 && rune.Value != 0x7F;
        }

        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.Control:
            case UnicodeCategory.Format:
            case UnicodeCategory.Surrogate:
            case UnicodeCategory.PrivateUse:
            case UnicodeCategory.OtherNotAssigned:
            case UnicodeCategory.SpaceSeparator:
            case UnicodeCategory.LineSeparator:
            case UnicodeCategory.ParagraphSeparator:
                return false;
        }

        var decomposed = rune.ToString().Normalize(NormalizationForm.FormKD);
        foreach (var character in decomposed)
        {
            if (character >= 0x80)
            {
                return true;
            }
        }

        // The character is only a compatibility or canonical disguise for ASCII text.
        return false;
    }

    private static bool IsAscii(string value)
    {
        foreach (var character in value)
        {
            if (character >= 0x80)
            {
                return false;
            }
        }

        return true;
    }

    private static string ToUpperAscii(string value)
        => string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                span[index] = character is >= 'a' and <= 'z' ? (char)(character - 32) : character;
            }
        });

    private static string ToLowerAscii(string value)
        => string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                span[index] = character is >= 'A' and <= 'Z' ? (char)(character + 32) : character;
            }
        });
}
