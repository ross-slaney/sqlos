using SqlOS.AuthServer.Contracts;

namespace SqlOS.AuthServer.Configuration;

/// <summary>
/// Configures the first-party client, redirect URI, scopes, credentials, and branding used by
/// single-application hosting mode.
/// </summary>
public sealed class SqlOSSingleApplicationOptions
{
    /// <summary>Gets or sets the application display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute application origin used to derive the default redirect URI.
    /// </summary>
    public string? Origin { get; set; }

    /// <summary>Gets or sets the OAuth client ID. When omitted, SqlOS derives it from <see cref="Name"/>.</summary>
    public string? ClientId { get; set; }

    /// <summary>Gets or sets the access-token audience. When omitted, the client ID is used.</summary>
    public string? Audience { get; set; }

    /// <summary>Gets or sets the callback path appended to <see cref="Origin"/>.</summary>
    public string RedirectPath { get; set; } = "/auth/callback";

    /// <summary>Gets the explicit absolute redirect URIs allowed for the client.</summary>
    public List<string> RedirectUris { get; } = [];

    /// <summary>Gets or sets the OAuth scopes allowed for the client.</summary>
    public List<string> AllowedScopes { get; set; } = ["openid", "profile", "email", "offline_access"];

    /// <summary>Gets or sets whether the hosted sign-in page allows password sign-up.</summary>
    public bool EnablePasswordSignup { get; set; } = true;

    /// <summary>Gets or sets the credential types enabled on the hosted sign-in page.</summary>
    public List<string> EnabledCredentialTypes { get; set; } = ["password"];

    /// <summary>Gets or sets whether the application name and credential settings configure the hosted sign-in page.</summary>
    public bool ConfigureAuthPageBranding { get; set; } = true;

    /// <summary>Gets or sets whether the application name configures transactional email branding.</summary>
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
    /// <summary>Enables OAuth client credentials for this confidential, non-browser client.</summary>
    public bool EnableClientCredentials { get; set; }
    /// <summary>Resolves the confidential OAuth client's secret from the host secret provider.</summary>
    public Func<string?>? ClientSecretResolver { get; set; }
    /// <summary>Alternatively resolves an ASP.NET Core PasswordHasher-compatible client-secret hash.</summary>
    public Func<string?>? ClientSecretHashResolver { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>
    /// Controls which organizations and principals may use this application. When omitted, new
    /// clients use <c>all_organizations</c> and existing clients retain their current access mode.
    /// </summary>
    public string? AccessMode { get; set; }
    /// <summary>Gets the ownership-safe application access assignments reconciled for this client.</summary>
    public List<SqlOSApplicationAssignmentSeedOptions> Assignments { get; } = [];
    /// <summary>Optional unified OAuth client and FGA service-account declaration.</summary>
    public SqlOSMachineClientSeedOptions? MachineClient { get; set; }

    public SqlOSClientSeedOptions AssignOrganization(string key, string organizationIdOrSlug, string access = SqlOSApplicationAssignmentAccess.Allowed, string? description = null)
        => Assign(key, SqlOSApplicationAssignmentPrincipalTypes.Organization, organizationIdOrSlug: organizationIdOrSlug, access: access, description: description);

    public SqlOSClientSeedOptions AssignUser(string key, string userId, string? organizationIdOrSlug = null, string access = SqlOSApplicationAssignmentAccess.Allowed, string? description = null)
        => Assign(key, SqlOSApplicationAssignmentPrincipalTypes.User, principalId: userId, organizationIdOrSlug: organizationIdOrSlug, access: access, description: description);

    public SqlOSClientSeedOptions AssignGroup(string key, string groupId, string? organizationIdOrSlug = null, string access = SqlOSApplicationAssignmentAccess.Allowed, string? description = null)
        => Assign(key, SqlOSApplicationAssignmentPrincipalTypes.Group, principalId: groupId, organizationIdOrSlug: organizationIdOrSlug, access: access, description: description);

    public SqlOSClientSeedOptions AssignRole(string key, string organizationIdOrSlug, string roleKey, string access = SqlOSApplicationAssignmentAccess.Allowed, string? description = null)
        => Assign(key, SqlOSApplicationAssignmentPrincipalTypes.Role, organizationIdOrSlug: organizationIdOrSlug, roleKey: roleKey, access: access, description: description);

    public SqlOSClientSeedOptions AssignServiceAccount(string key, string serviceAccountId, string? organizationIdOrSlug = null, string access = SqlOSApplicationAssignmentAccess.Allowed, string? description = null)
        => Assign(key, SqlOSApplicationAssignmentPrincipalTypes.ServiceAccount, principalId: serviceAccountId, organizationIdOrSlug: organizationIdOrSlug, access: access, description: description);

    public SqlOSClientSeedOptions AssignAgent(string key, string agentId, string? organizationIdOrSlug = null, string access = SqlOSApplicationAssignmentAccess.Allowed, string? description = null)
        => Assign(key, SqlOSApplicationAssignmentPrincipalTypes.Agent, principalId: agentId, organizationIdOrSlug: organizationIdOrSlug, access: access, description: description);

    private SqlOSClientSeedOptions Assign(string key, string principalType, string? principalId = null, string? organizationIdOrSlug = null, string? roleKey = null, string access = SqlOSApplicationAssignmentAccess.Allowed, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Seeded application assignments require a stable key.");
        Assignments.Add(new SqlOSApplicationAssignmentSeedOptions
        {
            Key = key.Trim(),
            PrincipalType = principalType,
            PrincipalId = principalId,
            OrganizationIdOrSlug = organizationIdOrSlug,
            RoleKey = roleKey,
            Access = access,
            Description = description
        });
        return this;
    }
}

public sealed class SqlOSMachineClientSeedOptions
{
    public string? OrganizationId { get; set; }
    public string? OrganizationSlug { get; set; }
    public DateTime? ExpiresAt { get; set; }
    /// <summary>Resolves secret material from the host secret provider. Never commit the returned value.</summary>
    public Func<string?>? SecretResolver { get; set; }
    /// <summary>Alternatively resolves an ASP.NET Core PasswordHasher-compatible hash.</summary>
    public Func<string?>? SecretHashResolver { get; set; }
    public List<SqlOSMachineClientGrantSeedOptions> Grants { get; } = [];

