namespace SqlOS.BehaviorLock.Host.Profiles;

/// <summary>
/// Fixed names the upgrade seed (built against the released package) and the upgrade gate
/// scenario (built against the source) share. Every run seeds a fresh database, so these need
/// no per-run suffix; per-run values (IDs, secrets, tokens) travel in <see cref="UpgradeManifest"/>.
/// </summary>
public static class UpgradeData
{
    public const string PortalClientId = "upgrade-portal";
    public const string PortalRedirectUri = "https://portal.example.test/auth/callback";
    public const string PartnerClientId = "upgrade-partner";
    public const string PartnerRedirectUri = "https://partner.example.test/callback";
    public const string CliClientId = "upgrade-cli";
    public const string DynamicClientRedirectUri = "http://127.0.0.1/callback/upgrade";
    public const string CimdClientId = "https://client.example.test/upgrade-client.json";
    public const string CimdRedirectUri = "https://client.example.test/callback";
    public const string CalendarReturnUri = "https://portal.example.test/calendar/connected";
    public const string ScimBasePath = "/scim/v2";

    public const string OrganizationName = "Acme";
    public const string OrganizationSlug = "acme";
    public const string Domain = "acme.example.test";
    public const string AliceEmail = "alice@example.test";
    public const string AlicePassword = "Upgrade-Alice-2468!";
    public const string CarolEmail = "carol@example.test";
    public const string CarolPassword = "Upgrade-Carol-2468!";
    public const string BobEmail = "bob@" + Domain;

    public const string RootWorkspaceId = "acme";
    public const string RootWorkspace = "workspace::acme";
    public const string ChildWorkspaceId = "acme-projects";
    public const string ChildWorkspace = "workspace::acme-projects";
    public const string ScimGroup = "Engineering";

    /// <summary>The client ID metadata document the fake network serves for <see cref="CimdClientId"/>.</summary>
    public const string CimdDocument = $$"""
        {
          "client_id": "{{CimdClientId}}",
          "client_name": "Upgrade Desktop",
          "redirect_uris": ["{{CimdRedirectUri}}"],
          "grant_types": ["authorization_code", "refresh_token"],
          "response_types": ["code"],
          "token_endpoint_auth_method": "none",
          "scope": "openid profile email offline_access"
        }
        """;
}
