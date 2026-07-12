namespace SqlOS.AuthServer.Contracts;

public static class SqlOSMfaFactorTypes
{
    public const string Totp = "totp";
    public const string RecoveryCode = "recovery_code";
}

public sealed record SqlOSMfaSettingsDto(
    bool Enabled,
    bool TotpEnabled,
    bool UserSelfEnrollmentEnabled,
    bool RecoveryCodesEnabled,
    bool RequireForAllUsers,
    bool RequireForOwnersAndAdmins,
    IReadOnlyList<string> RequiredRoles,
    IReadOnlyList<string> AvailableFactors,
    DateTime UpdatedAt,
    bool ManagedByStartupSeed);

public sealed record SqlOSUpdateMfaSettingsRequest(
    bool Enabled,
    bool TotpEnabled,
    bool UserSelfEnrollmentEnabled,
    bool RecoveryCodesEnabled,
    bool RequireForAllUsers,
    bool RequireForOwnersAndAdmins,
    IReadOnlyList<string>? RequiredRoles,
    IReadOnlyList<string>? AvailableFactors);

public sealed record SqlOSOrganizationMfaPolicyDto(
    string OrganizationId,
    string? OrganizationSlug,
    string? OrganizationName,
    bool IsEnabled,
    bool RequireMfaForAllUsers,
    bool RequireMfaForOwnersAndAdmins,
    bool UserSelfEnrollmentEnabled,
    bool RecoveryCodesEnabled,
    IReadOnlyList<string> RequiredRoles,
    IReadOnlyList<string> AvailableFactors,
    DateTime UpdatedAt);

public sealed record SqlOSUpdateOrganizationMfaPolicyRequest(
    bool IsEnabled,
    bool RequireMfaForAllUsers,
    bool RequireMfaForOwnersAndAdmins,
    bool UserSelfEnrollmentEnabled,
    bool RecoveryCodesEnabled,
    IReadOnlyList<string>? RequiredRoles,
    IReadOnlyList<string>? AvailableFactors);

public sealed record SqlOSMfaStatusResult(
    bool MfaEnabled,
    bool Required,
    bool EnrollmentRequired,
    bool UserSelfEnrollmentEnabled,
    bool HasTotp,
    int RecoveryCodeCount,
    IReadOnlyList<string> AvailableFactors,
    string? PolicyReason);

public sealed record SqlOSMfaAuthenticatorDto(
    string Id,
    string Type,
    string DisplayName,
    bool IsConfirmed,
    DateTime CreatedAt,
    DateTime? ConfirmedAt,
    DateTime? LastUsedAt);

public sealed record SqlOSTotpEnrollmentStartRequest(string? DisplayName = null);

public sealed record SqlOSTotpChallengeEnrollmentStartRequest(
    string MfaToken,
    string? DisplayName = null);

public sealed record SqlOSTotpEnrollmentStartResult(
    string EnrollmentToken,
    string AuthenticatorId,
    string Secret,
    string ProvisioningUri,
    string QrCodeDataUrl,
    DateTime ExpiresAt);

public sealed record SqlOSTotpEnrollmentVerifyRequest(
    string EnrollmentToken,
    string Code,
    string? MfaToken = null);

public sealed record SqlOSTotpEnrollmentVerifyResult(
    string AuthenticatorId,
    IReadOnlyList<string> RecoveryCodes,
    SqlOSTokenResponse? Tokens = null,
    string? RedirectUrl = null);

public sealed record SqlOSMfaChallengeVerifyRequest(
    string MfaToken,
    string Code);

public sealed record SqlOSMfaChallengeVerifyResult(
    SqlOSTokenResponse? Tokens,
    string? RedirectUrl);

public sealed record SqlOSMfaChallengePayload(
    string Flow,
    string ClientId,
    string AuthenticationMethod,
    string? AuthorizationRequestId = null,
    string? Resource = null,
    bool EnrollmentRequired = false,
    IReadOnlyList<string>? PermittedEnrollmentFactors = null);
