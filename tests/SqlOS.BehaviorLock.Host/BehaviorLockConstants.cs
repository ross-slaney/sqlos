namespace SqlOS.BehaviorLock.Host;

/// <summary>
/// Fixed origins, audiences, and fixture credentials shared by the host, the harness, and the
/// upgrade seed. They are deliberately constant: transcripts show them verbatim.
/// </summary>
public static class BehaviorLockConstants
{
    /// <summary>The host's public origin; the issuer, dashboard, and first-party app live here.</summary>
    public const string PublicOrigin = "https://sqlos.example.test";

    public const string AuthBasePath = "/sqlos/auth";

    public const string Issuer = PublicOrigin + AuthBasePath;

    public const string ApiPath = "/api";

    public const string McpPath = "/mcp";

    public const string ApiAudience = PublicOrigin + ApiPath;

    public const string McpAudience = PublicOrigin + McpPath;

    /// <summary>The separate resource API that validates SqlOS tokens with <c>AddJwtBearer</c> against JWKS.</summary>
    public const string ResourceApiAudience = "https://api.example.test";

    /// <summary>A second same-process resource registered with <c>AddSqlOSJwt</c>.</summary>
    public const string BillingAudience = "https://billing.example.test";

    public const string ResourceApiScheme = "ResourceApi";

    public const string BillingScheme = "Billing";

    /// <summary>Header that <c>Dashboard.AuthorizationCallback</c> accepts as operator authentication.</summary>
    public const string OperatorHeader = "X-BehaviorLock-Operator";

    public const string OperatorSecret = "behavior-lock-operator";

    public const string DashboardPassword = "behavior-lock-dashboard-password";

    /// <summary>The single-application client ID the hosted and headless profiles derive.</summary>
    public const string AppClientId = "behavior-lock-app";

    public const string AppRedirectUri = PublicOrigin + "/auth/callback";

    public const string ApplicationName = "Behavior Lock";

    /// <summary>The shared social/OIDC provider callback, registered on every seeded provider connection.</summary>
    public const string SocialCallbackUri = Issuer + "/oidc/callback";

    /// <summary>The host that CIMD client metadata documents are served from.</summary>
    public const string CimdClientHost = "client.example.test";
}

/// <summary>The FGA model every profile seeds, used by the library probes.</summary>
public static class BehaviorLockAuthorization
{
    public const string WorkspaceType = "workspace";

    public const string ReadPermission = "workspace.read";

    public const string WritePermission = "workspace.write";

    public const string AdminRole = "workspace_admin";

    public const string ReaderRole = "workspace_reader";
}
