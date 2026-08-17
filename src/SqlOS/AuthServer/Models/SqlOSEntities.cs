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
    public ICollection<SqlOSScimConnection> ScimConnections { get; set; } = new List<SqlOSScimConnection>();
    public ICollection<SqlOSOrganizationDomain> Domains { get; set; } = new List<SqlOSOrganizationDomain>();
    public ICollection<SqlOSApplicationAssignment> ApplicationAssignments { get; set; } = new List<SqlOSApplicationAssignment>();
    public SqlOSOrganizationMfaPolicy? MfaPolicy { get; set; }
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
    public ICollection<SqlOSUserPhoneNumber> PhoneNumbers { get; set; } = new List<SqlOSUserPhoneNumber>();
    public ICollection<SqlOSCredential> Credentials { get; set; } = new List<SqlOSCredential>();
    public ICollection<SqlOSMembership> Memberships { get; set; } = new List<SqlOSMembership>();
    public ICollection<SqlOSExternalIdentity> ExternalIdentities { get; set; } = new List<SqlOSExternalIdentity>();
    public ICollection<SqlOSSession> Sessions { get; set; } = new List<SqlOSSession>();
    public ICollection<SqlOSUserAuthenticator> Authenticators { get; set; } = new List<SqlOSUserAuthenticator>();
    public ICollection<SqlOSRecoveryCode> RecoveryCodes { get; set; } = new List<SqlOSRecoveryCode>();
    public SqlOSUserMfaPolicyOverride? MfaPolicyOverride { get; set; }
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

