using SqlOS.AuthServer.Models;
using SqlOS.Domain.Events;

namespace SqlOS.AuditLogs.Projections;

/// <summary>
/// The 7.2.1 <c>magic_link.*</c> rows. Each builds the metadata exactly as
/// <c>SqlOSMagicLinkService.RecordMagicLinkAuditAsync</c> did: <c>phase</c>, <c>maskedEmail</c>, and
/// the call site's <c>details</c> object with its member names as written there.
/// </summary>
internal static class MagicLinkAuditProjections
{
    public static SqlOSAuditProjectionBuilder AddMagicLinkEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            .Audit<MagicLinkRequested>(static (requested, context) => Row(
                "magic_link.requested",
                "start",
                requested.MaskedEmail,
                requested.IpAddress,
                new
                {
                    clientApplicationId = requested.ClientApplicationId,
                    authorizationRequestId = requested.AuthorizationRequestId,
                    requestedOrganizationId = requested.RequestedOrganizationId,
                    sent = requested.Sent
                },
                context))
            .Audit<MagicLinkDeliveryFailed>(static (failed, context) => Row(
                "magic_link.send_failed",
                "start",
                failed.MaskedEmail,
                failed.IpAddress,
                new
                {
                    clientApplicationId = failed.ClientApplicationId,
                    authorizationRequestId = failed.AuthorizationRequestId,
                    requestedOrganizationId = failed.RequestedOrganizationId
                },
                context))
            .Audit<MagicLinkCompleted>(static (completed, context) => Row(
                "magic_link.completed",
                "complete",
                completed.MaskedEmail,
                completed.IpAddress,
                new
                {
                    Id = completed.UserId,
                    completed.ClientApplicationId,
                    completed.AuthorizationRequestId,
                    completed.RequestedOrganizationId
                },
                context))
            .Audit<MagicLinkSendRateLimited>(static (limited, context) => Row(
                "magic_link.rate_limit_rejected",
                "start",
                limited.MaskedEmail,
                limited.IpAddress,
                new
                {
                    limit = limited.Limit,
                    clientApplicationId = limited.ClientApplicationId,
                    requestedOrganizationId = limited.RequestedOrganizationId
                },
                context))
            .Audit<MagicLinkNotFound>(static (_, context) => Row(
                "magic_link.rejected",
                "complete",
                maskedEmail: null,
                ipAddress: null,
                new { reason = "missing_expired_or_replayed" },
                context))
            .Audit<MagicLinkReplayed>(static (replayed, context) => Row(
                "magic_link.rejected",
                "complete",
                replayed.MaskedEmail,
                replayed.IpAddress,
                new
                {
                    reason = "replayed",
                    replayed.ClientApplicationId,
                    replayed.AuthorizationRequestId
                },
                context));

    private static SqlOSAuditEvent Row(
        string eventType,
        string phase,
        string? maskedEmail,
        string? ipAddress,
        object details,
        SqlOSAuditProjectionContext context)
        => AuthServerAuditRows.System(
            eventType,
            context,
            ipAddress: ipAddress,
            data: new
            {
                phase,
                maskedEmail,
                details
            });
}
