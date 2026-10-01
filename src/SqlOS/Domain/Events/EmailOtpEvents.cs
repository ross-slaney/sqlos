namespace SqlOS.Domain.Events;

// Email-code challenges (SqlOSEmailOtpChallenge) and the email-code flow's failure records. The
// audited ones carry exactly what 7.2.1's email_otp.* rows recorded: the purpose, the masked
// address, the address the request came from, and the details of each action.

/// <summary>An email-code challenge was issued.</summary>
internal sealed record EmailOtpChallengeIssued(string ChallengeId) : ISqlOSDomainEvent;

/// <summary>A newer challenge for the same address and sign-in context replaced this one.</summary>
internal sealed record EmailOtpChallengeSuperseded(string ChallengeId) : ISqlOSDomainEvent;

/// <summary>The challenge was withdrawn because its account's sessions were revoked.</summary>
internal sealed record EmailOtpChallengeWithdrawn(string ChallengeId, string Reason) : ISqlOSDomainEvent;

/// <summary>The challenge's code went out to its stored recipient, or was withheld (<c>email_otp.challenge_started</c>).</summary>
internal sealed record EmailOtpChallengeStarted(
    string ChallengeId,
    string Purpose,
    string MaskedEmail,
    string? IpAddress,
    string? ClientApplicationId,
    string? AuthorizationRequestId,
    string? RequestedOrganizationId,
    bool CodeSent) : ISqlOSDomainEvent;

/// <summary>The code could not be sent, so the challenge was invalidated (<c>email_otp.send_failed</c>).</summary>
internal sealed record EmailOtpDeliveryFailed(
    string ChallengeId,
    string Purpose,
    string MaskedEmail,
    string? IpAddress,
    string? ClientApplicationId,
    string? RequestedOrganizationId) : ISqlOSDomainEvent;

/// <summary>A presented code was right (<c>email_otp.verify_succeeded</c>).</summary>
internal sealed record EmailOtpCodeAccepted(
    string ChallengeId,
    string Purpose,
    string MaskedEmail,
    string? IpAddress,
    string? UserId,
    string? ClientApplicationId,
    string? AuthorizationRequestId) : ISqlOSDomainEvent;

/// <summary>
/// A presented code was wrong; <paramref name="Reason"/> is <c>max_attempts</c> when it spent the
/// last attempt, else <c>wrong_code</c> (<c>email_otp.verify_failed</c>).
/// </summary>
internal sealed record EmailOtpCodeRejected(
    string ChallengeId,
    string Purpose,
    string MaskedEmail,
    string? IpAddress,
    string? ClientApplicationId,
    string? AuthorizationRequestId,
    string Reason) : ISqlOSDomainEvent;

/// <summary>The challenge was spent by the code that proved its mailbox.</summary>
internal sealed record EmailOtpChallengeConsumed(string ChallengeId) : ISqlOSDomainEvent;

/// <summary>
/// A failure record: a code was refused because a send limit is reached; <paramref name="Limit"/>
/// names it (<c>email</c>, <c>ip</c> or <c>client</c>) (<c>email_otp.rate_limit_rejected</c>).
/// </summary>
internal sealed record EmailOtpSendRateLimited(
    string Purpose,
    string MaskedEmail,
    string? IpAddress,
    string Limit,
    string? ClientApplicationId,
    string? RequestedOrganizationId) : ISqlOSDomainEvent;

/// <summary>A failure record: an email-code sign-up started for an address an account already has (<c>email_otp.signup_existing_email</c>).</summary>
internal sealed record EmailOtpSignupStartedForExistingEmail(
    string MaskedEmail,
    string? IpAddress,
    string? ClientApplicationId,
    string? AuthorizationRequestId,
    string? RequestedOrganizationId) : ISqlOSDomainEvent;

/// <summary>
/// A failure record: an email-code sign-up was refused because the address belongs to an account
/// (<c>email_otp.signup_existing_email_rejected</c>).
/// </summary>
internal sealed record EmailOtpSignupRejectedForExistingEmail(
    string MaskedEmail,
    string? IpAddress,
    string? ClientApplicationId,
    string? AuthorizationRequestId,
    string Reason) : ISqlOSDomainEvent;
