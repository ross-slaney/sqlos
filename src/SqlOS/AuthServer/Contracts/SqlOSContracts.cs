using System.Security.Claims;
using System.Text.Json.Serialization;

namespace SqlOS.AuthServer.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SqlOSOidcProviderType
{
    Google,
    Microsoft,
    Apple,
    GitHub,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SqlOSSocialProviderProtocol
{
    Oidc,
    OAuthProfile
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SqlOSOidcClientAuthMethod
{
    ClientSecretPost,
    ClientSecretBasic
}

public sealed class SqlOSOidcClaimMapping
{
    public string SubjectClaim { get; init; } = "sub";
    public string? EmailClaim { get; init; } = "email";
    public string? EmailVerifiedClaim { get; init; } = "email_verified";
    public string? DisplayNameClaim { get; init; } = "name";
    public string? FirstNameClaim { get; init; } = "given_name";
    public string? LastNameClaim { get; init; } = "family_name";
    public string? PreferredUsernameClaim { get; init; } = "preferred_username";
}

public sealed record SqlOSOrganizationOption(string Id, string Slug, string Name, string Role);

/// <summary>
/// One scope entry rendered on the consent screen. <see cref="DisplayName"/> comes from the
/// operator-defined scope display-name catalog and falls back to the raw scope string when
/// no catalog entry exists.
/// </summary>
public sealed record SqlOSConsentScopeDisplay(string Scope, string DisplayName, string? Description = null);

/// <summary>A remembered consent grant projected for admin and account surfaces.</summary>
public sealed record SqlOSConsentGrantSummary(
    string Id,
    string UserId,
    string ClientApplicationId,
    string ClientId,
    string ClientName,
    IReadOnlyList<string> Scopes,
    DateTime GrantedAt,
    DateTime UpdatedAt);

public sealed record SqlOSCreateScopeDisplayNameRequest(
    string Scope,
    string DisplayName,
    string? Description = null);

public sealed record SqlOSUpdateScopeDisplayNameRequest(
    string DisplayName,
    string? Description = null);

public sealed record SqlOSTokenResponse(
    string AccessToken,
    string RefreshToken,
    string SessionId,
    string ClientId,
    string? OrganizationId,
    DateTime AccessTokenExpiresAt,
    DateTime RefreshTokenExpiresAt,
    string? IdToken = null);

public sealed record SqlOSLoginResult(
    bool RequiresOrganizationSelection,
    string? PendingAuthToken,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    SqlOSTokenResponse? Tokens,
    bool RequiresMfa = false,
    string? MfaToken = null,
    bool RequiresMfaEnrollment = false,
    IReadOnlyList<string>? MfaMethods = null);

/// <summary>
/// Represents the claims and SqlOS identifiers recovered from a successfully validated access token.
/// </summary>
/// <param name="Principal">The authenticated claims principal created from the token.</param>
/// <param name="SessionId">The active SqlOS session identifier associated with the token.</param>
/// <param name="UserId">The authenticated SqlOS user identifier, when present.</param>
/// <param name="OrganizationId">The active organization identifier, when present.</param>
/// <param name="ClientId">The OAuth client identifier, when present.</param>
/// <param name="Audience">The validated token audience, when present.</param>
public sealed record SqlOSValidatedToken(
    ClaimsPrincipal Principal,
    string SessionId,
    string? UserId,
    string? OrganizationId,
    string? ClientId,
    string? Audience);

public sealed record SqlOSSignupRequest(
    string DisplayName,
    string Email,
    string Password,
    string? OrganizationName,
    string? ClientId,
    string? OrganizationId);

public sealed record SqlOSPasswordLoginRequest(
    string Email,
    string Password,
    string? ClientId,
    string? OrganizationId);

public sealed record SqlOSSelectOrganizationRequest(
    string PendingAuthToken,
    string OrganizationId);

public sealed record SqlOSRefreshRequest(
    string RefreshToken,
    string? OrganizationId,
    string? Resource = null,
    string? ClientId = null);

public sealed record SqlOSForgotPasswordRequest(
    string Email,
    string? ClientId = null);

public sealed record SqlOSResetPasswordRequest(string Token, string NewPassword);

/// <summary>
/// Trusted in-process password-reset delivery request. Public password-reset endpoints bind
/// <see cref="SqlOSForgotPasswordRequest"/> and never accept <see cref="ResetUrlTemplate"/>.
/// </summary>
public sealed record SqlOSSendPasswordResetEmailRequest(
    string Email,
    string? ResetUrlTemplate = null,
    string? ClientId = null);

/// <summary>Trusted dashboard/admin password-reset delivery request.</summary>
public sealed record SqlOSSendUserPasswordResetEmailRequest(
    string? ResetUrlTemplate = null);

public sealed record SqlOSPasswordResetEmailResult(
    string Email,
    string MaskedEmail,
    DateTime ExpiresAt,
    string DeliveryId,
    string DeliveryStatus,
    string? ProviderMessageId,
    string? SanitizedError,
    string Message);

public sealed record SqlOSPasswordResetRequestResult(
    string Email,
    string MaskedEmail,
    string Message,
    DateTime ExpiresAt,
    DateTime NextAllowedSendAt);

public sealed record SqlOSCreateVerificationTokenRequest(string Email);

public sealed record SqlOSEmailVerificationRequestResult(string Message);

public sealed record SqlOSVerifyEmailRequest(string Token);

public sealed record SqlOSCreateOrganizationRequest(string Name, string? Slug, string? PrimaryDomain = null);

public sealed record SqlOSUpdateOrganizationRequest(string Name, string? Slug, string? PrimaryDomain = null, bool IsActive = true);

public sealed record SqlOSCreateMembershipRequest(string UserId, string Role);

public sealed record SqlOSCreateUserRequest(string DisplayName, string Email, string? Password);

public sealed record SqlOSCreateClientRequest(
    string ClientId,
    string Name,
    string Audience,
    List<string> RedirectUris,
    string? Description = null,
    List<string>? AllowedScopes = null,
    bool RequirePkce = true,
    bool IsFirstParty = false,
    bool AllowNativeHeadlessAuth = false,
    bool AllowDeviceAuthorization = false,
    string ClientType = "public_pkce");

public static class SqlOSApplicationAccessModes
{
    public const string AllOrganizations = "all_organizations";
    public const string SelectedOrganizations = "selected_organizations";
    public const string SelectedUsersGroupsRoles = "selected_users_groups_roles";
    public const string InternalOnly = "internal_only";
    public const string Disabled = "disabled";
}

public static class SqlOSApplicationAssignmentPrincipalTypes
{
    public const string Organization = "organization";
    public const string User = "user";
    public const string Group = "group";
    public const string Role = "role";
    public const string ServiceAccount = "service_account";
    public const string Agent = "agent";
}

public static class SqlOSApplicationAssignmentAccess
{
    public const string Allowed = "allowed";
    public const string Denied = "denied";
}

public sealed record SqlOSSetApplicationAccessModeRequest(string AccessMode);

public sealed record SqlOSCreateApplicationAssignmentRequest(
    string PrincipalType,
    string? PrincipalId = null,
    string? OrganizationId = null,
    string? RoleKey = null,
    string Access = SqlOSApplicationAssignmentAccess.Allowed,
    string? Reason = null);

public sealed record SqlOSRevokeApplicationAssignmentRequest(
    string? Reason = null,
    string? ActorType = null,
    string? ActorId = null);

public sealed record SqlOSApplicationAccessCheckResult(
    bool Allowed,
    string Decision,
    string AccessMode,
    string Source,
    string? AssignmentId,
    string? Reason,
    string ClientApplicationId,
    string ClientId,
    string? OrganizationId,
    string? UserId);

public static class SqlOSOAuthGrantTypes
{
    public const string AuthorizationCode = "authorization_code";
    public const string RefreshToken = "refresh_token";
    public const string ClientCredentials = "client_credentials";
    public const string DeviceCode = "urn:ietf:params:oauth:grant-type:device_code";
}

public sealed record SqlOSDeviceAuthorizationStartRequest(
    string ClientId,
    string? Scope = null,
    string? Resource = null);

public sealed record SqlOSDeviceAuthorizationStartResult(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresIn,
    int Interval);

public sealed record SqlOSDeviceAuthorizationResolveResult(
    string Id,
    string UserCode,
    string ClientId,
    string ClientName,
    string Scope,
    string? Resource,
    DateTime ExpiresAt,
    string Status,
    bool RequiresOrganizationSelection,
    IReadOnlyList<SqlOSOrganizationOption> Organizations);

public sealed record SqlOSDeviceAuthorizationApprovalRequest(
    string UserCode,
    string? OrganizationId = null);

public sealed record SqlOSDeviceTokenPollRequest(
    string ClientId,
    string DeviceCode,
    string? Resource = null);

public sealed record SqlOSDeviceTokenPollResult(
    SqlOSTokenResponse Tokens,
    string Scope);

public sealed class SqlOSDeviceAuthorizationException : InvalidOperationException
{
    public SqlOSDeviceAuthorizationException(string error, string message, int? interval = null)
        : base(message)
    {
        Error = error;
        Interval = interval;
    }

    public string Error { get; }

    public int? Interval { get; }
}

public sealed record SqlOSDynamicClientRegistrationRequest
{
    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    [JsonPropertyName("client_name")]
    public string? ClientName { get; init; }

    [JsonPropertyName("redirect_uris")]
    public List<string> RedirectUris { get; init; } = [];

    [JsonPropertyName("grant_types")]
    public List<string>? GrantTypes { get; init; }

    [JsonPropertyName("response_types")]
    public List<string>? ResponseTypes { get; init; }

    [JsonPropertyName("token_endpoint_auth_method")]
    public string? TokenEndpointAuthMethod { get; init; }

    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; init; }

    [JsonPropertyName("client_secret_expires_at")]
    public long? ClientSecretExpiresAt { get; init; }

    [JsonPropertyName("client_uri")]
    public string? ClientUri { get; init; }

    [JsonPropertyName("logo_uri")]
    public string? LogoUri { get; init; }

    [JsonPropertyName("software_id")]
    public string? SoftwareId { get; init; }

    [JsonPropertyName("software_version")]
    public string? SoftwareVersion { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

public sealed record SqlOSDynamicClientRegistrationResponse
{
    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("client_id_issued_at")]
    public required long ClientIdIssuedAt { get; init; }

    [JsonPropertyName("client_name")]
    public required string ClientName { get; init; }

    [JsonPropertyName("redirect_uris")]
    public required string[] RedirectUris { get; init; }

    [JsonPropertyName("grant_types")]
    public required string[] GrantTypes { get; init; }

    [JsonPropertyName("response_types")]
    public required string[] ResponseTypes { get; init; }

    [JsonPropertyName("token_endpoint_auth_method")]
    public required string TokenEndpointAuthMethod { get; init; }

    [JsonPropertyName("client_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientUri { get; init; }

    [JsonPropertyName("logo_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogoUri { get; init; }

    [JsonPropertyName("software_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoftwareId { get; init; }

    [JsonPropertyName("software_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoftwareVersion { get; init; }

    /// <summary>
    /// Space-delimited registered allow-list. Always present so a client can predict
    /// later grants from the registration response alone. An omitted request
    /// <c>scope</c> registers an empty allow-list and is echoed as an empty string.
    /// </summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }
}

public sealed record SqlOSCreateSsoConnectionRequest(
    string OrganizationId,
    string DisplayName,
    string IdentityProviderEntityId,
    string SingleSignOnUrl,
    string X509CertificatePem,
    bool AutoProvisionUsers,
    bool AutoLinkByEmail,
    string? EmailAttributeName,
    string? FirstNameAttributeName,
    string? LastNameAttributeName,
    bool TrustUpstreamMfa = false,
    List<string>? AcceptedAuthnContextClassRefs = null);

public static class SqlOSScimGroupMappingMatchTypes
{
    public const string DisplayName = "display_name";
    public const string ExternalId = "external_id";
    public const string Pattern = "pattern";
}

public static class SqlOSScimSources
{
    public const string Seeded = "seeded";
    public const string Dashboard = "dashboard";
    public const string Api = "api";
}

public sealed record SqlOSCreateScimConnectionRequest(
    string OrganizationId,
    string DisplayName,
    bool Enabled = true);

public sealed record SqlOSUpdateScimConnectionRequest(
    string DisplayName,
    bool Enabled);

public sealed record SqlOSRotateScimTokenResult(
    string ConnectionId,
    string Token,
    string TokenPrefix,
    DateTime TokenRotatedAt);

public sealed record SqlOSCreateScimConnectionResult(
    string ConnectionId,
    string OrganizationId,
    string DisplayName,
    bool IsEnabled,
    string Token,
    string TokenPrefix,
    DateTime TokenRotatedAt,
    string BaseUrl,
    string UsersUrl,
    string GroupsUrl);

public sealed record SqlOSCreateScimGroupMappingRequest(
    string MatchType,
    string? GroupDisplayName,
    string? GroupExternalId,
    string? GroupPattern,
    string RoleKey,
    string? ResourceId,
    string? ResourceIdTemplate,
    string? Description = null,
    bool Enabled = true);

public sealed record SqlOSUpdateScimGroupMappingRequest(
    string MatchType,
    string? GroupDisplayName,
    string? GroupExternalId,
    string? GroupPattern,
    string RoleKey,
    string? ResourceId,
    string? ResourceIdTemplate,
    string? Description,
    bool Enabled);

public sealed record SqlOSCreateOidcConnectionRequest(
    SqlOSOidcProviderType ProviderType,
    string DisplayName,
    string ClientId,
    string? ClientSecret,
    List<string> AllowedCallbackUris,
    bool UseDiscovery,
    string? DiscoveryUrl,
    string? Issuer,
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    string? UserInfoEndpoint,
    string? JwksUri,
    string? MicrosoftTenant,
    List<string>? Scopes,
    SqlOSOidcClaimMapping? ClaimMapping,
    SqlOSOidcClientAuthMethod? ClientAuthMethod,
    bool? UseUserInfo,
    string? AppleTeamId = null,
    string? AppleKeyId = null,
    string? ApplePrivateKeyPem = null,
    string? LogoDataUrl = null,
    bool TrustUpstreamMfa = false,
    List<string>? AcceptedAmrValues = null,
    List<string>? AcceptedAcrValues = null);

public sealed record SqlOSUpdateOidcConnectionRequest(
    string DisplayName,
    string ClientId,
    string? ClientSecret,
    List<string> AllowedCallbackUris,
    bool UseDiscovery,
    string? DiscoveryUrl,
    string? Issuer,
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    string? UserInfoEndpoint,
    string? JwksUri,
    string? MicrosoftTenant,
    List<string>? Scopes,
    SqlOSOidcClaimMapping? ClaimMapping,
    SqlOSOidcClientAuthMethod? ClientAuthMethod,
    bool? UseUserInfo,
    string? AppleTeamId = null,
    string? AppleKeyId = null,
    string? ApplePrivateKeyPem = null,
    string? LogoDataUrl = null,
    bool TrustUpstreamMfa = false,
    List<string>? AcceptedAmrValues = null,
    List<string>? AcceptedAcrValues = null);

public sealed record SqlOSAuthorizationUrlRequest(
    string ConnectionId,
    string ClientId,
    string RedirectUri,
    string State,
    string CodeChallenge,
    string CodeChallengeMethod = "S256");

public sealed record SqlOSCreateWorkspaceRequest(string Name);

public sealed record SqlOSOidcProviderSummary(
    string ConnectionId,
    string ProviderType,
    string DisplayName,
    bool IsEnabled,
    string? LogoDataUrl = null,
    string Protocol = nameof(SqlOSSocialProviderProtocol.Oidc));

public sealed record SqlOSOidcAuthorizationUrlRequest(
    string ConnectionId,
    string ClientId,
    string RedirectUri,
    string State,
    string CodeChallenge,
    string CodeChallengeMethod,
    string? Email);

public sealed record SqlOSOidcAuthorizationUrlResult(
    string AuthorizationUrl,
    string ConnectionId,
    string ProviderType,
    string DisplayName);

public sealed record SqlOSHomeRealmDiscoveryRequest(string Email);

public sealed record SqlOSHomeRealmDiscoveryResult(
    string Mode,
    string? OrganizationId,
    string? OrganizationName,
    string? PrimaryDomain,
    string? ConnectionId);

public sealed record SqlOSCreateSsoConnectionDraftRequest(
    string OrganizationId,
    string DisplayName,
    string? PrimaryDomain,
    bool AutoProvisionUsers,
    bool AutoLinkByEmail);

public sealed record SqlOSImportSsoMetadataRequest(string MetadataXml);

public sealed record SqlOSSsoAuthorizationStartRequest(
    string Email,
    string ClientId,
    string RedirectUri,
    string State,
    string CodeChallenge,
    string CodeChallengeMethod);

public sealed record SqlOSSsoAuthorizationStartResult(
    string AuthorizationUrl,
    string OrganizationId,
    string OrganizationName,
    string PrimaryDomain);

public sealed record SqlOSStartOidcAuthorizationRequest(
    string ConnectionId,
    string Email,
    string ClientId,
    string CallbackUri,
    string State,
    string Nonce,
    string CodeChallenge,
    string CodeChallengeMethod)
{
    /// <summary>
    /// When the bound authorization request demands fresh authentication
    /// (max_age=0, or prompt containing login/select_account), the upstream
    /// authorize URL carries <c>prompt=login</c> so the provider cannot
    /// silently reuse an existing upstream session.
    /// </summary>
    public bool ForceFreshAuthentication { get; init; }

    /// <summary>
    /// True when the bound authorization request carried <c>max_age=0</c>;
    /// the upstream authorize URL then also carries <c>max_age=0</c>.
    /// </summary>
    public bool PropagateMaxAgeZero { get; init; }
}

public sealed record SqlOSStartOidcAuthorizationResult(
    string AuthorizationUrl,
    string ConnectionId,
    SqlOSOidcProviderType ProviderType,
    string DisplayName,
    IReadOnlyList<string> AllowedCallbackUris);

public sealed record SqlOSCompleteOidcAuthorizationRequest(
    string ConnectionId,
    string ClientId,
    string CallbackUri,
    string Code,
    string CodeVerifier,
    string Nonce,
    string? UserPayloadJson);

public sealed record SqlOSCompleteOidcAuthorizationResult(
    string ConnectionId,
    SqlOSOidcProviderType ProviderType,
    string UserId,
    string Email,
    string DisplayName,
    string? OrganizationId,
    string AuthenticationMethod,
    int OrganizationCount)
{
    public bool UserCreated { get; init; }

    /// <summary>
    /// When the user authenticated at the upstream provider, taken from the
    /// validated ID token's <c>auth_time</c> claim. Null when the provider did
    /// not assert <c>auth_time</c> or has no ID token (OAuth profile flows);
    /// callers then fall back to conservative local resolution.
    /// </summary>
    public DateTime? UpstreamAuthenticatedAt { get; init; }
}

public sealed record SqlOSPkceExchangeRequest(
    string Code,
    string ClientId,
    string RedirectUri,
    string CodeVerifier);

public sealed record SqlOSSecuritySettingsDto(
    int RefreshTokenLifetimeMinutes,
    int SessionIdleTimeoutMinutes,
    int SessionAbsoluteLifetimeMinutes,
    int SigningKeyRotationIntervalDays,
    int SigningKeyGraceWindowDays,
    int SigningKeyRetiredCleanupDays,
    int RefreshTokenGraceWindowSeconds,
    DateTime UpdatedAt);

public sealed record SqlOSUpdateSecuritySettingsRequest(
    int RefreshTokenLifetimeMinutes,
    int SessionIdleTimeoutMinutes,
    int SessionAbsoluteLifetimeMinutes,
    int SigningKeyRotationIntervalDays,
    int SigningKeyGraceWindowDays,
    int SigningKeyRetiredCleanupDays,
    int RefreshTokenGraceWindowSeconds = 30);

public sealed record SqlOSAuthPageSettingsDto(
    string? LogoBase64,
    string PrimaryColor,
    string AccentColor,
    string BackgroundColor,
    string Layout,
    string PageTitle,
    string PageSubtitle,
    bool EnablePasswordSignup,
    string[] EnabledCredentialTypes,
    DateTime UpdatedAt,
    bool ManagedByStartupSeed,
    bool HeadlessCapabilityRegistered,
    bool LocalPasswordRuntimeEnabled,
    bool EmailOtpRuntimeConfigured,
    bool MagicLinkRuntimeConfigured = false,
    bool PhoneOtpRuntimeConfigured = false,
    SqlOSConfigurationOwnershipDto? Ownership = null);

public sealed record SqlOSAuthEmailBrandingSettingsDto(
    string ApplicationName,
    string? LogoBase64,
    string PrimaryColor,
    string AccentColor,
    string BackgroundColor,
    DateTime UpdatedAt,
    bool ManagedByStartupSeed,
    SqlOSConfigurationOwnershipDto? Ownership = null);

public sealed record SqlOSUpdateAuthPageSettingsRequest(
    string? LogoBase64,
    string PrimaryColor,
    string AccentColor,
    string BackgroundColor,
    string Layout,
    string PageTitle,
    string PageSubtitle,
    bool EnablePasswordSignup,
    string[] EnabledCredentialTypes);

public sealed record SqlOSUpdateAuthEmailBrandingSettingsRequest(
    string ApplicationName,
    string? LogoBase64,
    string PrimaryColor,
    string AccentColor,
    string BackgroundColor);

public sealed record SqlOSAuthorizationServerMetadataDto
{
    [JsonPropertyName("issuer")]
    public required string Issuer { get; init; }

    [JsonPropertyName("authorization_endpoint")]
    public required string AuthorizationEndpoint { get; init; }

    [JsonPropertyName("token_endpoint")]
    public required string TokenEndpoint { get; init; }

    [JsonPropertyName("device_authorization_endpoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceAuthorizationEndpoint { get; init; }

    [JsonPropertyName("jwks_uri")]
    public required string JwksUri { get; init; }

    [JsonPropertyName("response_types_supported")]
    public required string[] ResponseTypesSupported { get; init; }

    [JsonPropertyName("response_modes_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? ResponseModesSupported { get; init; }

    [JsonPropertyName("request_parameter_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequestParameterSupported { get; init; }

    [JsonPropertyName("request_uri_parameter_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequestUriParameterSupported { get; init; }

    [JsonPropertyName("grant_types_supported")]
    public required string[] GrantTypesSupported { get; init; }

    [JsonPropertyName("code_challenge_methods_supported")]
    public required string[] CodeChallengeMethodsSupported { get; init; }

    [JsonPropertyName("scopes_supported")]
    public required string[] ScopesSupported { get; init; }

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public required string[] TokenEndpointAuthMethodsSupported { get; init; }

    [JsonPropertyName("registration_endpoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegistrationEndpoint { get; init; }

    [JsonPropertyName("client_id_metadata_document_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ClientIdMetadataDocumentSupported { get; init; }

    [JsonPropertyName("resource_parameter_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ResourceParameterSupported { get; init; }

    [JsonPropertyName("userinfo_endpoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserInfoEndpoint { get; init; }

    [JsonPropertyName("subject_types_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? SubjectTypesSupported { get; init; }

    [JsonPropertyName("id_token_signing_alg_values_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? IdTokenSigningAlgValuesSupported { get; init; }

    [JsonPropertyName("claims_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? ClaimsSupported { get; init; }

    [JsonPropertyName("end_session_endpoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndSessionEndpoint { get; init; }
}
