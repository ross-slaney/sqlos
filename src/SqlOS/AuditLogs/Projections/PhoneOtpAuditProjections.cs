using SqlOS.Domain.Events;

namespace SqlOS.AuditLogs.Projections;

/// <summary>
/// The 7.2.1 <c>phone_otp.*</c> rows. Each builds the metadata exactly as
/// <c>SqlOSPhoneOtpService.RecordPhoneOtpAuditAsync</c> did: <c>purpose</c>, <c>maskedPhone</c>, and
/// the call site's <c>details</c> object with its member names as written there.
/// </summary>
internal static class PhoneOtpAuditProjections
{
    public static SqlOSAuditProjectionBuilder AddPhoneOtpEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            .Unaudited<PhoneOtpChallengeIssued>()
            .Unaudited<PhoneOtpChallengeSuperseded>()
            .Unaudited<PhoneOtpChallengeWithdrawn>()
            .Unaudited<PhoneOtpChallengeConsumed>()
            .Audit<PhoneOtpChallengeStarted>(static (started, context) => Row(
                "phone_otp.challenge_started",
                started.Purpose,
                started.MaskedPhone,
                started.IpAddress,
                new
                {
                    clientApplicationId = started.ClientApplicationId,
                    authorizationRequestId = started.AuthorizationRequestId,
                    requestedOrganizationId = started.RequestedOrganizationId,
                    sent = started.CodeSent
                },
                context))
            .Audit<PhoneOtpDeliveryFailed>(static (failed, context) => Row(
                "phone_otp.send_failed",
                failed.Purpose,
                failed.MaskedPhone,
                failed.IpAddress,
                new
                {
                    clientApplicationId = failed.ClientApplicationId,
                    requestedOrganizationId = failed.RequestedOrganizationId,
                    providerStatus = failed.ProviderStatus
                },
                context))
            .Audit<PhoneOtpCodeAccepted>(static (accepted, context) => Row(
                "phone_otp.verify_succeeded",
                accepted.Purpose,
                accepted.MaskedPhone,
                accepted.IpAddress,
                new
                {
                    accepted.UserId,
                    accepted.ClientApplicationId,
                    accepted.AuthorizationRequestId,
                    providerStatus = accepted.ProviderStatus
                },
                context))
            .Audit<PhoneOtpCodeRejected>(static (rejected, context) => Row(
                "phone_otp.verify_failed",
                rejected.Purpose,
                rejected.MaskedPhone,
                rejected.IpAddress,
                new
                {
                    rejected.ClientApplicationId,
                    rejected.AuthorizationRequestId,
                    reason = rejected.Reason
                },
                context))
            .Audit<PhoneOtpPhoneEnrolled>(static (enrolled, context) => Row(
                "phone_otp.phone_added",
                "enrollment",
                enrolled.MaskedPhone,
                enrolled.IpAddress,
                new
                {
                    userId = enrolled.UserId,
                    phoneNumberId = enrolled.PhoneNumberId
                },
                context))
            .Audit<PhoneOtpSendRateLimited>(static (limited, context) => Row(
                "phone_otp.rate_limit_rejected",
                limited.Purpose,
                limited.MaskedPhone,
                limited.IpAddress,
                new
                {
                    limit = limited.Limit,
                    clientApplicationId = limited.ClientApplicationId,
                    requestedOrganizationId = limited.RequestedOrganizationId
                },
                context));

    private static SqlOS.AuthServer.Models.SqlOSAuditEvent Row(
        string eventType,
        string purpose,
        string maskedPhone,
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
                maskedPhone,
                details
            });
}
