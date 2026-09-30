using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>The clients the <c>multi-app</c> profile seeds (see <c>HostProfiles</c>).</summary>
internal static class AtlasClients
{
    public const string Portal = "atlas-portal";
    public const string PortalRedirectUri = "https://portal.example.test/auth/callback";

    public const string OpsConsole = "atlas-ops-console";
    public const string OpsConsoleRedirectUri = "https://ops.example.test/auth/callback";

    public const string Partner = "atlas-partner";
    public const string PartnerRedirectUri = "https://partner.example.test/callback";

    public const string Confidential = "atlas-confidential";
    public const string ConfidentialRedirectUri = "https://web.example.test/signin-oidc";

    public const string Cli = "atlas-cli";

    public const string Worker = "atlas-worker";

    public const string ConfidentialSecret = HostProfiles.ConfidentialClientSecret;

    public const string WorkerSecret = HostProfiles.MachineClientSecret;

    /// <summary>An authorization request for the first-party portal that opens the hosted password page.</summary>
    public static AuthorizationRequest PortalPasswordRequest(Transcript t, string scope = "openid profile email offline_access")
        => t.Urls.Authorize(Portal, PortalRedirectUri, scope, new Dictionary<string, string?> { ["view"] = "password" });
}
