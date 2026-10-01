namespace SqlOS.Domain.Events;

// Phone-code challenges (SqlOSPhoneOtpChallenge) and the phone-code flow's failure records. The
// audited ones carry exactly what 7.2.1's phone_otp.* rows recorded.

/// <summary>A phone-code challenge was issued.</summary>
internal sealed record PhoneOtpChallengeIssued(string ChallengeId) : ISqlOSDomainEvent;

/// <summary>A newer challenge for the same number, purpose and sign-in context replaced this one.</summary>
internal sealed record PhoneOtpChallengeSuperseded(string ChallengeId) : ISqlOSDomainEvent;

/// <summary>The challenge was withdrawn because its account's sessions were revoked.</summary>
internal sealed record PhoneOtpChallengeWithdrawn(string ChallengeId, string Reason) : ISqlOSDomainEvent;

/// <summary>
/// The provider sent the code to the stored recipient, or the code was withheld because no active
/// account owns the number (<c>phone_otp.challenge_started</c>).
/// </summary>
internal sealed record PhoneOtpChallengeStarted(
    string ChallengeId,
    string Purpose,
    string MaskedPhone,
    string? IpAddress,
    string? ClientApplicationId,
    string? AuthorizationRequestId,
    string? RequestedOrganizationId,
    bool CodeSent) : ISqlOSDomainEvent;

/// <summary>The provider refused to send the code, so the challenge was invalidated (<c>phone_otp.send_failed</c>).</summary>
internal sealed record PhoneOtpDeliveryFailed(
    string ChallengeId,
    string Purpose,
    string MaskedPhone,
    string? IpAddress,
    string? ClientApplicationId,
    string? RequestedOrganizationId,
    string? ProviderStatus) : ISqlOSDomainEvent;

/// <summary>The provider approved the presented code (<c>phone_otp.verify_succeeded</c>).</summary>
internal sealed record PhoneOtpCodeAccepted(
    string ChallengeId,
    string Purpose,
    string MaskedPhone,
    string? IpAddress,
    string? UserId,
    string? ClientApplicationId,
    string? AuthorizationRequestId,
    string? ProviderStatus) : ISqlOSDomainEvent;

/// <summary>
/// The presented code was refused, or the challenge was never sent, so the challenge was
/// invalidated for <paramref name="Reason"/> (<c>phone_otp.verify_failed</c>).
/// </summary>
internal sealed record PhoneOtpCodeRejected(
    string ChallengeId,
    string Purpose,
    string MaskedPhone,
    string? IpAddress,
    string? ClientApplicationId,
    string? AuthorizationRequestId,
    string Reason) : ISqlOSDomainEvent;

/// <summary>The challenge was spent by the code the provider approved.</summary>
internal sealed record PhoneOtpChallengeConsumed(string ChallengeId) : ISqlOSDomainEvent;

/// <summary>An enrollment challenge added its verified number to the account (<c>phone_otp.phone_added</c>).</summary>
internal sealed record PhoneOtpPhoneEnrolled(
    string ChallengeId,
    string MaskedPhone,
    string? IpAddress,
    string UserId,
    string PhoneNumberId) : ISqlOSDomainEvent;

/// <summary>
/// A failure record: a code was refused because a send limit is reached; <paramref name="Limit"/>
/// names it (<c>phone_otp.rate_limit_rejected</c>).
/// </summary>
internal sealed record PhoneOtpSendRateLimited(
    string Purpose,
    string MaskedPhone,
    string? IpAddress,
    string Limit,
    string? ClientApplicationId,
    string? RequestedOrganizationId) : ISqlOSDomainEvent;
