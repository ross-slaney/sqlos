using SqlOS.Domain.Events;

namespace SqlOS.AuditLogs.Projections;

/// <summary>
/// The 7.2.1 <c>email_otp.*</c> rows. Each builds the metadata exactly as
/// <c>SqlOSEmailOtpService.RecordOtpAuditAsync</c> did: <c>purpose</c>, <c>maskedEmail</c>, and the
/// call site's <c>details</c> object with its member names as written there.
/// </summary>
internal static class EmailOtpAuditProjections
{
    public static SqlOSAuditProjectionBuilder AddEmailOtpEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            .Unaudited<EmailOtpChallengeIssued>()
            .Unaudited<EmailOtpChallengeSuperseded>()
            .Unaudited<EmailOtpChallengeWithdrawn>()
            .Unaudited<EmailOtpChallengeConsumed>()
            .Audit<EmailOtpChallengeStarted>(static (started, context) => Row(
                "email_otp.challenge_started",
                started.Purpose,
                started.MaskedEmail,
                started.IpAddress,
                new
                {
                    clientApplicationId = started.ClientApplicationId,
                    authorizationRequestId = started.AuthorizationRequestId,
                    requestedOrganizationId = started.RequestedOrganizationId,
                    sent = started.CodeSent
                },
                context))
            .Audit<EmailOtpDeliveryFailed>(static (failed, context) => Row(
                "email_otp.send_failed",
                failed.Purpose,
                failed.MaskedEmail,
                failed.IpAddress,
                new
                {
                    clientApplicationId = failed.ClientApplicationId,
                    requestedOrganizationId = failed.RequestedOrganizationId
                },
                context))
            .Audit<EmailOtpCodeAccepted>(static (accepted, context) => Row(
                "email_otp.verify_succeeded",
                accepted.Purpose,
                accepted.MaskedEmail,
                accepted.IpAddress,
                new
                {
                    accepted.UserId,
                    accepted.ClientApplicationId,
                    accepted.AuthorizationRequestId
                },
                context))
            .Audit<EmailOtpCodeRejected>(static (rejected, context) => Row(
                "email_otp.verify_failed",
                rejected.Purpose,
                rejected.MaskedEmail,
                rejected.IpAddress,
                new
                {
                    rejected.ClientApplicationId,
                    rejected.AuthorizationRequestId,
                    reason = rejected.Reason
                },
                context))
            .Audit<EmailOtpSendRateLimited>(static (limited, context) => Row(
                "email_otp.rate_limit_rejected",
                limited.Purpose,
                limited.MaskedEmail,
                limited.IpAddress,
                new
                {
                    limit = limited.Limit,
                    clientApplicationId = limited.ClientApplicationId,
                    requestedOrganizationId = limited.RequestedOrganizationId
                },
                context))
            .Audit<EmailOtpSignupStartedForExistingEmail>(static (signup, context) => Row(
                "email_otp.signup_existing_email",
                "signup",
                signup.MaskedEmail,
                signup.IpAddress,
                new
                {
                    clientApplicationId = signup.ClientApplicationId,
                    authorizationRequestId = signup.AuthorizationRequestId,
                    requestedOrganizationId = signup.RequestedOrganizationId,
                    reason = "existing_email"
                },
                context))
            .Audit<EmailOtpSignupRejectedForExistingEmail>(static (rejected, context) => Row(
                "email_otp.signup_existing_email_rejected",
                "signup",
                rejected.MaskedEmail,
                rejected.IpAddress,
                new
                {
                    rejected.ClientApplicationId,
                    rejected.AuthorizationRequestId,
                    reason = rejected.Reason
                },
                context));

    private static SqlOS.AuthServer.Models.SqlOSAuditEvent Row(
        string eventType,
        string purpose,
        string maskedEmail,
        string? ipAddress,
        object details,
        SqlOSAuditProjectionContext context)
        => AuthServerAuditRows.System(
            eventType,
            context,
            ipAddress: ipAddress,
            data: new
            {
                purpose,
                maskedEmail,
                details
            });
}
