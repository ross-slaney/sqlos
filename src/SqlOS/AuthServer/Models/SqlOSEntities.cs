using SqlOS.AuthServer.Contracts;

namespace SqlOS.AuthServer.Models;

public sealed class SqlOSOrganization
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? PrimaryDomain { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ICollection<SqlOSMembership> Memberships { get; set; } = new List<SqlOSMembership>();
    public ICollection<SqlOSSsoConnection> SsoConnections { get; set; } = new List<SqlOSSsoConnection>();
}

public sealed class SqlOSUser
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? DefaultEmail { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<SqlOSUserEmail> Emails { get; set; } = new List<SqlOSUserEmail>();
    public ICollection<SqlOSCredential> Credentials { get; set; } = new List<SqlOSCredential>();
    public ICollection<SqlOSMembership> Memberships { get; set; } = new List<SqlOSMembership>();
    public ICollection<SqlOSExternalIdentity> ExternalIdentities { get; set; } = new List<SqlOSExternalIdentity>();
    public ICollection<SqlOSSession> Sessions { get; set; } = new List<SqlOSSession>();
}

public sealed class SqlOSUserEmail
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public bool IsVerified { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public SqlOSUser? User { get; set; }
}

public sealed class SqlOSCredential
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Type { get; set; } = "password";
    public string SecretHash { get; set; } = string.Empty;
    public int SecretVersion { get; set; } = 1;
    public DateTime? LastUsedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public SqlOSUser? User { get; set; }
}

public sealed class SqlOSMembership
{
    public string OrganizationId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Role { get; set; } = "member";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public SqlOSOrganization? Organization { get; set; }
    public SqlOSUser? User { get; set; }
}

public sealed class SqlOSSsoConnection
{
    public string Id { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string IdentityProviderEntityId { get; set; } = string.Empty;
    public string SingleSignOnUrl { get; set; } = string.Empty;
    public string X509CertificatePem { get; set; } = string.Empty;
    public string? NameIdFormat { get; set; }
    public string EmailAttributeName { get; set; } = "email";
    public string FirstNameAttributeName { get; set; } = "first_name";
    public string LastNameAttributeName { get; set; } = "last_name";
    public bool AutoProvisionUsers { get; set; } = true;
    public bool AutoLinkByEmail { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public SqlOSOrganization? Organization { get; set; }
    public ICollection<SqlOSExternalIdentity> ExternalIdentities { get; set; } = new List<SqlOSExternalIdentity>();
}

public sealed class SqlOSOidcConnection
{
    public string Id { get; set; } = string.Empty;
    public SqlOSOidcProviderType ProviderType { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecretEncrypted { get; set; }
    public string AllowedCallbackUrisJson { get; set; } = "[]";
    public bool UseDiscovery { get; set; } = true;
    public string? DiscoveryUrl { get; set; }
    public string? Issuer { get; set; }
    public string? AuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? UserInfoEndpoint { get; set; }
    public string? JwksUri { get; set; }
    public string? MicrosoftTenant { get; set; }
    public string ScopesJson { get; set; } = "[]";
    public string ClaimMappingJson { get; set; } = "{}";
    public SqlOSOidcClientAuthMethod ClientAuthMethod { get; set; } = SqlOSOidcClientAuthMethod.ClientSecretPost;
    public bool UseUserInfo { get; set; } = true;
    public string? AppleTeamId { get; set; }
    public string? AppleKeyId { get; set; }
    public string? ApplePrivateKeyEncrypted { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<SqlOSExternalIdentity> ExternalIdentities { get; set; } = new List<SqlOSExternalIdentity>();
}

public sealed class SqlOSExternalIdentity
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string? SsoConnectionId { get; set; }
    public string? OidcConnectionId { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; }

    public SqlOSUser? User { get; set; }
    public SqlOSSsoConnection? SsoConnection { get; set; }
    public SqlOSOidcConnection? OidcConnection { get; set; }
}

public sealed class SqlOSSession
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string? AuthenticationMethod { get; set; }
    public string? ClientApplicationId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime IdleExpiresAt { get; set; }
    public DateTime AbsoluteExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }

    public SqlOSUser? User { get; set; }
    public SqlOSClientApplication? ClientApplication { get; set; }
    public ICollection<SqlOSRefreshToken> RefreshTokens { get; set; } = new List<SqlOSRefreshToken>();
}

public sealed class SqlOSRefreshToken
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string FamilyId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenId { get; set; }

