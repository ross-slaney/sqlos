namespace SqlOS.AuthServer.Contracts;

/// <summary>
/// Selects sessions for an administrative revocation. At least one selector is required;
/// multiple selectors are combined with AND semantics.
/// </summary>
public sealed record SqlOSAdminSessionRevocationRequest(
    string? SessionId = null,
    string? UserId = null,
    string? OrganizationId = null,
    string? ClientApplicationId = null,
    string? Reason = null,
    string? OperationId = null,
    bool Confirm = false);

public sealed record SqlOSAdminSessionRevocationResult(
    bool Preview,
    string OperationId,
    int MatchedSessions,
    int NewlyRevokedSessions,
    int AlreadyRevokedSessions,
    int ActiveRefreshTokens,
    int NewlyRevokedRefreshTokens,
    DateTime? RevokedAt,
    string? AuditEventId = null);
