using SqlOS.AuthServer.Models;

namespace SqlOS.Domain.Events;

// Password resets: the reset links and request markers (temporary tokens of the password-reset
// kinds), and the reset flow's failure records. The audited ones carry exactly what 7.2.1's
// password_reset.* rows recorded: the account, the masked address, the address the request came
// from, and the details of each action.

/// <summary>
/// A reset was requested for an address, recorded on the request's marker; <paramref name="Eligible"/>
/// says whether an account can be sent a link (<c>password_reset.requested</c>).
/// </summary>
internal sealed record PasswordResetRequested(
    string TokenId,
    string? UserId,
    string MaskedEmail,
    string? IpAddress,
    bool Eligible,
    string? ClientKey) : TemporaryTokenOutcome(TokenId)
{
    public override TemporaryTokenKind Kind => SqlOSTemporaryTokenKinds.PasswordResetRequest;
}

/// <summary>
/// A failure record: a reset email was refused because a send limit is reached;
/// <paramref name="Scope"/> names it (<c>password_reset.rate_limit_rejected</c>).
/// </summary>
internal sealed record PasswordResetSendRateLimited(
    string? UserId,
    string MaskedEmail,
    string? IpAddress,
    string Scope,
    DateTime? RetryAfter,
    string? ClientKey) : ISqlOSDomainEvent;

/// <summary>
/// The reset link was emailed to its account's stored address with the built-in template
/// (<c>password_reset.email_sent</c>).
/// </summary>
internal sealed record PasswordResetEmailSent(
    string TokenId,
    string UserId,
    string MaskedEmail,
    string? IpAddress,
    string DeliveryId,
    string DeliveryStatus,
    string? ProviderMessageId) : TemporaryTokenOutcome(TokenId)
{
    public override TemporaryTokenKind Kind => SqlOSTemporaryTokenKinds.PasswordReset;
}

/// <summary>
/// The reset link was handed to the host's message builder and sender
/// (<c>password_reset.email_sent</c>, with <c>customMessage</c>).
/// </summary>
internal sealed record PasswordResetMessageSent(
    string TokenId,
    string UserId,
    string MaskedEmail,
    string? IpAddress,
    string DeliveryId) : TemporaryTokenOutcome(TokenId)
{
    public override TemporaryTokenKind Kind => SqlOSTemporaryTokenKinds.PasswordReset;
}

/// <summary>
/// A failure record: the reset link could not be built or sent, so the account's open links were
/// withdrawn; <paramref name="Error"/> is the failure's message (<c>password_reset.email_send_failed</c>).
/// </summary>
internal sealed record PasswordResetEmailFailed(
    string UserId,
    string MaskedEmail,
    string? IpAddress,
    string Error) : ISqlOSDomainEvent;

/// <summary>
/// An operator sent the account a reset email from the admin API or the dashboard, recorded on the
/// link it sent (<c>password_reset.admin_email_sent</c>).
/// </summary>
internal sealed record PasswordResetEmailSentByOperator(
    string TokenId,
    string UserId,
    string MaskedEmail,
    string? IpAddress,
    string DeliveryId,
    string DeliveryStatus) : TemporaryTokenOutcome(TokenId)
{
    public override TemporaryTokenKind Kind => SqlOSTemporaryTokenKinds.PasswordReset;
}

/// <summary>
/// A failure record: a presented reset link set no password; <paramref name="Reason"/> says why
/// (<c>password_reset.invalid_or_expired</c>).
/// </summary>
internal sealed record PasswordResetLinkRefused(string? UserId, string Reason) : ISqlOSDomainEvent;

/// <summary>The reset link set its account's new password (<c>password_reset.completed</c>).</summary>
internal sealed record PasswordResetCompleted(string TokenId, string UserId) : TemporaryTokenOutcome(TokenId)
{
    public override TemporaryTokenKind Kind => SqlOSTemporaryTokenKinds.PasswordReset;
}