    public SqlOSSession? Session { get; set; }
}

public sealed class SqlOSClientApplication
{
    public string Id { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Audience { get; set; } = "sqlos";
    public string ClientType { get; set; } = "public_pkce";
    public bool RequirePkce { get; set; } = true;
    public string AllowedScopesJson { get; set; } = "[]";
    public bool IsFirstParty { get; set; }
    public string RedirectUrisJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public sealed class SqlOSSigningKey
{
    public string Id { get; set; } = string.Empty;
    public string Kid { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "RS256";
    public string PublicKeyPem { get; set; } = string.Empty;
    public string PrivateKeyPem { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime ActivatedAt { get; set; }
    public DateTime? RetiredAt { get; set; }
}

public sealed class SqlOSTemporaryToken
{
    public string Id { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? ClientApplicationId { get; set; }
    public string? OrganizationId { get; set; }
    public string? PayloadJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

public sealed class SqlOSAuditEvent
{
    public string Id { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string? UserId { get; set; }
    public string? SessionId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string ActorType { get; set; } = string.Empty;
    public string? ActorId { get; set; }
    public DateTime OccurredAt { get; set; }
    public string? IpAddress { get; set; }
    public string? DataJson { get; set; }
}

public sealed class SqlOSSettings
{
    public string Id { get; set; } = "default";
    public int RefreshTokenLifetimeMinutes { get; set; }
    public int SessionIdleTimeoutMinutes { get; set; }
    public int SessionAbsoluteLifetimeMinutes { get; set; }
    public int SigningKeyRotationIntervalDays { get; set; } = 90;
    public int SigningKeyGraceWindowDays { get; set; } = 7;
    public int SigningKeyRetiredCleanupDays { get; set; } = 30;
    public DateTime UpdatedAt { get; set; }
}

public sealed class SqlOSAuthPageSettings
{
    public string Id { get; set; } = "default";
    public string? LogoBase64 { get; set; }
    public string PrimaryColor { get; set; } = "#2563eb";
    public string AccentColor { get; set; } = "#0f172a";
    public string BackgroundColor { get; set; } = "#f8fafc";
    public string Layout { get; set; } = "split";
    public string PageTitle { get; set; } = "Sign in";
    public string PageSubtitle { get; set; } = "Secure your app-owned AI and MCP experiences with SqlOS.";
    public bool EnablePasswordSignup { get; set; } = true;
    public string EnabledCredentialTypesJson { get; set; } = "[\"password\"]";
    public DateTime UpdatedAt { get; set; }
}

public sealed class SqlOSAuthorizationRequest
{
    public string Id { get; set; } = string.Empty;
    public string ClientApplicationId { get; set; } = string.Empty;
    public string PresentationMode { get; set; } = "hosted";
    public string? OrganizationId { get; set; }
    public string? ConnectionId { get; set; }
    public string? LoginHintEmail { get; set; }
    public string? UiContextJson { get; set; }
    public string RedirectUri { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string? Resource { get; set; }
    public string? Nonce { get; set; }
    public string? Prompt { get; set; }
    public string CodeChallenge { get; set; } = string.Empty;
    public string CodeChallengeMethod { get; set; } = "S256";
    public string? ResolvedAuthMethod { get; set; }
    public string? ResolvedOrganizationId { get; set; }
    public string? ResolvedConnectionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    public SqlOSClientApplication? ClientApplication { get; set; }
    public SqlOSOrganization? Organization { get; set; }
    public SqlOSSsoConnection? Connection { get; set; }
}

public sealed class SqlOSAuthorizationCode
{
    public string Id { get; set; } = string.Empty;
    public string AuthorizationRequestId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ClientApplicationId { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string RedirectUri { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string? Resource { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public string CodeChallenge { get; set; } = string.Empty;
    public string CodeChallengeMethod { get; set; } = "S256";
    public string AuthenticationMethod { get; set; } = "saml";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    public SqlOSAuthorizationRequest? AuthorizationRequest { get; set; }
    public SqlOSUser? User { get; set; }
    public SqlOSClientApplication? ClientApplication { get; set; }
    public SqlOSOrganization? Organization { get; set; }
}
