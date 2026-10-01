using SqlOS.Domain;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// The 7.2.1 entry points to SqlOS's redirect URI rules, which live in <see cref="RedirectUri"/>.
/// </summary>
internal static class SqlOSRedirectUriPolicy
{
    public static bool IsAllowed(
        Uri uri,
        bool allowHttpsRedirectUris,
        bool allowLoopbackRedirectUris)
        => RedirectUri.IsAllowedForRegistration(uri, allowHttpsRedirectUris, allowLoopbackRedirectUris);

    /// <inheritdoc cref="RedirectUri.MatchesRegistered(IReadOnlyCollection{string}, string, bool)"/>
    public static bool IsRegisteredMatch(
        IReadOnlyCollection<string> registeredRedirectUris,
        string requestedRedirectUri,
        bool allowLoopbackRedirectUris)
        => RedirectUri.MatchesRegistered(registeredRedirectUris, requestedRedirectUri, allowLoopbackRedirectUris);
}
