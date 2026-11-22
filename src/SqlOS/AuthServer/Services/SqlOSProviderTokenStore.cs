using System.Text.Json;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Writes and clears the provider tokens kept on an external identity. Tokens are protected with
/// <see cref="SqlOSCryptoService.ProtectSecret"/>, the same Data Protection purpose Calendar uses
/// for its provider tokens. Nothing here logs or audits a token.
/// </summary>
internal static class SqlOSProviderTokenStore
{
    public static bool HasTokens(SqlOSExternalIdentity identity)
        => identity.ProviderAccessTokenEncrypted != null
            || identity.ProviderRefreshTokenEncrypted != null
            || identity.ProviderAccessTokenExpiresAt != null
            || identity.ProviderGrantedScopesJson != null;

    /// <summary>
    /// Stores a token response. A response without a refresh token keeps the stored one: providers
    /// such as Google issue a refresh token only on first consent, and many refresh responses omit it.
    /// </summary>
    public static void Store(
        SqlOSExternalIdentity identity,
        SqlOSCryptoService cryptoService,
        string accessToken,
        string? refreshToken,
        DateTime? expiresAt,
        IReadOnlyList<string> grantedScopes,
        DateTime now)
    {
        identity.ProviderAccessTokenEncrypted = cryptoService.ProtectSecret(accessToken);
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            identity.ProviderRefreshTokenEncrypted = cryptoService.ProtectSecret(refreshToken);
        }

        identity.ProviderAccessTokenExpiresAt = expiresAt;
        identity.ProviderGrantedScopesJson = JsonSerializer.Serialize(grantedScopes);
        identity.ProviderTokensUpdatedAt = now;
    }

    public static void Clear(SqlOSExternalIdentity identity)
    {
        identity.ProviderAccessTokenEncrypted = null;
        identity.ProviderRefreshTokenEncrypted = null;
        identity.ProviderAccessTokenExpiresAt = null;
        identity.ProviderGrantedScopesJson = null;
        identity.ProviderTokensUpdatedAt = null;
    }

    public static IReadOnlyList<string> ReadScopes(SqlOSExternalIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.ProviderGrantedScopesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(identity.ProviderGrantedScopesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads the token response's <c>scope</c>. RFC 6749 §5.1 separates scopes with spaces; GitHub
    /// separates them with commas. When the response omits it, the requested scopes were granted.
    /// </summary>
    public static IReadOnlyList<string> ParseGrantedScopes(string? scope, IReadOnlyList<string> requestedScopes)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return requestedScopes;
        }

        return scope
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
