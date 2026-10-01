using System.Diagnostics.CodeAnalysis;
using PhoneNumbers;

namespace SqlOS.Domain;

/// <summary>
/// A valid phone number in E.164 form (<c>+16502530000</c>), parsed with libphonenumber exactly as
/// SqlOS 7.x parses phone numbers.
/// </summary>
/// <remarks>
/// A number without a country code is read in the caller's default region; the caller passes that
/// region as 7.x did (the phone OTP flow trims and upper-cases its configured region first). A
/// number libphonenumber cannot parse, or parses but does not consider valid, is rejected. Which
/// countries may receive codes is the caller's policy, applied to <see cref="Region"/>. Equality
/// is ordinal equality of <see cref="E164"/>.
/// </remarks>
internal sealed record PhoneNumber
{
    public const string RequiredMessage = "Phone number is required.";
    public const string InvalidMessage = "Phone number is invalid.";

    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    private PhoneNumber(string e164, string? region)
    {
        E164 = e164;
        Region = region;
    }

    public string E164 { get; }

    /// <summary>The upper-case region code libphonenumber assigns the number (<c>US</c>).</summary>
    public string? Region { get; }

    public static bool TryParse(string? value, string? defaultRegion, [NotNullWhen(true)] out PhoneNumber? phoneNumber)
        => TryParse(value, defaultRegion, out phoneNumber, out _);

    public static bool TryParse(
        string? value,
        string? defaultRegion,
        [NotNullWhen(true)] out PhoneNumber? phoneNumber,
        [NotNullWhen(false)] out string? error)
    {
        phoneNumber = null;
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = RequiredMessage;
            return false;
        }

        try
        {
            var parsed = Util.Parse(trimmed, defaultRegion);
            if (!Util.IsValidNumber(parsed))
            {
                error = InvalidMessage;
                return false;
            }

            phoneNumber = new PhoneNumber(
                Util.Format(parsed, PhoneNumberFormat.E164),
                Util.GetRegionCodeForNumber(parsed)?.ToUpperInvariant());
            error = null;
            return true;
        }
        catch (NumberParseException)
        {
            error = InvalidMessage;
            return false;
        }
    }

    public static PhoneNumber Parse(string? value, string? defaultRegion)
        => TryParse(value, defaultRegion, out var phoneNumber, out var error)
            ? phoneNumber
            : throw SqlOSDomainException.Of(SqlOSDomainError.InvalidPhoneNumber, error);

    public bool Equals(PhoneNumber? other)
        => other is not null && string.Equals(E164, other.E164, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(E164);

    public override string ToString() => E164;
}
