using System.Security.Claims;
using System.Text.Json.Serialization;

namespace SqlOS.AuthServer.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SqlOSOidcProviderType
{
    Google,
    Microsoft,
    Apple,
    Custom
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

public sealed record SqlOSTokenResponse(
    string AccessToken,
    string RefreshToken,
    string SessionId,
    string ClientId,
    string? OrganizationId,
    DateTime AccessTokenExpiresAt,
    DateTime RefreshTokenExpiresAt);

public sealed record SqlOSLoginResult(
    bool RequiresOrganizationSelection,
    string? PendingAuthToken,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    SqlOSTokenResponse? Tokens);

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
    string? Resource = null);

public sealed record SqlOSExchangeCodeRequest(
    string Code,
    string ClientId);

public sealed record SqlOSForgotPasswordRequest(string Email);

public sealed record SqlOSResetPasswordRequest(string Token, string NewPassword);

public sealed record SqlOSCreateVerificationTokenRequest(string Email);

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
    string? LastNameAttributeName);

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
    string? LogoDataUrl = null);

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
    string? LogoDataUrl = null);

public sealed record SqlOSAuthorizationUrlRequest(string ConnectionId, string ClientId, string RedirectUri);

public sealed record SqlOSCreateWorkspaceRequest(string Name);

public sealed record SqlOSOidcProviderSummary(
    string ConnectionId,
    string ProviderType,
    string DisplayName,
    bool IsEnabled,
    string? LogoDataUrl = null);

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
    string CodeChallengeMethod);

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
    int OrganizationCount);

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
    bool EmailOtpRuntimeConfigured);

public sealed record SqlOSAuthEmailBrandingSettingsDto(
    string ApplicationName,
    string? LogoBase64,
    string PrimaryColor,
    string AccentColor,
    string BackgroundColor,
    DateTime UpdatedAt,
    bool ManagedByStartupSeed);

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
}
