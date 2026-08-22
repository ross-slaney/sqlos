using SqlOS.AuthServer.Contracts;

namespace SqlOS.AuthServer.Services;

public sealed record SqlOSOpenIdScopeWarning(string Code, string Message);

/// <summary>
/// Computed operator and request-time signals when <c>openid</c> is missing.
/// SqlOS issues an <c>id_token</c> only when OpenID Provider mode is enabled and
/// <c>openid</c> is granted; these warnings explain why a client or sign-in will
/// not receive one and record that <c>openid</c> is never always-allowed.
/// Shared by admin, dashboard, hosted AuthPage, and headless so the rule is not
/// reimplemented in JavaScript.
/// </summary>
public static class SqlOSOpenIdScopeWarnings
{
    public const string OpenIdScope = "openid";

    public const string MissingAllowlistedOpenIdCode = "missing_allowlisted_openid";

    public const string OmittedGrantedOpenIdCode = "omitted_granted_openid";

    public const string MissingAllowlistedOpenIdMessage =
        "This allowlist omits openid. This client cannot receive an ID token until openid is on its allowlist. openid is never granted unless it is both requested and allowlisted.";

    public const string OmittedGrantedOpenIdMessage =
        "openid was not granted, so this sign-in will not receive an ID token. It was not requested on /authorize or is not on the client allowlist. Send openid and keep it allowlisted. Omitting scope remains valid OAuth.";

    public static bool ContainsOpenId(IEnumerable<string> scopes)
        => scopes.Contains(OpenIdScope, StringComparer.Ordinal);

    public static SqlOSOpenIdScopeWarning? ForMissingAllowlistedOpenId(
        IReadOnlyCollection<string> allowedScopes,
        bool isFirstParty,
        bool allowNativeHeadlessAuth,
        bool allowDeviceAuthorization,
        IReadOnlyCollection<string> redirectUris,
        IReadOnlyCollection<string> grantTypes)
    {
        if (!IsUserFacingClient(isFirstParty, allowNativeHeadlessAuth, allowDeviceAuthorization, redirectUris, grantTypes))
        {
            return null;
        }

        if (ContainsOpenId(allowedScopes))
        {
            return null;
        }

        return new SqlOSOpenIdScopeWarning(MissingAllowlistedOpenIdCode, MissingAllowlistedOpenIdMessage);
    }

    public static SqlOSOpenIdScopeWarning? ForOmittedGrantedOpenId(
        IReadOnlyCollection<string> allowedScopes,
        string? grantedScope)
    {
        if (!ContainsOpenId(allowedScopes))
        {
            return null;
        }

        if (ContainsOpenId(SqlOSScopePolicy.Split(grantedScope)))
        {
            return null;
        }

        return new SqlOSOpenIdScopeWarning(OmittedGrantedOpenIdCode, OmittedGrantedOpenIdMessage);
    }

    public static (string? Info, bool OmittedOpenId) ApplyRequestWarning(
        string? existingInfo,
        IReadOnlyCollection<string>? allowedScopes,
        string? grantedScope)
    {
        var warning = ForOmittedGrantedOpenId(allowedScopes ?? [], grantedScope);
        return (MergeInfo(existingInfo, warning), warning != null);
    }

    public static string? MergeInfo(string? existingInfo, SqlOSOpenIdScopeWarning? warning)
    {
        if (warning == null)
        {
            return existingInfo;
        }

        if (string.IsNullOrWhiteSpace(existingInfo))
        {
            return warning.Message;
        }

        if (existingInfo.Contains(warning.Message, StringComparison.Ordinal))
        {
            return existingInfo;
        }

        return $"{existingInfo} {warning.Message}";
    }

    /// <summary>
    /// Whether the client can complete an interactive, user-facing flow. Shared by
    /// the missing-allowlist warning and the admin <c>oidcCapable</c> projections so
    /// machine-only (client-credentials) clients are never presented as OIDC-capable.
    /// </summary>
    public static bool IsUserFacingClient(
        bool isFirstParty,
        bool allowNativeHeadlessAuth,
        bool allowDeviceAuthorization,
        IReadOnlyCollection<string> redirectUris,
        IReadOnlyCollection<string> grantTypes)
        => isFirstParty
            || allowNativeHeadlessAuth
            || allowDeviceAuthorization
            || redirectUris.Count > 0
            || grantTypes.Contains(SqlOSOAuthGrantTypes.AuthorizationCode, StringComparer.Ordinal)
            || grantTypes.Contains(SqlOSOAuthGrantTypes.DeviceCode, StringComparer.Ordinal);
}
