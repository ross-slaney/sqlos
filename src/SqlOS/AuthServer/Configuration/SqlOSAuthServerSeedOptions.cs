using SqlOS.AuthServer.Contracts;

namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSSingleApplicationOptions
{
    public string Name { get; set; } = string.Empty;
    public string? Origin { get; set; }
    public string? ClientId { get; set; }
    public string? Audience { get; set; }
    public string RedirectPath { get; set; } = "/auth/callback";
    public List<string> RedirectUris { get; } = [];
    public List<string> AllowedScopes { get; set; } = ["openid", "profile", "email", "offline_access"];
    public bool EnablePasswordSignup { get; set; } = true;
    public List<string> EnabledCredentialTypes { get; set; } = ["password"];
    public bool ConfigureAuthPageBranding { get; set; } = true;
    public bool ConfigureEmailBranding { get; set; } = true;
}

public sealed class SqlOSAuthPageSeedOptions
{
    public string? LogoBase64 { get; set; }
    public string PrimaryColor { get; set; } = "#2563eb";
    public string AccentColor { get; set; } = "#0f172a";
    public string BackgroundColor { get; set; } = "#f8fafc";
    public string Layout { get; set; } = "split";
    public string PageTitle { get; set; } = "Sign in";
    public string PageSubtitle { get; set; } = "Secure your app-owned AI and MCP experiences with SqlOS.";
    public bool EnablePasswordSignup { get; set; } = true;
    public List<string> EnabledCredentialTypes { get; set; } = ["password"];
}

public sealed class SqlOSAuthEmailSeedOptions
{
    public string ApplicationName { get; set; } = "SqlOS";
    public string? LogoBase64 { get; set; }
    public string PrimaryColor { get; set; } = "#2563eb";
    public string AccentColor { get; set; } = "#0f172a";
    public string BackgroundColor { get; set; } = "#f8fafc";
}

public sealed class SqlOSMfaSeedOptions
{
    public bool Enabled { get; set; } = true;
    public bool TotpEnabled { get; set; } = true;
    public bool UserSelfEnrollmentEnabled { get; set; } = true;
    public bool RecoveryCodesEnabled { get; set; } = true;
    public bool RequireForAllUsers { get; set; }
    public bool RequireForOwnersAndAdmins { get; set; }
    public List<string> RequiredRoles { get; set; } = ["owner", "admin"];
    public List<string> AvailableFactors { get; set; } = ["totp", "recovery_code"];
    public List<SqlOSOrganizationMfaPolicySeedOptions> Organizations { get; } = [];
}

public sealed class SqlOSOrganizationMfaPolicySeedOptions
{
    public string OrganizationId { get; set; } = string.Empty;
    public string? OrganizationSlug { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool RequireMfaForAllUsers { get; set; }
    public bool RequireMfaForOwnersAndAdmins { get; set; }
    public bool UserSelfEnrollmentEnabled { get; set; } = true;
    public bool RecoveryCodesEnabled { get; set; } = true;
    public List<string> RequiredRoles { get; set; } = ["owner", "admin"];
    public List<string> AvailableFactors { get; set; } = ["totp", "recovery_code"];
}

public sealed class SqlOSClientSeedOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Audience { get; set; }
    public string ClientType { get; set; } = "public_pkce";
    public bool RequirePkce { get; set; } = true;
    public List<string> AllowedScopes { get; set; } = [];
    public List<string> RedirectUris { get; set; } = [];
    public bool IsFirstParty { get; set; }
    public bool AllowNativeHeadlessAuth { get; set; }
    public bool AllowDeviceAuthorization { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Declarative seed for a social/OIDC login connection (Google, Microsoft, Apple, or custom).
/// Seeds are reconciled into the database on startup, matched by <see cref="ProviderType"/>
/// (and <see cref="DisplayName"/> for <see cref="SqlOSOidcProviderType.Custom"/>).
/// Callback URIs may contain the <c>{connectionId}</c> placeholder, which is replaced with the
/// generated connection id so the SqlOS-owned callback URL can be seeded without knowing the id up front.
/// </summary>
public sealed class SqlOSOidcConnectionSeedOptions
{
    public SqlOSOidcProviderType ProviderType { get; set; } = SqlOSOidcProviderType.Custom;
    public string DisplayName { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Allowed callback URIs. Supports the <c>{connectionId}</c> placeholder, which is replaced
    /// with the generated connection id at seed time.
    /// </summary>
    public List<string> AllowedCallbackUris { get; set; } = [];

    public bool UseDiscovery { get; set; } = true;
    public string? DiscoveryUrl { get; set; }
    public string? Issuer { get; set; }
    public string? AuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? UserInfoEndpoint { get; set; }
    public string? JwksUri { get; set; }

    /// <summary>Azure AD tenant id (Microsoft only). Defaults to <c>common</c> when omitted.</summary>
    public string? MicrosoftTenant { get; set; }

    public List<string>? Scopes { get; set; }
    public SqlOSOidcClaimMapping? ClaimMapping { get; set; }
    public SqlOSOidcClientAuthMethod? ClientAuthMethod { get; set; }
    public bool? UseUserInfo { get; set; }
    public string? AppleTeamId { get; set; }
    public string? AppleKeyId { get; set; }
    public string? ApplePrivateKeyPem { get; set; }
    public string? LogoDataUrl { get; set; }

    /// <summary>
    /// Whether the connection should be enabled when first seeded. After the connection exists,
    /// manual enable/disable from the dashboard is preserved across restarts.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}
