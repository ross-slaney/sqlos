using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace SqlOS.Domain;

/// <summary>
/// An email address: the form SqlOS stores and displays, and the single canonical key that decides
/// which account the address belongs to (#422).
/// </summary>
/// <remarks>
/// <para>
/// Two different mailboxes must never share a key, because the key decides whose account a login
/// code, magic link, upstream identity, SAML assertion, SCIM record, or invitation lands in. The
/// key is therefore built with rules that do not depend on culture, database collation, or
/// Unicode case tables:
/// </para>
/// <list type="bullet">
/// <item>Surrounding whitespace is trimmed; control, format (the invisible markup such as bidi
/// overrides, zero-width characters and soft hyphens), separator, private-use, unassigned, and
/// unpaired-surrogate characters are rejected, as is any character that is only a disguise for
/// ASCII (its compatibility decomposition is entirely ASCII, such as the long s, the Kelvin sign,
/// fullwidth letters, and ligatures).</item>
/// <item>The address is normalized to Unicode NFC and must contain exactly one <c>@</c> with a
/// non-empty local part and domain.</item>
/// <item>A non-ASCII domain is converted to its IDNA ASCII form. It must already be in its
/// lower-cased IDNA form apart from letter case; mappings that would silently change it (for
/// example removing a zero-width character or folding a compatibility form) reject the address.
/// </item>
/// <item>Only ASCII letters are case-folded (to upper case, matching the historical key for ASCII
/// addresses). Non-ASCII local-part characters are kept exactly as written.</item>
/// </list>
/// <para>
/// Visible ASCII punctuation, including HTML markup characters such as <c>&lt;</c> and
/// <c>&gt;</c>, is accepted as in 7.2.1; renderers encode addresses on output. Rejecting it would
/// be a behavior change that needs a ledger entry.
/// </para>
/// <para>
/// Equality is ordinal equality of <see cref="Canonical"/>, the only equality SqlOS uses for
/// lookups. SQL equality on the stored key is only a candidate filter: a lookup confirms the match
/// with <see cref="MatchesStored"/>, so a collation that treats two different strings as equal can
/// never select another person's account. These rules moved here unchanged from the 7.2.1
/// <c>SqlOSEmailAddress</c>, which now delegates to this type.
/// </para>
/// </remarks>
internal sealed class EmailAddress : IEquatable<EmailAddress>
{
    public const int MaxLength = 320;
    public const string InvalidMessage = "Enter a valid email address.";

    private EmailAddress(string address, string canonical)
    {
        Address = address;
        Canonical = canonical;
    }

    /// <summary>The address to store and display: trimmed and in Unicode NFC.</summary>
    public string Address { get; }

    /// <summary>The canonical key (the <c>NormalizedEmail</c> column).</summary>
    public string Canonical { get; }

    /// <summary>The lower-case IDNA ASCII domain.</summary>
    public string Domain => DomainOf(Canonical);

    public static bool TryParse(string? value, [NotNullWhen(true)] out EmailAddress? emailAddress)
    {
        emailAddress = null;
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

        if (!TryCanonicalizeDomain(composed[(at + 1)..], out var asciiDomain))
        {
            return false;
        }

        var key = $"{ToUpperAscii(composed[..at])}@{ToUpperAscii(asciiDomain)}";
        if (key.Length > MaxLength)
        {
            return false;
        }

        emailAddress = new EmailAddress(composed, key);
        return true;
    }

    public static EmailAddress Parse(string? value)
        => TryParse(value, out var emailAddress)
            ? emailAddress
            : throw SqlOSDomainException.Of(SqlOSDomainError.InvalidEmailAddress, InvalidMessage);

    /// <summary>
    /// Normalizes a DNS domain the same way the email key does: ASCII domains are case-folded,
    /// internationalized domains become their lower-case IDNA ASCII form.
    /// </summary>
    public static bool TryCanonicalizeDomain(string? domain, out string asciiDomain)
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
    public static string DomainOf(string canonical)
        => ToLowerAscii(canonical[(canonical.LastIndexOf('@') + 1)..]);

    /// <summary>
    /// The key for input that is not a valid address (for example rate-limit buckets). Only ASCII
    /// letters are folded, so it keeps the characters that made the address invalid and can never
    /// equal the canonical key of a valid address.
    /// </summary>
    public static string FallbackKey(string? value) => ToUpperAscii((value ?? string.Empty).Trim());

    /// <summary>
    /// The key SqlOS 7.2.0 and earlier stored (<c>Trim().ToUpperInvariant()</c>). Lookups include
    /// it so rows written by those versions stay reachable.
    /// </summary>
    public static string LegacyKey(string value) => value.Trim().ToUpperInvariant();

    /// <summary>
    /// SQL candidate keys for a lookup: the canonical key plus the 7.2.0 keys of the raw input.
    /// Every candidate row is confirmed with <see cref="MatchesStored"/> before it is used.
    /// </summary>
    public static IReadOnlyList<string> LookupKeys(string canonical, string? rawEmail)
    {
        var keys = new List<string>(3) { canonical };
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

    /// <summary>
    /// True when a stored address is this mailbox. The stored address is re-canonicalized rather
    /// than trusting a stored key, so rows keyed by an older algorithm match only when they really
    /// are the same mailbox, and a stored address that is not valid never matches.
    /// </summary>
    public bool MatchesStored(string? storedAddress)
        => TryParse(storedAddress, out var stored) && Equals(stored);

    public bool Equals(EmailAddress? other)
        => other is not null && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as EmailAddress);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Canonical);

    public override string ToString() => Address;

    public static bool operator ==(EmailAddress? left, EmailAddress? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(EmailAddress? left, EmailAddress? right) => !(left == right);

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
