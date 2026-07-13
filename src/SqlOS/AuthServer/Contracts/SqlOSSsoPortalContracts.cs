namespace SqlOS.AuthServer.Contracts;

public sealed record SqlOSCreateSsoPortalSessionRequest(
    string OrganizationId,
    string? CreatedByUserId = null,
    DateTime? ExpiresAt = null,
    string? ReturnUrl = null,
    string? Provider = null);

public sealed record SqlOSRevokeSsoPortalSessionRequest(string? Reason = null);

public sealed record SqlOSUpdateSsoPortalProviderRequest(string Provider);

public sealed record SqlOSSsoPortalEnrollmentPolicyRequest(
    bool RequireSsoForExistingMembers,
    bool AllowJitProvisioning);

public sealed record SqlOSSsoPortalRevokeOrganizationSessionsRequest(bool Confirm);

public sealed record SqlOSSsoPortalDomainRequest(string Domain);

public sealed record SqlOSSsoPortalMetadataRequest(string MetadataXml);

public sealed record SqlOSSsoPortalTestRequest(
    string? ClientId = null,
    string? RedirectUri = null,
    string? State = null,
    string? CodeChallenge = null,
    string? CodeChallengeMethod = null);

public sealed record SqlOSSsoPortalSessionResult(
    string Id,
    string OrganizationId,
    string OrganizationName,
    string? PrimaryDomain,
    string Status,
    string? Provider,
    string? ConnectionId,
    string? SetupUrl,
    string PortalUrl,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? OpenedAt,
    DateTime? LastSeenAt,
    DateTime? RevokedAt,
    string? RevokedReason);

public sealed record SqlOSSsoPortalStateResult(
    SqlOSSsoPortalOrganizationResult Organization,
    SqlOSSsoPortalConnectionResult Connection,
    string? Provider,
    string ServiceProviderEntityId,
    string AssertionConsumerServiceUrl,
    IReadOnlyList<SqlOSSsoProviderGuide> Providers,
    SqlOSSsoPortalTestResult? LatestTest,
    SqlOSOrganizationDomainResult? Domain = null,
    SqlOSSsoSetupAllowedActions? AllowedActions = null);

public sealed record SqlOSSsoPortalOrganizationResult(
    string Id,
    string Name,
    string Slug,
    string? PrimaryDomain);

public sealed record SqlOSSsoPortalConnectionResult(
    string Id,
    string DisplayName,
    bool IsEnabled,
    string SetupStatus,
    string? IdentityProviderEntityId,
    string? SingleSignOnUrl,
    bool AutoProvisionUsers,
    bool AutoLinkByEmail,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    SqlOSSsoPortalEnrollmentPolicyResult? EnrollmentPolicy = null);

public sealed record SqlOSSsoPortalEnrollmentPolicyResult(
    bool RequireSsoForExistingMembers,
    bool AllowJitProvisioning);

public sealed record SqlOSSsoPortalRevokeOrganizationSessionsResult(
    string OrganizationId,
    string ConnectionId,
    string Domain,
    int RevokedSessions,
    DateTime RevokedAt);

public sealed record SqlOSSsoProviderGuide(
    string Key,
    string Label,
    string MetadataLabel,
    string EntityIdLabel,
    string AcsUrlLabel,
    IReadOnlyList<string> Steps);

public sealed record SqlOSSsoMetadataValidationResult(
    bool IsValid,
    string? Error,
    string? IdentityProviderEntityId,
    string? SingleSignOnUrl,
    bool HasSigningCertificate);

public sealed record SqlOSDomainOwnershipRecord(
    string Type,
    string Name,
    string Value);

public sealed record SqlOSOrganizationDomainResult(
    string Id,
    string OrganizationId,
    string Domain,
    string Status,
    SqlOSDomainOwnershipRecord? OwnershipRecord,
    DateTime CreatedAt,
    DateTime? VerifiedAt,
    DateTime? LastCheckedAt,
    DateTime? RevokedAt,
    string? LastError);

public sealed record SqlOSSsoSetupServiceProvider(
    string EntityId,
    string AssertionConsumerServiceUrl);

public sealed record SqlOSSsoSetupAllowedActions(
    bool CanSelectProvider,
    bool CanStartDomainVerification,
    bool CanConfirmDomainVerification,
    bool CanValidateMetadata,
    bool CanImportMetadata,
    bool CanActivate,
    bool CanDisable,
    bool CanTest,
    bool CanSignOut,
    bool CanUpdateEnrollmentPolicy = true,
    bool CanRevokeOrganizationSessions = false);

public sealed record SqlOSSsoSetupViewModel(
    string View,
    string SetupApiBasePath,
    string PortalUrl,
    SqlOSSsoPortalOrganizationResult Organization,
    SqlOSSsoPortalConnectionResult Connection,
    SqlOSOrganizationDomainResult? Domain,
    string? Provider,
    SqlOSSsoSetupServiceProvider ServiceProvider,
    IReadOnlyList<SqlOSSsoProviderGuide> Providers,
    SqlOSSsoPortalTestResult? LatestTest,
    SqlOSSsoSetupAllowedActions AllowedActions,
    string? Error,
    IReadOnlyDictionary<string, string> FieldErrors);

public sealed record SqlOSSsoSetupActionResult(
    string Type,
    string? RedirectUrl,
    SqlOSSsoSetupViewModel? ViewModel);

public sealed record SqlOSSsoPortalTestResult(
    string Status,
    string Message,
    string? AuthorizationUrl,
    DateTime TestedAt);
