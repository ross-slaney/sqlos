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
}
