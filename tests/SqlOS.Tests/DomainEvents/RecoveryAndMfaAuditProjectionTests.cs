using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.Tests.DomainEvents;

/// <summary>
/// The password-reset, email-verification and MFA events project exactly the rows 7.2.1 wrote for
/// them. Each expected row is built the way the 7.2.1 call site built it: the same
/// <c>RecordAuditAsync</c> arguments and the same anonymous metadata objects, member for member.
/// </summary>
[TestClass]
public sealed class RecoveryAndMfaAuditProjectionTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Ip = "203.0.113.10";

    [TestMethod]
    public void Password_reset_events_project_the_7_2_1_password_reset_rows()
    {
        var retryAfter = new DateTime(2026, 10, 1, 13, 0, 0, DateTimeKind.Utc);
        AssertRow(
            new PasswordResetSendRateLimited("usr_1", "al***@example.test", Ip, "password-reset-email", retryAfter, "client-1"),
            PasswordReset721("password_reset.rate_limit_rejected", "system", null, "usr_1", "al***@example.test", Ip, new
            {
                scope = "password-reset-email",
                retryAfter = (DateTime?)retryAfter,
                clientKey = "client-1"
            }));
        AssertRow(
            new PasswordResetSendRateLimited(null, "un***@example.test", null, "password-reset-ip", null, null),
            PasswordReset721("password_reset.rate_limit_rejected", "system", null, null, "un***@example.test", null, new
            {
                scope = "password-reset-ip",
                retryAfter = (DateTime?)null,
                clientKey = (string?)null
            }));
        AssertRow(
            new PasswordResetRequested("tmp_1", "usr_1", "al***@example.test", Ip, true, "client-1"),
            PasswordReset721("password_reset.requested", "system", null, "usr_1", "al***@example.test", Ip, new
            {
                eligible = true,
                clientKey = "client-1"
            }));
        AssertRow(
            new PasswordResetMessageSent("tmp_1", "usr_1", "al***@example.test", Ip, "edl_1"),
            PasswordReset721("password_reset.email_sent", "user", "usr_1", "usr_1", "al***@example.test", Ip, new
            {
                deliveryId = "edl_1",
                customMessage = true
            }));
        AssertRow(
            new PasswordResetEmailSent("tmp_1", "usr_1", "al***@example.test", Ip, "edl_1", "queued", "provider-1"),
            PasswordReset721("password_reset.email_sent", "user", "usr_1", "usr_1", "al***@example.test", Ip, new
            {
                DeliveryId = "edl_1",
                DeliveryStatus = "queued",
                ProviderMessageId = (string?)"provider-1"
            }));
        AssertRow(
            new PasswordResetEmailFailed("usr_1", "al***@example.test", Ip, "Password reset email delivery failed."),
            PasswordReset721("password_reset.email_send_failed", "system", null, "usr_1", "al***@example.test", Ip, new
            {
                error = "Password reset email delivery failed."
            }));
        AssertRow(
            new PasswordResetEmailSentByOperator("tmp_1", "usr_1", "al***@example.test", Ip, "edl_1", "queued"),
            PasswordReset721("password_reset.admin_email_sent", "admin", null, "usr_1", "al***@example.test", Ip, new
            {
                DeliveryId = "edl_1",
                DeliveryStatus = "queued"
            }));
        AssertRow(
            new PasswordResetLinkRefused(null, "missing_or_consumed"),
            PasswordReset721("password_reset.invalid_or_expired", "system", null, null, null, null, new { reason = "missing_or_consumed" }));
        AssertRow(
            new PasswordResetLinkRefused("usr_1", "inactive_user"),
            PasswordReset721("password_reset.invalid_or_expired", "system", null, "usr_1", null, null, new { reason = "inactive_user" }));
        AssertRow(
            new PasswordResetCompleted("tmp_1", "usr_1"),
            PasswordReset721("password_reset.completed", "user", "usr_1", "usr_1", null, null, null));
    }

    [TestMethod]
    public void Email_verification_events_project_the_7_2_1_verification_rows()
    {
        // SqlOSAuthService.RequestEmailVerificationAsync: RecordAuditAsync(eventType, "system", null,
        // userId, ipAddress, data: { maskedEmail, ... }).
        AssertRow(
            new EmailVerificationRequested("usr_1", "al***@example.test", Ip, Eligible: true),
            Row721("user.email-verification-requested", "system", null, "usr_1", null, Ip, new
            {
                maskedEmail = "al***@example.test",
                eligible = true
            }));
        AssertRow(
            new EmailVerificationRequested(null, "un***@example.test", null, Eligible: false),
            Row721("user.email-verification-requested", "system", null, null, null, null, new
            {
                maskedEmail = "un***@example.test",
                eligible = false
            }));
        AssertRow(
            new EmailVerificationEmailSent("tmp_1", "usr_1", "al***@example.test", Ip, "edl_1", "sent", null),
            Row721("user.email-verification-sent", "system", null, "usr_1", null, Ip, new
            {
                maskedEmail = "al***@example.test",
                DeliveryId = "edl_1",
                DeliveryStatus = "sent",
                ProviderMessageId = (string?)null
            }));
        AssertRow(
            new EmailVerificationEmailFailed("usr_1", "al***@example.test", Ip, "Email verification delivery failed."),
            Row721("user.email-verification-send-failed", "system", null, "usr_1", null, Ip, new
            {
                maskedEmail = "al***@example.test",
                error = "Email verification delivery failed."
            }));
    }

    [TestMethod]
    public void Mfa_failure_records_project_the_7_2_1_mfa_rows()
    {
        // SqlOSAuthService.TryRecordMfaChallengeAuditAsync.
        AssertRow(
            new MfaChallengeFailed("tmp_1", "usr_1", "org_1", Ip, AttemptCount: 5, ChallengeLocked: true),
            Row721("user.mfa.challenge_failed", "system", null, "usr_1", "org_1", Ip, new
            {
                challengeId = "tmp_1",
                details = new
                {
                    attemptCount = 5,
                    challengeLocked = true
                }
            }));
        // SqlOSAuthService.RecordRejectedChallengeEnrollmentAsync, which recorded no IP address.
        AssertRow(
            new MfaChallengeEnrollmentRejected("tmp_1", "usr_1", null, "cli_1", "start"),
            Row721("user.mfa.enrollment.challenge_rejected", "user", "usr_1", "usr_1", null, null, new
            {
                stage = "start",
                challenge_id = "tmp_1",
                client_application_id = "cli_1"
            }));
        AssertRow(
            new MfaChallengeEnrollmentRejected("tmp_1", null, "org_1", null, "start"),
            Row721("user.mfa.enrollment.challenge_rejected", "user", null, null, "org_1", null, new
            {
                stage = "start",
                challenge_id = "tmp_1",
                client_application_id = (string?)null
            }));
    }

    [TestMethod]
    public void Every_recovery_and_mfa_outcome_belongs_to_its_tokens_kind()
    {
        new PasswordResetRequested("tmp_1", null, "x", null, false, null).Kind.Should().BeSameAs(SqlOSTemporaryTokenKinds.PasswordResetRequest);
        new PasswordResetEmailSent("tmp_1", "usr_1", "x", null, "edl_1", "queued", null).Kind.Should().BeSameAs(SqlOSTemporaryTokenKinds.PasswordReset);
        new PasswordResetMessageSent("tmp_1", "usr_1", "x", null, "edl_1").Kind.Should().BeSameAs(SqlOSTemporaryTokenKinds.PasswordReset);
        new PasswordResetEmailSentByOperator("tmp_1", "usr_1", "x", null, "edl_1", "queued").Kind.Should().BeSameAs(SqlOSTemporaryTokenKinds.PasswordReset);
        new PasswordResetCompleted("tmp_1", "usr_1").Kind.Should().BeSameAs(SqlOSTemporaryTokenKinds.PasswordReset);
        new EmailVerificationEmailSent("tmp_1", "usr_1", "x", null, "edl_1", "sent", null).Kind.Should().BeSameAs(SqlOSTemporaryTokenKinds.EmailVerification);
    }

    // The 7.2.1 SqlOSAuthService.RecordPasswordResetAuditAsync.
    private static SqlOSAuditEvent PasswordReset721(
        string eventType,
        string actorType,
        string? actorId,
        string? userId,
        string? maskedEmail,
        string? ipAddress,
        object? details)
        => Row721(eventType, actorType, actorId, userId, null, ipAddress, new
        {
            maskedEmail,
            details
        });

    // The 7.2.1 SqlOSAdminService.RecordAuditAsync(eventType, actorType, actorId, userId, organizationId, ipAddress: ..., data: ...).
    private static SqlOSAuditEvent Row721(
        string eventType,
        string actorType,
        string? actorId,
        string? userId,
        string? organizationId,
        string? ipAddress,
        object? data)
        => SqlOSAuditRows.Create(
            SqlOSAuditRows.AuthServerRequest(eventType, actorType, actorId, userId: userId, organizationId: organizationId, ipAddress: ipAddress, data: data),
            "evt_expected",
            Now);

    private static void AssertRow(ISqlOSDomainEvent domainEvent, SqlOSAuditEvent expected)
    {
        var projected = SqlOSAuditProjection.Default.Project(domainEvent, new SqlOSAuditProjectionContext(Now, SqlOSRequestContext.System));

        projected.Should().NotBeNull($"{domainEvent.GetType().Name} is audited");
        projected!.Id.Should().MatchRegex("^evt_[0-9a-f]{24}$");
        projected.Should().BeEquivalentTo(expected, options => options.Excluding(row => row.Id), $"{domainEvent.GetType().Name} projects the 7.2.1 {expected.EventType} row");
        projected.MetadataJson.Should().Be(expected.MetadataJson, "the metadata is byte-identical, member order included");
        projected.ContextJson.Should().Be(expected.ContextJson);
        projected.TargetsJson.Should().Be(expected.TargetsJson);
    }
}
