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
    string? ClientId);

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
    string? OrganizationId);

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
    string ClientType = "public_pkce");

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
    DateTime UpdatedAt);

public sealed record SqlOSUpdateSecuritySettingsRequest(
    int RefreshTokenLifetimeMinutes,
    int SessionIdleTimeoutMinutes,
    int SessionAbsoluteLifetimeMinutes,
    int SigningKeyRotationIntervalDays,
    int SigningKeyGraceWindowDays,
    int SigningKeyRetiredCleanupDays);

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
    bool HeadlessCapabilityRegistered);

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

public sealed record SqlOSAuthorizationServerMetadataDto(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string JwksUri,
    string[] ResponseTypesSupported,
    string[] GrantTypesSupported,
    string[] CodeChallengeMethodsSupported,
    string[] ScopesSupported,
    string[] TokenEndpointAuthMethodsSupported);
