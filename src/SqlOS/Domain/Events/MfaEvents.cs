namespace SqlOS.Domain.Events;

// MFA challenges: the failure records of the second factor. The audited ones carry exactly what
// 7.2.1's user.mfa.* rows recorded.

/// <summary>
/// A failure record: a wrong second factor was presented for an MFA challenge.
/// <paramref name="AttemptCount"/> is the challenge's failures so far, and
/// <paramref name="ChallengeLocked"/> says whether this one reached the limit and withdrew it
/// (<c>user.mfa.challenge_failed</c>).
/// </summary>
internal sealed record MfaChallengeFailed(
    string ChallengeId,
    string UserId,
    string? OrganizationId,
    string? IpAddress,
    int AttemptCount,
    bool ChallengeLocked) : ISqlOSDomainEvent;

/// <summary>
/// A failure record: an authenticator enrollment was refused for an MFA challenge that does not
/// permit one; <paramref name="Stage"/> is <c>start</c>
/// (<c>user.mfa.enrollment.challenge_rejected</c>).
/// </summary>
internal sealed record MfaChallengeEnrollmentRejected(
    string ChallengeId,
    string? UserId,
    string? OrganizationId,
    string? ClientApplicationId,
    string Stage) : ISqlOSDomainEvent;
