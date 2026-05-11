using System.Text.Json.Nodes;

namespace SqlOS.AuthServer.Contracts;

public sealed record SqlOSHeadlessProviderDto(
    string ConnectionId,
    string ProviderType,
    string DisplayName,
    string? LogoDataUrl = null);

public sealed record SqlOSHeadlessDeviceAuthorizationDto(
    string UserCode,
    string ClientId,
    string ClientName,
    string Scope,
    string? Resource,
    DateTime ExpiresAt,
    string Status);

public sealed record SqlOSHeadlessViewModel(
    string View,
    string AuthBasePath,
    string HeadlessApiBasePath,
    SqlOSAuthPageSettingsDto Settings,
    string? RequestId,
    string? ClientId,
    string? ClientName,
    string? Email,
    string? DisplayName,
    string? Error,
    string? Info,
    IReadOnlyDictionary<string, string> FieldErrors,
    string? ChallengeToken,
    string? SignupToken,
    string? PendingToken,
    IReadOnlyList<SqlOSOrganizationOption> OrganizationSelection,
    IReadOnlyList<SqlOSHeadlessProviderDto> Providers,
    SqlOSEmailInvitationResult? Invitation,
    JsonObject? UiContext,
    SqlOSHeadlessDeviceAuthorizationDto? DeviceAuthorization = null);

public sealed record SqlOSHeadlessActionResult(
    string Type,
    string? RedirectUrl,
    SqlOSHeadlessViewModel? ViewModel);

public sealed record SqlOSHeadlessStartRequest(
    string ResponseType,
    string ClientId,
    string RedirectUri,
    string State,
    string? Scope,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    string? Resource,
    string? LoginHint,
    string? Prompt,
    string? Nonce,
    string? View,
    JsonObject? UiContext,
    string? InvitationToken = null);

public sealed record SqlOSHeadlessIdentifyRequest(
    string RequestId,
    string Email,
    string? InvitationToken = null);

public sealed record SqlOSHeadlessPasswordLoginRequest(
    string RequestId,
    string Email,
    string Password,
    string? InvitationToken = null);

public sealed record SqlOSHeadlessEmailOtpStartRequest(
    string RequestId,
    string Email,
    string? InvitationToken = null);

public sealed record SqlOSHeadlessEmailOtpVerifyRequest(
    string RequestId,
    string ChallengeToken,
    string Code,
    string? InvitationToken = null);

public sealed record SqlOSHeadlessEmailOtpSignupStartRequest(
    string RequestId,
    string DisplayName,
    string Email,
    string? OrganizationName,
    JsonObject? CustomFields,
    string? InvitationToken = null);

public sealed record SqlOSHeadlessEmailOtpSignupVerifyRequest(
    string RequestId,
    string SignupToken,
    string ChallengeToken,
    string Code,
    string? InvitationToken = null);

public sealed record SqlOSHeadlessSignupRequest(
    string RequestId,
    string DisplayName,
    string Email,
    string Password,
    string? OrganizationName,
    JsonObject? CustomFields,
    string? InvitationToken = null);

public sealed record SqlOSHeadlessOrganizationSelectionRequest(
    string PendingToken,
    string OrganizationId);

public sealed record SqlOSHeadlessDeviceAuthorizationResolveRequest(
    string? UserCode,
    string? RequestId = null);

public sealed record SqlOSHeadlessDeviceAuthorizationApproveRequest(
    string? UserCode,
    string? OrganizationId = null,
    string? RequestId = null);

public sealed record SqlOSHeadlessProviderStartRequest(
    string RequestId,
    string ConnectionId,
    string? Email,
    string? InvitationToken = null);

public sealed class SqlOSHeadlessValidationException : InvalidOperationException
{
    public SqlOSHeadlessValidationException(
        string message,
        IReadOnlyDictionary<string, string>? fieldErrors = null,
        IReadOnlyList<string>? globalErrors = null)
        : base(message)
    {
        FieldErrors = fieldErrors ?? new Dictionary<string, string>(StringComparer.Ordinal);
        GlobalErrors = globalErrors ?? Array.Empty<string>();
    }

    public IReadOnlyDictionary<string, string> FieldErrors { get; }
    public IReadOnlyList<string> GlobalErrors { get; }
}