    public SqlOSMachineClientSeedOptions Grant(string resourceId, string roleId, string? description = null)
    {
        Grants.Add(new SqlOSMachineClientGrantSeedOptions(resourceId, roleId, description));
        return this;
    }
}

public sealed record SqlOSMachineClientGrantSeedOptions(string ResourceId, string RoleId, string? Description = null);

/// <summary>Declarative, stable-keyed assignment owned by a client seed.</summary>
public sealed class SqlOSApplicationAssignmentSeedOptions
{
    public string Key { get; set; } = string.Empty;
    public string PrincipalType { get; set; } = string.Empty;
    public string? PrincipalId { get; set; }
    /// <summary>An organization stable ID or slug, resolved during startup.</summary>
    public string? OrganizationIdOrSlug { get; set; }
    public string? RoleKey { get; set; }
    public string Access { get; set; } = SqlOSApplicationAssignmentAccess.Allowed;
    public string? Description { get; set; }
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
    /// <summary>Stable source key used to reconcile this connection across renames.</summary>
    public string? Key { get; set; }
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
    public bool TrustUpstreamMfa { get; set; }
    public List<string> AcceptedAmrValues { get; } = [];
    public List<string> AcceptedAcrValues { get; } = [];

    /// <summary>
    /// Whether the connection should be enabled when first seeded. After the connection exists,
    /// manual enable/disable from the dashboard is preserved across restarts.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}

public sealed class SqlOSScimConnectionSeedOptions
{
    public string Key { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string? OrganizationSlug { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string? Token { get; set; }
    public string? TokenSecretName { get; set; }
    public List<SqlOSScimGroupMappingSeedOptions> GroupMappings { get; } = [];

    public SqlOSScimConnectionSeedOptions MapGroup(string displayName, Action<SqlOSScimGroupMappingSeedOptions> configure)
    {
        var mapping = new SqlOSScimGroupMappingSeedOptions
        {
            SourceKey = $"name:{displayName}",
            MatchType = SqlOSScimGroupMappingMatchTypes.DisplayName,
            GroupDisplayName = displayName
        };
        configure(mapping);
        GroupMappings.Add(mapping);
        return this;
    }

    public SqlOSScimConnectionSeedOptions MapGroupExternalId(string externalId, Action<SqlOSScimGroupMappingSeedOptions> configure)
    {
        var mapping = new SqlOSScimGroupMappingSeedOptions
        {
            SourceKey = $"external:{externalId}",
            MatchType = SqlOSScimGroupMappingMatchTypes.ExternalId,
            GroupExternalId = externalId
        };
        configure(mapping);
        GroupMappings.Add(mapping);
        return this;
    }

    public SqlOSScimConnectionSeedOptions MapGroupPattern(string pattern, Action<SqlOSScimGroupMappingSeedOptions> configure)
    {
        var mapping = new SqlOSScimGroupMappingSeedOptions
        {
            SourceKey = $"pattern:{pattern}",
            MatchType = SqlOSScimGroupMappingMatchTypes.Pattern,
            GroupPattern = pattern
        };
        configure(mapping);
        GroupMappings.Add(mapping);
        return this;
    }
}

/// <summary>Declarative upstream SAML connection reconciled by a stable source key.</summary>
public sealed class SqlOSSamlConnectionSeedOptions
{
    public string Key { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string? OrganizationSlug { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Optional federation metadata XML. Use this or the three explicit IdP fields below.</summary>
    public string? MetadataXml { get; set; }
    public string? IdentityProviderEntityId { get; set; }
    public string? SingleSignOnUrl { get; set; }
    public string? X509CertificatePem { get; set; }
    public string? PrimaryDomain { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool AutoProvisionUsers { get; set; } = true;
    public bool AutoLinkByEmail { get; set; }
    public string? NameIdFormat { get; set; }
    public string EmailAttributeName { get; set; } = "email";
    public string FirstNameAttributeName { get; set; } = "first_name";
    public string LastNameAttributeName { get; set; } = "last_name";
    public bool TrustUpstreamMfa { get; set; }
    public List<string> AcceptedAuthnContextClassRefs { get; } = [];
}

public sealed class SqlOSScimGroupMappingSeedOptions
{
    public string SourceKey { get; set; } = string.Empty;
    public string MatchType { get; set; } = SqlOSScimGroupMappingMatchTypes.DisplayName;
    public string? GroupDisplayName { get; set; }
    public string? GroupExternalId { get; set; }
    public string? GroupPattern { get; set; }
    public string RoleKey { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string? ResourceIdTemplate { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Code-first seed for one consent-screen scope display name. Reconciled on startup by
/// <c>SqlOSAdminService.UpsertSeededScopeDisplayNamesAsync</c> with the scope string as the
/// stable configuration source key.
/// </summary>
public sealed class SqlOSScopeDisplaySeedOptions
{
    public string Scope { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
}
