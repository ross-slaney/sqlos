using SqlOS.AuthServer.Contracts;

namespace SqlOS.AuthServer.Services;

internal static class SqlOSOidcProviderLogoCatalog
{
    private static readonly IReadOnlyDictionary<SqlOSOidcProviderType, string> BuiltInLogoDataUrls =
        new Dictionary<SqlOSOidcProviderType, string>
        {
            [SqlOSOidcProviderType.Google] = CreateSvgDataUrl(
                """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <path fill="#EA4335" d="M12 10.2v3.9h5.4c-.22 1.26-.93 2.32-1.95 3.03l3.15 2.44c1.84-1.69 2.9-4.18 2.9-7.15 0-.66-.06-1.3-.18-1.92H12z" />
                  <path fill="#34A853" d="M12 22c2.7 0 4.96-.9 6.61-2.44l-3.15-2.44c-.87.59-1.99.94-3.46.94-2.66 0-4.92-1.79-5.73-4.19l-3.25 2.5C4.66 19.78 8.06 22 12 22z" />
                  <path fill="#FBBC05" d="M6.27 13.87c-.21-.59-.33-1.23-.33-1.87s.12-1.28.33-1.87l-3.25-2.5C2.37 8.93 2 10.43 2 12s.37 3.07 1.02 4.37l3.25-2.5z" />
                  <path fill="#4285F4" d="M12 5.94c1.47 0 2.79.51 3.83 1.5l2.87-2.87C16.95 2.94 14.69 2 12 2 8.06 2 4.66 4.22 3.02 7.63l3.25 2.5c.81-2.4 3.07-4.19 5.73-4.19z" />
                </svg>
                """),
            [SqlOSOidcProviderType.Microsoft] = CreateSvgDataUrl(
                """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <rect x="2" y="2" width="9" height="9" fill="#F25022" />
                  <rect x="13" y="2" width="9" height="9" fill="#7FBA00" />
                  <rect x="2" y="13" width="9" height="9" fill="#00A4EF" />
                  <rect x="13" y="13" width="9" height="9" fill="#FFB900" />
                </svg>
                """),
            [SqlOSOidcProviderType.Apple] = CreateSvgDataUrl(
                """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <path fill="#111827" d="M16.365 1.43c0 1.14-.426 2.233-1.278 3.279-.869 1.044-1.914 1.648-3.138 1.523-.016-.136-.024-.272-.024-.41 0-1.088.473-2.257 1.42-3.507.473-.62 1.074-1.134 1.802-1.54.727-.398 1.415-.618 2.064-.659.016.145.024.29.024.434Zm4.345 16.998c-.345 1.06-.8 2.047-1.366 2.96-.771 1.248-1.4 2.112-1.888 2.592-.755.788-1.562 1.19-2.423 1.208-.617 0-1.366-.177-2.246-.532-.883-.354-1.694-.532-2.432-.532-.771 0-1.598.177-2.48.532-.884.355-1.595.54-2.137.556-.828.033-1.655-.377-2.48-1.232-.526-.46-1.183-1.352-1.973-2.675-.847-1.407-1.544-3.037-2.088-4.892C.292 14.886 0 12.945 0 11.07c0-2.148.463-4.002 1.39-5.56.727-1.25 1.697-2.236 2.912-2.96 1.215-.73 2.527-1.1 3.938-1.117.617 0 1.426.19 2.432.568 1.002.38 1.646.57 1.932.57.215 0 .94-.227 2.173-.678 1.166-.419 2.149-.593 2.949-.52 2.168.177 3.796 1.028 4.88 2.556-1.937 1.173-2.898 2.814-2.882 4.916.016 1.639.612 2.999 1.79 4.08.534.5 1.13.887 1.791 1.16-.145.42-.296.862-.46 1.329Z" />
                </svg>
                """)
        };

    public static string? NormalizeCustomLogoDataUrl(string? logoDataUrl)
        => string.IsNullOrWhiteSpace(logoDataUrl)
            ? null
            : logoDataUrl.Trim();

    public static string? ResolveEffectiveLogoDataUrl(SqlOSOidcProviderType providerType, string? customLogoDataUrl)
    {
        var customLogo = NormalizeCustomLogoDataUrl(customLogoDataUrl);
        if (!string.IsNullOrWhiteSpace(customLogo))
        {
            return customLogo;
        }

        return BuiltInLogoDataUrls.TryGetValue(providerType, out var logoDataUrl)
            ? logoDataUrl
            : null;
    }

    private static string CreateSvgDataUrl(string svg)
        => $"data:image/svg+xml;charset=utf-8,{Uri.EscapeDataString(svg.ReplaceLineEndings(string.Empty).Trim())}";
}
