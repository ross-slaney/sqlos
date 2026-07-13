using SqlOS.AuthServer.Configuration;

namespace SqlOS.AuthServer.Services;

internal static class SqlOSPublicOriginResolver
{
    public static string Resolve(SqlOSAuthServerOptions options)
    {
        var configuredOrigin = options.PublicOrigin?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredOrigin))
        {
            return new Uri(configuredOrigin, UriKind.Absolute)
                .GetLeftPart(UriPartial.Authority)
                .TrimEnd('/');
        }

        return new Uri(options.Issuer, UriKind.Absolute)
            .GetLeftPart(UriPartial.Authority)
            .TrimEnd('/');
    }
}
