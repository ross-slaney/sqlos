using SqlOS.AuthServer.Models;

namespace SqlOS.Domain.Events;

// Email verification: the verification links (temporary tokens of the email-verification kind) and
// the verification flow's records. The audited ones carry exactly what 7.2.1's
// user.email-verification-* rows recorded.

/// <summary>An email-verification link was created for an account's address (<c>user.email-verification-token-created</c>).</summary>
internal sealed record EmailVerificationTokenCreated(string TokenId, string UserId) : TemporaryTokenOutcome(TokenId)
{
    public override TemporaryTokenKind Kind => SqlOSTemporaryTokenKinds.EmailVerification;
}

/// <summary>
/// A record of a verification request for an address; <paramref name="Eligible"/> says whether an
/// account owns it unverified (<c>user.email-verification-requested</c>).
/// </summary>
internal sealed record EmailVerificationRequested(
    string? UserId,
    string MaskedEmail,
    string? IpAddress,
    bool Eligible) : ISqlOSDomainEvent;

/// <summary>The verification link was emailed to the address it verifies (<c>user.email-verification-sent</c>).</summary>
internal sealed record EmailVerificationEmailSent(
    string TokenId,
    string UserId,
    string MaskedEmail,
    string? IpAddress,
    string DeliveryId,
    string DeliveryStatus,
    string? ProviderMessageId) : TemporaryTokenOutcome(TokenId)
{
    public override TemporaryTokenKind Kind => SqlOSTemporaryTokenKinds.EmailVerification;
}

/// <summary>
/// A failure record: the verification email could not be sent, so its link was withdrawn;
/// <paramref name="Error"/> is the failure's message (<c>user.email-verification-send-failed</c>).
/// </summary>
internal sealed record EmailVerificationEmailFailed(
    string UserId,
    string MaskedEmail,
    string? IpAddress,
    string Error) : ISqlOSDomainEvent;
