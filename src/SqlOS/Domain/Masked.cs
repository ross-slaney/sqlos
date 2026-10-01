namespace SqlOS.Domain;

/// <summary>
/// How SqlOS shows an address it must not disclose in full: in audit rows, and in the messages that
/// say where a code or link went. The forms are exactly 7.x's.
/// </summary>
internal static class Masked
{
    /// <summary>
    /// The first two characters of the local part, <c>***</c>, and the domain as written
    /// (<c>al***@example.test</c>). A value without a local part of at least two characters, or
    /// without a domain, is shown as it is.
    /// </summary>
    public static string Email(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var atIndex = email.IndexOf('@');
        if (atIndex <= 1 || atIndex == email.Length - 1)
        {
            return email;
        }

        var local = email[..atIndex];
        var domain = email[(atIndex + 1)..];
        var visibleCount = Math.Min(2, local.Length);
        return $"{local[..visibleCount]}***@{domain}";
    }

    /// <summary>
    /// The first two and last four characters of an E.164 number with at least three <c>*</c>
    /// between them (<c>+1*****0000</c>). A number of five characters or fewer is shown as it is.
    /// </summary>
    public static string Phone(string e164PhoneNumber)
    {
        ArgumentNullException.ThrowIfNull(e164PhoneNumber);
        if (e164PhoneNumber.Length <= 5)
        {
            return e164PhoneNumber;
        }

        var prefix = e164PhoneNumber[..Math.Min(2, e164PhoneNumber.Length)];
        var suffix = e164PhoneNumber[^Math.Min(4, e164PhoneNumber.Length)..];
        return $"{prefix}{new string('*', Math.Max(3, e164PhoneNumber.Length - prefix.Length - suffix.Length))}{suffix}";
    }
}
