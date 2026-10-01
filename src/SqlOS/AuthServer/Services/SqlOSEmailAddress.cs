using SqlOS.AuthServer.Models;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// The 7.2.1 string entry points to SqlOS's canonical email key. The rules live in
/// <see cref="EmailAddress"/>; these methods delegate to it so existing call sites keep their
/// shape until they move onto the value object.
/// </summary>
internal static class SqlOSEmailAddress
{
    public const int MaxLength = EmailAddress.MaxLength;
    public const string InvalidEmailMessage = EmailAddress.InvalidMessage;

    /// <summary>Computes the canonical key, or returns false when the address is not valid.</summary>
    public static bool TryNormalize(string? value, out string normalizedEmail)
        => TryCanonicalize(value, out _, out normalizedEmail);

    /// <summary>
    /// Computes the canonical key and the canonical address (trimmed, NFC) to store and display.
    /// </summary>
    public static bool TryCanonicalize(string? value, out string address, out string normalizedEmail)
    {
        if (EmailAddress.TryParse(value, out var emailAddress))
        {
            address = emailAddress.Address;
            normalizedEmail = emailAddress.Canonical;
            return true;
        }

        address = string.Empty;
        normalizedEmail = string.Empty;
        return false;
    }

    /// <inheritdoc cref="EmailAddress.TryCanonicalizeDomain"/>
    public static bool TryNormalizeDomain(string? domain, out string asciiDomain)
        => EmailAddress.TryCanonicalizeDomain(domain, out asciiDomain);

    /// <summary>The lower-case ASCII domain of a canonical key.</summary>
    public static string GetDomain(string normalizedEmail) => EmailAddress.DomainOf(normalizedEmail);

    /// <summary>
    /// True when the stored row is the same mailbox as the canonical key. The stored address is
    /// re-canonicalized rather than trusting the stored key.
    /// </summary>
    public static bool MatchesStoredEmail(SqlOSUserEmail row, string normalizedEmail)
        => EmailAddress.TryParse(row.Email, out var stored)
            && string.Equals(stored.Canonical, normalizedEmail, StringComparison.Ordinal);

    /// <summary>True when both values are valid addresses for the same mailbox.</summary>
    public static bool IsSameMailbox(string? left, string? right)
        => EmailAddress.TryParse(left, out var leftAddress)
            && EmailAddress.TryParse(right, out var rightAddress)
            && leftAddress.Equals(rightAddress);

    /// <inheritdoc cref="EmailAddress.FallbackKey"/>
    public static string FallbackKey(string? value) => EmailAddress.FallbackKey(value);

    /// <inheritdoc cref="EmailAddress.LookupKeys"/>
    public static IReadOnlyList<string> LookupKeys(string normalizedEmail, string? rawEmail)
        => EmailAddress.LookupKeys(normalizedEmail, rawEmail);

    internal static string LegacyKey(string value) => EmailAddress.LegacyKey(value);
}