public sealed class SqlOSUserPhoneNumber
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string PhoneNumberHash { get; set; } = string.Empty;
    public string? DisplayValueEncrypted { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsVerified { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RemovedAt { get; set; }
    public string? RemovalReason { get; set; }

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

public sealed class SqlOSPasswordLoginBucket
{
    public string Id { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string BucketKey { get; set; } = string.Empty;
    public string? NormalizedEmail { get; set; }
    public string? UserId { get; set; }
    public string? ClientKey { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgentHash { get; set; }
    public int FailureCount { get; set; }
    public DateTime? WindowStartedAt { get; set; }
    public DateTime? LastFailureAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LockoutReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public SqlOSUser? User { get; set; }
    public ICollection<SqlOSPasswordLoginReservationBucket> Reservations { get; set; } = [];
}

public sealed class SqlOSPasswordLoginReservation
{
    public string Id { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public ICollection<SqlOSPasswordLoginReservationBucket> Buckets { get; set; } = [];
}

public sealed class SqlOSPasswordLoginReservationBucket
{
    public string ReservationId { get; set; } = string.Empty;
    public string BucketId { get; set; } = string.Empty;

    public SqlOSPasswordLoginReservation? Reservation { get; set; }
    public SqlOSPasswordLoginBucket? Bucket { get; set; }
}

public sealed class SqlOSMfaAttemptBucket
{
    public string Id { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string BucketKey { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime? WindowStartedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<SqlOSMfaAttemptReservationBucket> Reservations { get; set; } = [];
}

public sealed class SqlOSMfaAttemptReservation
{
    public string Id { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public ICollection<SqlOSMfaAttemptReservationBucket> Buckets { get; set; } = [];
}

public sealed class SqlOSMfaAttemptReservationBucket
{
    public string ReservationId { get; set; } = string.Empty;
    public string BucketId { get; set; } = string.Empty;

    public SqlOSMfaAttemptReservation? Reservation { get; set; }
    public SqlOSMfaAttemptBucket? Bucket { get; set; }
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

public sealed class SqlOSInvitation
{
    public string Id { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string InvitedEmail { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string Role { get; set; } = "member";
    public string TokenHash { get; set; } = string.Empty;
    public string? InvitedByUserId { get; set; }
    public string? ClientApplicationId { get; set; }
    public string? RedirectUri { get; set; }
    public string? Scope { get; set; }
    public string? Resource { get; set; }
    public string? CustomFieldsJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastSentAt { get; set; }
    public string? LastSendError { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? AcceptedByUserId { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public SqlOSOrganization? Organization { get; set; }
    public SqlOSUser? InvitedByUser { get; set; }
    public SqlOSUser? AcceptedByUser { get; set; }
    public SqlOSClientApplication? ClientApplication { get; set; }
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
    public bool TrustUpstreamMfa { get; set; }
    public string AcceptedAuthnContextClassRefsJson { get; set; } = "[]";
    public string ConfigurationOwner { get; set; } = "dashboard";
    public string? ConfigurationSourceKey { get; set; }
    public string? ConfigurationFingerprint { get; set; }
    public DateTime? LastReconciledAt { get; set; }
    public DateTime? ConfigurationOrphanedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public SqlOSOrganization? Organization { get; set; }
    public ICollection<SqlOSExternalIdentity> ExternalIdentities { get; set; } = new List<SqlOSExternalIdentity>();
    public ICollection<SqlOSSsoPortalSession> PortalSessions { get; set; } = new List<SqlOSSsoPortalSession>();
}

public sealed class SqlOSScimConnection
{
    public string Id { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string? SeedKey { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string? TokenHash { get; set; }
    public string? TokenPrefix { get; set; }
    public DateTime? TokenRotatedAt { get; set; }
    public DateTime? TokenLastUsedAt { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string Source { get; set; } = "dashboard";
    public string ConfigurationOwner { get; set; } = "dashboard";
    public string? ConfigurationSourceKey { get; set; }
    public string? ConfigurationFingerprint { get; set; }
    public DateTime? LastReconciledAt { get; set; }
    public DateTime? ConfigurationOrphanedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public SqlOSOrganization? Organization { get; set; }
    public ICollection<SqlOSScimExternalId> ExternalIds { get; set; } = new List<SqlOSScimExternalId>();
    public ICollection<SqlOSScimGroupMapping> GroupMappings { get; set; } = new List<SqlOSScimGroupMapping>();
    public ICollection<SqlOSScimSyncEvent> SyncEvents { get; set; } = new List<SqlOSScimSyncEvent>();
}

public sealed class SqlOSScimExternalId
{
    public string Id { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = "User";
    public string? ExternalId { get; set; }
    public string EntityId { get; set; } = string.Empty;
    public string? FgaSubjectId { get; set; }
    public string? UserName { get; set; }
    public string? PrimaryEmail { get; set; }
    public string? DisplayName { get; set; }
    public string? FormattedName { get; set; }
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public bool OwnsUserLifecycle { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastSyncedAt { get; set; }

    public SqlOSScimConnection? Connection { get; set; }
}

public sealed class SqlOSScimGroupMapping
{
    public string Id { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string? SourceKey { get; set; }
    public string Source { get; set; } = "dashboard";
    public string MatchType { get; set; } = "display_name";
    public string? GroupDisplayName { get; set; }
    public string? GroupExternalId { get; set; }
    public string? GroupPattern { get; set; }
    public string RoleKey { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string? ResourceIdTemplate { get; set; }
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public SqlOSScimConnection? Connection { get; set; }
    public ICollection<SqlOSScimManagedGrant> ManagedGrants { get; set; } = new List<SqlOSScimManagedGrant>();
}

public sealed class SqlOSScimManagedGrant
{
    public string Id { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string MappingId { get; set; } = string.Empty;
    public string GroupExternalId { get; set; } = string.Empty;
    public string FgaGroupId { get; set; } = string.Empty;
    public string FgaGroupSubjectId { get; set; } = string.Empty;
    public string GrantId { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public SqlOSScimConnection? Connection { get; set; }
    public SqlOSScimGroupMapping? Mapping { get; set; }
}

public sealed class SqlOSScimSyncEvent
{
    public string Id { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string? ExternalId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Result { get; set; } = "success";
    public string? Error { get; set; }
    public string? DataJson { get; set; }
    public string? RequestId { get; set; }
    public DateTime OccurredAt { get; set; }

    public SqlOSScimConnection? Connection { get; set; }
    public SqlOSOrganization? Organization { get; set; }
}

public sealed class SqlOSScimOperationCommit
{
    public string Id { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
}

public sealed class SqlOSSsoPortalSession
{
    public string Id { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string? ConnectionId { get; set; }
    public string LinkTokenHash { get; set; } = string.Empty;
    public string? SessionTokenHash { get; set; }
    public string? Provider { get; set; }
    public string? ReturnUrl { get; set; }
    public string ActorType { get; set; } = "platform_admin";
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime? LastTestedAt { get; set; }
    public string? LastTestStatus { get; set; }
    public string? LastTestMessage { get; set; }

    public SqlOSOrganization? Organization { get; set; }
    public SqlOSSsoConnection? Connection { get; set; }
}

public static class SqlOSOrganizationDomainStatuses
{
    public const string PendingOwnership = "pending_ownership";
    public const string Active = "active";
    public const string Revoked = "revoked";
}

public sealed class SqlOSOrganizationDomain
{
    public string Id { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Status { get; set; } = SqlOSOrganizationDomainStatuses.PendingOwnership;
    public string? VerificationToken { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? LastError { get; set; }

    public SqlOSOrganization? Organization { get; set; }
}

public sealed class SqlOSOidcConnection
{
    public string Id { get; set; } = string.Empty;
    public SqlOSOidcProviderType ProviderType { get; set; }
    public SqlOSSocialProviderProtocol Protocol { get; set; } = SqlOSSocialProviderProtocol.Oidc;
    public string DisplayName { get; set; } = string.Empty;
    public string? LogoDataUrl { get; set; }
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
    public bool TrustUpstreamMfa { get; set; }
    public string AcceptedAmrValuesJson { get; set; } = "[]";
    public string AcceptedAcrValuesJson { get; set; } = "[]";
    public string ConfigurationOwner { get; set; } = "dashboard";
    public string? ConfigurationSourceKey { get; set; }
    public string? ConfigurationFingerprint { get; set; }
    public DateTime? LastReconciledAt { get; set; }
    public DateTime? ConfigurationOrphanedAt { get; set; }
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
    public string? OrganizationId { get; set; }
    public string? Resource { get; set; }
    public string? EffectiveAudience { get; set; }
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
    public SqlOSOrganization? Organization { get; set; }
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

    /// <summary>
    /// The complete access/refresh token pair issued when this token was
    /// rotated. The JSON payload is purpose-bound and time-limited with
    /// ASP.NET Core Data Protection. It is only recoverable during the
    /// configured retry grace window, allowing every concurrent retry to
    /// receive the exact same credentials without creating sibling tokens.
    /// This property remains mapped to the historical
    /// <c>ReplacementAccessToken</c> database column.
    /// </summary>
    public string? ReplacementTokenResponse { get; set; }

    /// <summary>
    /// The organization ID the cached <see cref="ReplacementTokenResponse"/>
    /// was minted for. Stored alongside the cached token so the grace
    /// window response metadata stays consistent with the cached JWT and
    /// callers can't switch organizations on the grace window path.
    /// </summary>
    public string? ReplacementOrganizationId { get; set; }

    /// <summary>
    /// The access-token expiry timestamp for the cached
    /// <see cref="ReplacementTokenResponse"/>.
    /// Stored explicitly so the grace window response can return the same
    /// expiry that's encoded in the cached JWT, rather than recomputing
    /// from <see cref="DateTime.UtcNow"/> which would drift.
    /// </summary>
    public DateTime? ReplacementAccessTokenExpiresAt { get; set; }

    public SqlOSSession? Session { get; set; }
}

public sealed class SqlOSClientApplication
{
    public string Id { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Audience { get; set; } = "sqlos";
    public string AccessMode { get; set; } = "all_organizations";
    public string ClientType { get; set; } = "public_pkce";
    public string RegistrationSource { get; set; } = "manual";
    public string ConfigurationOwner { get; set; } = "dashboard";
    public string? ConfigurationSourceKey { get; set; }
    public string? ConfigurationFingerprint { get; set; }
    public DateTime? LastReconciledAt { get; set; }
    public DateTime? ConfigurationOrphanedAt { get; set; }
    public string TokenEndpointAuthMethod { get; set; } = "none";
    public string GrantTypesJson { get; set; } = "[\"authorization_code\",\"refresh_token\"]";
    public string ResponseTypesJson { get; set; } = "[\"code\"]";
    public bool RequirePkce { get; set; } = true;
    public string AllowedScopesJson { get; set; } = "[]";
    public bool IsFirstParty { get; set; }
    public bool AllowNativeHeadlessAuth { get; set; }
    public bool AllowDeviceAuthorization { get; set; }
    public string RedirectUrisJson { get; set; } = "[]";
    public string? MetadataDocumentUrl { get; set; }
    public string? ClientUri { get; set; }
    public string? LogoUri { get; set; }
    public string? SoftwareId { get; set; }
    public string? SoftwareVersion { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime? MetadataFetchedAt { get; set; }
    public DateTime? MetadataExpiresAt { get; set; }
    public string? MetadataEtag { get; set; }
    public DateTime? MetadataLastModifiedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? DisabledAt { get; set; }
    public string? DisabledReason { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<SqlOSClientCredential> ClientCredentials { get; set; } = new List<SqlOSClientCredential>();
    public ICollection<SqlOSApplicationAssignment> ApplicationAssignments { get; set; } = new List<SqlOSApplicationAssignment>();
}

public sealed class SqlOSClientCredential
{
    public string Id { get; set; } = string.Empty;
    public string ClientApplicationId { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string ConfigurationOwner { get; set; } = "dashboard";
    public string? ConfigurationSourceKey { get; set; }
    public DateTime? LastReconciledAt { get; set; }

    public SqlOSClientApplication? ClientApplication { get; set; }
}

public sealed class SqlOSApplicationAssignment
{
    public string Id { get; set; } = string.Empty;
    public string ClientApplicationId { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string PrincipalType { get; set; } = "organization";
    public string? PrincipalId { get; set; }
    public string? RoleKey { get; set; }
    public string Access { get; set; } = "allowed";
    public string? Reason { get; set; }
    public string ConfigurationOwner { get; set; } = "dashboard";
    public string? ConfigurationSourceKey { get; set; }
    public string? ConfigurationFingerprint { get; set; }
    public DateTime? LastReconciledAt { get; set; }
    public DateTime? ConfigurationOrphanedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByActorType { get; set; }
    public string? CreatedByActorId { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedByActorType { get; set; }
    public string? RevokedByActorId { get; set; }

    public SqlOSClientApplication? ClientApplication { get; set; }
    public SqlOSOrganization? Organization { get; set; }
}

public sealed class SqlOSDeviceAuthorization
{
    public string Id { get; set; } = string.Empty;
    public string DeviceCodeHash { get; set; } = string.Empty;
    public string UserCodeHash { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public string ClientApplicationId { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string? Resource { get; set; }
    public string Status { get; set; } = "pending";
    public int PollingIntervalSeconds { get; set; } = 5;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastPolledAt { get; set; }
    public int PollCount { get; set; }
    public int SlowDownCount { get; set; }
    public string? ApprovedUserId { get; set; }
    public string? ApprovedOrganizationId { get; set; }
    public string? AuthenticationMethod { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? DeniedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public SqlOSClientApplication? ClientApplication { get; set; }
    public SqlOSUser? ApprovedUser { get; set; }
    public SqlOSOrganization? ApprovedOrganization { get; set; }
}

public sealed class SqlOSSigningKey
{
    public string Id { get; set; } = string.Empty;
    public string Kid { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "RS256";
    public string PublicKeyPem { get; set; } = string.Empty;
    public string CustodyProvider { get; set; } = string.Empty;
    public string KeyReference { get; set; } = string.Empty;
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
    private string _eventType = string.Empty;

    public string Id { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string? ApplicationId { get; set; }
    public string? ApplicationKey { get; set; }
    public string? UserId { get; set; }
    public string? SessionId { get; set; }
    public string EventType
    {
        get => _eventType;
        set
        {
            _eventType = value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(Action))
            {
                Action = _eventType;
            }
        }
    }
    public string Source { get; set; } = "authserver";
    public string Action { get; set; } = string.Empty;
    public string ActorType { get; set; } = "system";
    public string? ActorId { get; set; }
    public string? ActorDisplayName { get; set; }
    public string TargetsJson { get; set; } = "[]";
    public string? ContextJson { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime IngestedAt { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? RequestId { get; set; }
    public string? CorrelationId { get; set; }
    public string? IdempotencyKeyHash { get; set; }
    public string? IdempotencyScopeHash { get; set; }
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
    /// <summary>
    /// Grace window after a refresh token has been rotated during which the
    /// previous refresh token can still be exchanged. See
    /// <see cref="Configuration.SqlOSAuthServerOptions.RefreshTokenGraceWindowSeconds"/>.
    /// </summary>
    public int RefreshTokenGraceWindowSeconds { get; set; } = 30;
    public DateTime UpdatedAt { get; set; }
}

public sealed class SqlOSMfaSettings
{
    public string Id { get; set; } = "default";
    public bool Enabled { get; set; } = true;
    public bool TotpEnabled { get; set; } = true;
    public bool UserSelfEnrollmentEnabled { get; set; } = true;
    public bool RecoveryCodesEnabled { get; set; } = true;
    public bool RequireForAllUsers { get; set; }
    public bool RequireForOwnersAndAdmins { get; set; }
    public string RequiredRolesJson { get; set; } = "[\"owner\",\"admin\"]";
    public string AvailableFactorsJson { get; set; } = "[\"totp\",\"recovery_code\"]";
    public string ConfigurationOwner { get; set; } = "system";
    public string? ConfigurationSourceKey { get; set; }
    public string? ConfigurationFingerprint { get; set; }
    public DateTime? LastReconciledAt { get; set; }
    public DateTime? ConfigurationOrphanedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class SqlOSOrganizationMfaPolicy
{
    public string OrganizationId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool RequireMfaForAllUsers { get; set; }
    public bool RequireMfaForOwnersAndAdmins { get; set; }
    public bool UserSelfEnrollmentEnabled { get; set; } = true;
    public bool RecoveryCodesEnabled { get; set; } = true;
    public string RequiredRolesJson { get; set; } = "[\"owner\",\"admin\"]";
    public string AvailableFactorsJson { get; set; } = "[\"totp\",\"recovery_code\"]";
    public DateTime UpdatedAt { get; set; }

    public SqlOSOrganization? Organization { get; set; }
}

public sealed class SqlOSUserMfaPolicyOverride
{
    public string UserId { get; set; } = string.Empty;
    public bool? RequireMfa { get; set; }
    public bool? UserSelfEnrollmentEnabled { get; set; }
    public DateTime UpdatedAt { get; set; }

    public SqlOSUser? User { get; set; }
}

public sealed class SqlOSUserAuthenticator
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Type { get; set; } = "totp";
    public string DisplayName { get; set; } = "Authenticator app";
    public string SecretProtected { get; set; } = string.Empty;
    public int SecretVersion { get; set; } = 1;
    public string Algorithm { get; set; } = "SHA1";
    public int Digits { get; set; } = 6;
    public int PeriodSeconds { get; set; } = 30;
    public bool IsConfirmed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    public long? LastAcceptedTimeStep { get; set; }

    public SqlOSUser? User { get; set; }
}

public sealed class SqlOSRecoveryCode
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public SqlOSUser? User { get; set; }
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
    public string? EmailApplicationName { get; set; }
    public string? EmailLogoBase64 { get; set; }
    public string EmailPrimaryColor { get; set; } = "#2563eb";
    public string EmailAccentColor { get; set; } = "#0f172a";
    public string EmailBackgroundColor { get; set; } = "#f8fafc";
    public string AuthPageConfigurationOwner { get; set; } = "system";
    public string? AuthPageConfigurationSourceKey { get; set; }
    public string? AuthPageConfigurationFingerprint { get; set; }
    public DateTime? AuthPageLastReconciledAt { get; set; }
    public DateTime? AuthPageConfigurationOrphanedAt { get; set; }
    public string EmailConfigurationOwner { get; set; } = "system";
    public string? EmailConfigurationSourceKey { get; set; }
    public string? EmailConfigurationFingerprint { get; set; }
    public DateTime? EmailLastReconciledAt { get; set; }
    public DateTime? EmailConfigurationOrphanedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class SqlOSEmailOtpChallenge
{
    public string Id { get; set; } = string.Empty;
    public string ChallengeTokenHash { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? UserEmailId { get; set; }
    public string? AuthorizationRequestId { get; set; }
    public string? ClientApplicationId { get; set; }
    public string? RequestedOrganizationId { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSentAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime? InvalidatedAt { get; set; }
    public string? InvalidatedReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public SqlOSUser? User { get; set; }
    public SqlOSUserEmail? UserEmail { get; set; }
    public SqlOSAuthorizationRequest? AuthorizationRequest { get; set; }
    public SqlOSClientApplication? ClientApplication { get; set; }
}

public sealed class SqlOSPhoneOtpChallenge
{
    public string Id { get; set; } = string.Empty;
    public string ChallengeTokenHash { get; set; } = string.Empty;
    public string PhoneNumberHash { get; set; } = string.Empty;
    public string PhoneNumberEncrypted { get; set; } = string.Empty;
    public string MaskedPhoneNumber { get; set; } = string.Empty;
    public string Purpose { get; set; } = "login";
    public string? UserId { get; set; }
    public string? UserPhoneNumberId { get; set; }
    public string? AuthorizationRequestId { get; set; }
    public string? ClientApplicationId { get; set; }
    public string? RequestedOrganizationId { get; set; }
    public bool ProviderStarted { get; set; }
    public string Provider { get; set; } = "twilio_verify";
    public string? ProviderChallengeId { get; set; }
    public string? ProviderStatus { get; set; }
    public int AttemptCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSentAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime? InvalidatedAt { get; set; }
    public string? InvalidatedReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public SqlOSUser? User { get; set; }
    public SqlOSUserPhoneNumber? UserPhoneNumber { get; set; }
    public SqlOSAuthorizationRequest? AuthorizationRequest { get; set; }
    public SqlOSClientApplication? ClientApplication { get; set; }
}

public sealed class SqlOSAuthorizationRequest
{
    public string Id { get; set; } = string.Empty;
    public string ClientApplicationId { get; set; } = string.Empty;
    public string? DeviceAuthorizationId { get; set; }
    public string PresentationMode { get; set; } = "hosted";
    public string? OrganizationId { get; set; }
    public string? ConnectionId { get; set; }
    public string? InvitationId { get; set; }
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
    public SqlOSDeviceAuthorization? DeviceAuthorization { get; set; }
    public SqlOSOrganization? Organization { get; set; }
    public SqlOSSsoConnection? Connection { get; set; }
    public SqlOSInvitation? Invitation { get; set; }
}

public sealed class SqlOSSamlReplay
{
    public string Id { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string ResponseId { get; set; } = string.Empty;
    public string AssertionId { get; set; } = string.Empty;
    public DateTime ConsumedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
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
