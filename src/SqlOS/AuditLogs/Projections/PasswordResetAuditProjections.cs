using SqlOS.AuthServer.Models;
using SqlOS.Domain.Events;

namespace SqlOS.AuditLogs.Projections;

/// <summary>
/// The 7.2.1 <c>password_reset.*</c> rows. Each builds the metadata exactly as
/// <c>SqlOSAuthService.RecordPasswordResetAuditAsync</c> did: <c>maskedEmail</c>, and the call
/// site's <c>details</c> object with its member names as written there.
/// </summary>
internal static class PasswordResetAuditProjections
{
    public static SqlOSAuditProjectionBuilder AddPasswordResetEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            .Audit<PasswordResetSendRateLimited>(static (limited, context) => Row(
                "password_reset.rate_limit_rejected",
                "system",
                actorId: null,
                limited.UserId,
                limited.MaskedEmail,
                limited.IpAddress,
                new
                {
                    scope = limited.Scope,
                    retryAfter = limited.RetryAfter,
                    clientKey = limited.ClientKey
                },
                context))
            .Audit<PasswordResetRequested>(static (requested, context) => Row(
                "password_reset.requested",
                "system",
                actorId: null,
                requested.UserId,
                requested.MaskedEmail,
                requested.IpAddress,
                new
                {
                    eligible = requested.Eligible,
                    clientKey = requested.ClientKey
                },
                context))
            .Audit<PasswordResetMessageSent>(static (sent, context) => Row(
                "password_reset.email_sent",
                "user",
                sent.UserId,
                sent.UserId,
                sent.MaskedEmail,
                sent.IpAddress,
                new
                {
                    deliveryId = sent.DeliveryId,
                    customMessage = true
                },
                context))
            .Audit<PasswordResetEmailSent>(static (sent, context) => Row(
                "password_reset.email_sent",
                "user",
                sent.UserId,
                sent.UserId,
                sent.MaskedEmail,
                sent.IpAddress,
                new
                {
                    sent.DeliveryId,
                    sent.DeliveryStatus,
                    sent.ProviderMessageId
                },
                context))
            .Audit<PasswordResetEmailFailed>(static (failed, context) => Row(
                "password_reset.email_send_failed",
                "system",
                actorId: null,
                failed.UserId,
                failed.MaskedEmail,
                failed.IpAddress,
                new { error = failed.Error },
                context))
            .Audit<PasswordResetEmailSentByOperator>(static (sent, context) => Row(
                "password_reset.admin_email_sent",
                "admin",
                actorId: null,
                sent.UserId,
                sent.MaskedEmail,
                sent.IpAddress,
                new
                {
                    sent.DeliveryId,
                    sent.DeliveryStatus
                },
                context))
            .Audit<PasswordResetLinkRefused>(static (refused, context) => Row(
                "password_reset.invalid_or_expired",
                "system",
                actorId: null,
                refused.UserId,
                maskedEmail: null,
                ipAddress: null,
                new { reason = refused.Reason },
                context))
            .Audit<PasswordResetCompleted>(static (completed, context) => Row(
                "password_reset.completed",
                "user",
                completed.UserId,
                completed.UserId,
                maskedEmail: null,
                ipAddress: null,
                details: null,
                context));

    private static SqlOSAuditEvent Row(
        string eventType,
        string actorType,
        string? actorId,
        string? userId,
        string? maskedEmail,
        string? ipAddress,
        object? details,
        SqlOSAuditProjectionContext context)
        => AuthServerAuditRows.Row(
            eventType,
            actorType,
            actorId,
            context,
            userId: userId,
            ipAddress: ipAddress,
            data: new
            {
                maskedEmail,
                details
            });
}
