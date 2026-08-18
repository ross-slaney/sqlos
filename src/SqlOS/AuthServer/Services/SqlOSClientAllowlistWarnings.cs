using SqlOS.AuthServer.Contracts;

namespace SqlOS.AuthServer.Services;

public sealed record SqlOSClientAllowlistWarning(string Code, string Message);

/// <summary>
/// Computed operator signal when a stored allowlist will grant nothing.
/// Shared by the admin API and dashboard so the rule is not reimplemented in JavaScript.
/// </summary>
public static class SqlOSClientAllowlistWarnings
{
    public const string EmptyAllowlistCode = "empty_allowlist";

    public const string UserFacingEmptyAllowlistMessage =
        "An empty AllowedScopes list grants no scopes. First-party and headless apps that send scope on /authorize need those values on this client. Change a code-owned seed in source control.";

    public const string MachineEmptyAllowlistMessage =
        "An empty AllowedScopes list grants no scopes on client-credentials requests.";

    public static SqlOSClientAllowlistWarning? ForEmptyAllowlist(
        IReadOnlyCollection<string> allowedScopes,
        bool isFirstParty,
        bool allowNativeHeadlessAuth,
        bool allowDeviceAuthorization,
        IReadOnlyCollection<string> redirectUris,
        IReadOnlyCollection<string> grantTypes)
    {
        if (allowedScopes.Count > 0)
        {
            return null;
        }

        if (IsUserFacingClient(isFirstParty, allowNativeHeadlessAuth, allowDeviceAuthorization, redirectUris, grantTypes))
        {
            return new SqlOSClientAllowlistWarning(EmptyAllowlistCode, UserFacingEmptyAllowlistMessage);
        }

        return new SqlOSClientAllowlistWarning(EmptyAllowlistCode, MachineEmptyAllowlistMessage);
    }

    private static bool IsUserFacingClient(
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
