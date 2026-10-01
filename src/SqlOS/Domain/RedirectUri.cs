using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace SqlOS.Domain;

/// <summary>
/// An OAuth redirect URI: an absolute URI, kept exactly as written because matching is exact.
/// </summary>
/// <remarks>
/// <para>
/// These are the 7.2.1 <c>SqlOSRedirectUriPolicy</c> rules, which now delegates here: a requested
/// URI matches a registration by ordinal equality, except that an HTTP loopback IP literal ignores
/// the port on both sides (RFC 8252 §7.3); a URI may be registered dynamically only when it is
/// HTTPS or HTTP loopback, each as the host allows.
/// </para>
/// <para>
/// Callers trim input where 7.x trimmed it. Equality is ordinal equality of <see cref="Value"/>.
/// </para>
/// </remarks>
internal sealed record RedirectUri
{
    public const string InvalidMessage = "Redirect URI must be an absolute URI.";

    private RedirectUri(string value) => Value = value;

    /// <summary>The URI exactly as registered or requested.</summary>
    public string Value { get; }

    /// <summary>True when the URI has a fragment, which RFC 6749 §3.1.2 forbids in a redirect URI.</summary>
    public bool HasFragment => !string.IsNullOrWhiteSpace(ToUri().Fragment);

    public static bool TryCreate(string? value, [NotNullWhen(true)] out RedirectUri? redirectUri)
    {
        redirectUri = value is not null && Uri.TryCreate(value, UriKind.Absolute, out _)
            ? new RedirectUri(value)
            : null;
        return redirectUri is not null;
    }

    public static RedirectUri Create(string? value)
        => TryCreate(value, out var redirectUri)
            ? redirectUri
            : throw SqlOSDomainException.Of(SqlOSDomainError.InvalidRedirectUri);

    /// <summary>True when a client may register this URI dynamically (DCR or a metadata document).</summary>
    public bool IsAllowedForRegistration(bool allowHttpsRedirectUris, bool allowLoopbackRedirectUris)
        => IsAllowedForRegistration(ToUri(), allowHttpsRedirectUris, allowLoopbackRedirectUris);

    /// <summary>True when this requested URI matches one of a client's registered redirect URIs.</summary>
    public bool MatchesRegistered(IReadOnlyCollection<string> registeredRedirectUris, bool allowLoopbackRedirectUris)
    {
        ArgumentNullException.ThrowIfNull(registeredRedirectUris);
        return MatchesRegistered(registeredRedirectUris, Value, allowLoopbackRedirectUris);
    }

    public override string ToString() => Value;

    /// <summary>HTTPS when the host allows it, or HTTP to a loopback host when the host allows that.</summary>
    internal static bool IsAllowedForRegistration(Uri uri, bool allowHttpsRedirectUris, bool allowLoopbackRedirectUris)
    {
        if (allowHttpsRedirectUris
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return allowLoopbackRedirectUris
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && uri.IsLoopback;
    }

    /// <summary>
    /// Matches a requested redirect URI against a client's registered redirect URIs. Exact ordinal
    /// matching applies to every URI. When the requested URI is HTTP with a loopback IP-literal
    /// host, RFC 8252 §7.3 additionally requires accepting the ephemeral port chosen at
    /// authorization time, so ports are ignored on both sides while scheme, address, path, and
    /// query must still match exactly. The <c>localhost</c> hostname is deliberately excluded from
    /// port-insensitive matching because it can resolve to non-loopback addresses; native clients
    /// per the RFC use the loopback literal instead.
    /// </summary>
    internal static bool MatchesRegistered(
        IReadOnlyCollection<string> registeredRedirectUris,
        string requestedRedirectUri,
        bool allowLoopbackRedirectUris)
    {
        if (registeredRedirectUris.Contains(requestedRedirectUri, StringComparer.Ordinal))
        {
            return true;
        }

        if (!allowLoopbackRedirectUris
            || !TryParseLoopbackHttpUri(requestedRedirectUri, out var requestedAddress, out var requestedPathAndQuery))
        {
            return false;
        }

        foreach (var registeredRedirectUri in registeredRedirectUris)
        {
            if (TryParseLoopbackHttpUri(registeredRedirectUri, out var registeredAddress, out var registeredPathAndQuery)
                && registeredAddress.Equals(requestedAddress)
                && string.Equals(registeredPathAndQuery, requestedPathAndQuery, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseLoopbackHttpUri(string value, out IPAddress address, out string pathAndQuery)
    {
        address = IPAddress.None;
        pathAndQuery = string.Empty;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out var parsedAddress) || !IPAddress.IsLoopback(parsedAddress))
        {
            return false;
        }

        address = parsedAddress;
        pathAndQuery = uri.PathAndQuery;
        return true;
    }

    private Uri ToUri() => new(Value, UriKind.Absolute);
}
