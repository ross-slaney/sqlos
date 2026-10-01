using SqlOS.Domain.Events;

namespace SqlOS.AuditLogs.Projections;

/// <summary>The audit rows of temporary tokens' lifecycle (7.2.1 audited only email-verification links).</summary>
internal static class TemporaryTokenAuditProjections
{
    public static SqlOSAuditProjectionBuilder AddTemporaryTokenEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            .Unaudited<TemporaryTokenIssued>()
            .Unaudited<TemporaryTokenConsumed>()
            .Unaudited<TemporaryTokenRetired>()
            .Unaudited<TemporaryTokenPayloadReplaced>()
            .Audit<EmailVerificationTokenCreated>(static (created, context) => AuthServerAuditRows.System(
                "user.email-verification-token-created",
                context,
                userId: created.UserId));
}
