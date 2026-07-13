namespace SqlOS.AuthServer.Contracts;

public sealed record SqlOSMagicLinkStartRequest(
    string Email,
    string ClientId,
    string? OrganizationId);

public sealed record SqlOSMagicLinkCompleteRequest(
    string Token);

public sealed record SqlOSMagicLinkStartResult(
    string Email,
    string MaskedEmail,
    string Message,
    DateTime ExpiresAt,
    DateTime NextAllowedSendAt);

public sealed record SqlOSMagicLinkCompleteResult(
    bool RequiresOrganizationSelection,
    string? PendingAuthToken,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    SqlOSTokenResponse? Tokens);
