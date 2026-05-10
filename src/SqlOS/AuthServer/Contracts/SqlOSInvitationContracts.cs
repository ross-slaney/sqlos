using System.Text.Json.Nodes;

namespace SqlOS.AuthServer.Contracts;

public sealed record SqlOSCreateEmailInvitationRequest(
    string OrganizationId,
    string Email,
    string Role,
    string? ClientId = null,
    string? RedirectUri = null,
    string? Scope = null,
    string? Resource = null,
    DateTime? ExpiresAt = null,
    JsonObject? CustomFields = null,
    string? InvitedByUserId = null,
    bool SendEmail = true);

public sealed record SqlOSResendEmailInvitationRequest(string InvitationId);

public sealed record SqlOSRevokeEmailInvitationRequest(string InvitationId, string? Reason = null);

public sealed record SqlOSAcceptEmailInvitationRequest(string InvitationToken, string UserId);

public sealed record SqlOSEmailInvitationResult(
    string Id,
    string OrganizationId,
    string OrganizationName,
    string Email,
    string Role,
    string Status,
    string? InviteUrl,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? LastSentAt,
    DateTime? AcceptedAt,
    string? AcceptedByUserId,
    DateTime? RevokedAt,
    string? RevokedReason,
    string? LastSendError,
    JsonObject? CustomFields);

public sealed record SqlOSInvitationAcceptanceResult(
    string InvitationId,
    string OrganizationId,
    string UserId,
    string Role,
    DateTime AcceptedAt,
    bool MembershipCreated,
    bool MembershipReactivated,
    bool EmailVerified);

public sealed record SqlOSHeadlessInvitationResolveRequest(string InvitationToken);
