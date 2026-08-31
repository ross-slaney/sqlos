using System.Net;

namespace SqlOS.AuthServer.Services;

internal static class SqlOSRedirectUriPolicy
{
    public static bool IsAllowed(
        Uri uri,
        bool allowHttpsRedirectUris,
        bool allowLoopbackRedirectUris)
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
    /// Matches a requested redirect URI against a client's registered redirect URIs.
    /// Exact ordinal matching applies to every URI. When the requested URI is HTTP with a
    /// loopback IP-literal host, RFC 8252 §7.3 additionally requires accepting the ephemeral
    /// port chosen at authorization time, so ports are ignored on both sides while scheme,
    /// address, path, and query must still match exactly. The <c>localhost</c> hostname is
    /// deliberately excluded from port-insensitive matching because it can resolve to
    /// non-loopback addresses; native clients per the RFC use the loopback literal instead.
    /// </summary>
    public static bool IsRegisteredMatch(
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
}
