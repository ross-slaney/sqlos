using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

namespace SqlOS.Domain;

/// <summary>
/// A DNS domain an organization can claim: a lower-case IDNA ASCII name with a public suffix.
/// </summary>
/// <remarks>
/// <para>
/// Parsing accepts what an operator types (a bare domain, a URL, or an email address) and applies
/// the 7.2.1 claim rules in order, each with its 7.2.1 message: a value is required, wildcards,
/// names that are not valid DNS names and IP literals are rejected, <c>localhost</c> only when
/// <see cref="DomainNameRules.AllowLocalhost"/>, single-label names and invalid labels are
/// rejected, and so is the host's own namespace (<see cref="DomainNameRules.ReservedRoots"/>).
/// </para>
/// <para>
/// An unverified organization <c>PrimaryDomain</c> hint is not a claim and keeps its looser 7.x
/// normalization; the domain of an email address is part of <see cref="EmailAddress"/>.
/// </para>
/// </remarks>
internal sealed record DomainName
{
    public const string RequiredMessage = "Domain is required.";
    public const string InvalidDnsNameMessage = "Domain is not a valid DNS name.";

    private DomainName(string value) => Value = value;

    /// <summary>The lower-case IDNA ASCII name.</summary>
    public string Value { get; }

    public static bool TryParse(
        string? value,
        DomainNameRules rules,
        [NotNullWhen(true)] out DomainName? domainName,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(rules);
        domainName = null;
        error = Validate(value, rules, out var asciiDomain);
        if (error is not null)
        {
            return false;
        }

        domainName = new DomainName(asciiDomain);
        return true;
    }

    public static DomainName Parse(string? value, DomainNameRules rules)
        => TryParse(value, rules, out var domainName, out var error)
            ? domainName
            : throw SqlOSDomainException.Of(SqlOSDomainError.InvalidDomainName, error);

    public override string ToString() => Value;

    private static string? Validate(string? value, DomainNameRules rules, out string asciiDomain)
    {
        asciiDomain = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return RequiredMessage;
        }

        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            candidate = uri.Host;
        }

        var atIndex = candidate.LastIndexOf('@');
        if (atIndex >= 0)
        {
            candidate = candidate[(atIndex + 1)..];
        }

        candidate = candidate.Trim().Trim('[', ']').Trim('.').Trim().ToLowerInvariant();
        if (candidate.StartsWith("*.", StringComparison.Ordinal) || candidate.Contains('*', StringComparison.Ordinal))
        {
            return "Wildcard domains cannot be verified.";
        }

        try
        {
            asciiDomain = new IdnMapping().GetAscii(candidate).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return InvalidDnsNameMessage;
        }

        if (IPAddress.TryParse(asciiDomain, out _))
        {
            return "IP addresses cannot be verified as organization domains.";
        }

        if (string.Equals(asciiDomain, "localhost", StringComparison.Ordinal))
        {
            return rules.AllowLocalhost ? null : "Localhost domain verification is disabled.";
        }

        if (!asciiDomain.Contains('.', StringComparison.Ordinal))
        {
            return "Domain must include a public DNS suffix.";
        }

        var labelError = ValidateLabels(asciiDomain);
        if (labelError is not null)
        {
            return labelError;
        }

        foreach (var root in rules.ReservedRoots)
        {
            var normalizedRoot = NormalizeReservedRoot(root);
            if (string.IsNullOrWhiteSpace(normalizedRoot))
            {
                continue;
            }

            if (string.Equals(asciiDomain, normalizedRoot, StringComparison.Ordinal)
                || asciiDomain.EndsWith($".{normalizedRoot}", StringComparison.Ordinal))
            {
                return $"Domain is reserved by the SqlOS host: {normalizedRoot}.";
            }
        }

        return null;
    }

    private static string? ValidateLabels(string domain)
    {
        if (domain.Length > 253)
        {
            return "Domain name is too long.";
        }

        var labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2)
        {
            return "Domain must include a public DNS suffix.";
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
            {
                return "Domain contains an invalid DNS label.";
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                return "Domain labels cannot start or end with a hyphen.";
            }

            if (!label.All(static ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                return "Domain contains characters that are not valid in DNS labels.";
            }
        }

        return null;
    }

    /// <summary>
    /// A reserved root is host configuration, not input: one that is not a valid DNS name is a
    /// misconfiguration and throws, as in 7.2.1, when a domain reaches the reserved-root check.
    /// </summary>
    private static string? NormalizeReservedRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var root = value.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            return new IdnMapping().GetAscii(root).ToLowerInvariant();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Reserved domain root is invalid: {value}.", ex);
        }
    }
}

/// <summary>The host's rules for claimable domains (<c>SqlOSSsoPortalOptions</c>).</summary>
internal sealed record DomainNameRules(IReadOnlyCollection<string> ReservedRoots, bool AllowLocalhost)
{
    /// <summary>No reserved roots; <c>localhost</c> is not claimable.</summary>
    public static DomainNameRules Default { get; } = new([], AllowLocalhost: false);
}
