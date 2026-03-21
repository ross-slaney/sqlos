using System.Text.Json.Nodes;

namespace SqlOS.AuthServer.Contracts;

public sealed record SqlOSHeadlessProviderDto(
    string ConnectionId,
    string ProviderType,
    string DisplayName,
    string? LogoDataUrl = null);

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
    string? PendingToken,
    IReadOnlyList<SqlOSOrganizationOption> OrganizationSelection,
    IReadOnlyList<SqlOSHeadlessProviderDto> Providers,
    JsonObject? UiContext);

public sealed record SqlOSHeadlessActionResult(
    string Type,
    string? RedirectUrl,
    SqlOSHeadlessViewModel? ViewModel);

public sealed record SqlOSHeadlessIdentifyRequest(
    string RequestId,
    string Email);

public sealed record SqlOSHeadlessPasswordLoginRequest(
    string RequestId,
    string Email,
    string Password);

public sealed record SqlOSHeadlessSignupRequest(
    string RequestId,
    string DisplayName,
    string Email,
    string Password,
    string? OrganizationName,
    JsonObject? CustomFields);

public sealed record SqlOSHeadlessOrganizationSelectionRequest(
    string PendingToken,
    string OrganizationId);

public sealed record SqlOSHeadlessProviderStartRequest(
    string RequestId,
    string ConnectionId,
    string? Email);

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
