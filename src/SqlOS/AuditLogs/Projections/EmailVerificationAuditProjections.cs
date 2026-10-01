using SqlOS.Domain.Events;

namespace SqlOS.AuditLogs.Projections;

/// <summary>
/// The 7.2.1 <c>user.email-verification-*</c> rows of the verification request flow, each with the
/// metadata <c>SqlOSAuthService.RequestEmailVerificationAsync</c> built: the masked address first,
/// then the action's details, member for member.
/// </summary>
internal static class EmailVerificationAuditProjections
{
    public static SqlOSAuditProjectionBuilder AddEmailVerificationEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            .Audit<EmailVerificationRequested>(static (requested, context) => AuthServerAuditRows.System(
                "user.email-verification-requested",
                context,
                userId: requested.UserId,
                ipAddress: requested.IpAddress,
                data: new
                {
                    maskedEmail = requested.MaskedEmail,
                    eligible = requested.Eligible
                }))
            .Audit<EmailVerificationEmailSent>(static (sent, context) => AuthServerAuditRows.System(
                "user.email-verification-sent",
                context,
                userId: sent.UserId,
                ipAddress: sent.IpAddress,
                data: new
                {
                    maskedEmail = sent.MaskedEmail,
                    sent.DeliveryId,
                    sent.DeliveryStatus,
                    sent.ProviderMessageId
                }))
            .Audit<EmailVerificationEmailFailed>(static (failed, context) => AuthServerAuditRows.System(
                "user.email-verification-send-failed",
                context,
                userId: failed.UserId,
                ipAddress: failed.IpAddress,
                data: new
                {
                    maskedEmail = failed.MaskedEmail,
                    error = failed.Error
                }));
}
