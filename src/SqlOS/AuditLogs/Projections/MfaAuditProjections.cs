using SqlOS.Domain.Events;

namespace SqlOS.AuditLogs.Projections;

/// <summary>
/// The 7.2.1 <c>user.mfa.*</c> failure rows of the second factor, each with the metadata the 7.2.1
/// call site built.
/// </summary>
internal static class MfaAuditProjections
{
    public static SqlOSAuditProjectionBuilder AddMfaEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            // SqlOSAuthService.TryRecordMfaChallengeAuditAsync: RecordAuditAsync(eventType, "system", null,
            // userId, organizationId, ipAddress, data: { challengeId, details }).
            .Audit<MfaChallengeFailed>(static (failed, context) => AuthServerAuditRows.System(
                "user.mfa.challenge_failed",
                context,
                userId: failed.UserId,
                ipAddress: failed.IpAddress,
                data: new
                {
                    challengeId = failed.ChallengeId,
                    details = new
                    {
                        attemptCount = failed.AttemptCount,
                        challengeLocked = failed.ChallengeLocked
                    }
                },
                organizationId: failed.OrganizationId))
            // SqlOSAuthService.RecordRejectedChallengeEnrollmentAsync: RecordAuditAsync(eventType, "user",
            // userId, userId, organizationId, data: { stage, challenge_id, client_application_id }).
            .Audit<MfaChallengeEnrollmentRejected>(static (rejected, context) => AuthServerAuditRows.Row(
                "user.mfa.enrollment.challenge_rejected",
                "user",
                rejected.UserId,
                context,
                userId: rejected.UserId,
                organizationId: rejected.OrganizationId,
                data: new
                {
                    stage = rejected.Stage,
                    challenge_id = rejected.ChallengeId,
                    client_application_id = rejected.ClientApplicationId
                }));
}
