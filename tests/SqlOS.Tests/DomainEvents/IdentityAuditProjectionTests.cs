using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.Tests.DomainEvents;

/// <summary>
/// The email-code, phone-code, sign-in-link and temporary-token events project exactly the rows
/// 7.2.1 wrote for them. Each expected row is built the way the 7.2.1 call site built it: the same
/// <c>RecordAuditAsync</c> arguments and the same anonymous metadata objects, member for member.
/// </summary>
[TestClass]
public sealed class IdentityAuditProjectionTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private const string Ip = "203.0.113.10";

    [TestMethod]
    public void Email_code_events_project_the_7_2_1_email_otp_rows()
    {
        AssertRow(
            new EmailOtpChallengeStarted("otp_1", "login", "al***@example.test", Ip, "cli_1", "req_1", "org_1", true),
            EmailOtp721("email_otp.challenge_started", "al***@example.test", "login", Ip, new
            {
                clientApplicationId = "cli_1",
                authorizationRequestId = "req_1",
                requestedOrganizationId = "org_1",
                sent = true
            }));
        AssertRow(
            new EmailOtpDeliveryFailed("otp_1", "signup", "al***@example.test", Ip, "cli_1", null),
            EmailOtp721("email_otp.send_failed", "al***@example.test", "signup", Ip, new
            {
                clientApplicationId = "cli_1",
                requestedOrganizationId = (string?)null
            }));
        AssertRow(
            new EmailOtpCodeAccepted("otp_1", "login", "al***@example.test", Ip, "usr_1", "cli_1", "req_1"),
            EmailOtp721("email_otp.verify_succeeded", "al***@example.test", "login", Ip, new
            {
                UserId = "usr_1",
                ClientApplicationId = "cli_1",
                AuthorizationRequestId = "req_1"
            }));
        AssertRow(
            new EmailOtpCodeRejected("otp_1", "signup", "al***@example.test", Ip, "cli_1", null, "max_attempts"),
            EmailOtp721("email_otp.verify_failed", "al***@example.test", "signup", Ip, new
            {
                ClientApplicationId = "cli_1",
                AuthorizationRequestId = (string?)null,
                reason = "max_attempts"
            }));
        AssertRow(
            new EmailOtpSendRateLimited("login", "al***@example.test", Ip, "ip", "cli_1", null),
            EmailOtp721("email_otp.rate_limit_rejected", "al***@example.test", "login", Ip, new
            {
                limit = "ip",
                clientApplicationId = "cli_1",
                requestedOrganizationId = (string?)null
            }));
        AssertRow(
            new EmailOtpSignupStartedForExistingEmail("al***@example.test", Ip, "cli_1", "req_1", "org_1"),
            EmailOtp721("email_otp.signup_existing_email", "al***@example.test", "signup", Ip, new
            {
                clientApplicationId = "cli_1",
                authorizationRequestId = "req_1",
                requestedOrganizationId = "org_1",
                reason = "existing_email"
            }));
        AssertRow(
            new EmailOtpSignupRejectedForExistingEmail("al***@example.test", Ip, "cli_1", null, "challenge_bound_to_existing_user"),
            EmailOtp721("email_otp.signup_existing_email_rejected", "al***@example.test", "signup", Ip, new
            {
                ClientApplicationId = "cli_1",
                AuthorizationRequestId = (string?)null,
                reason = "challenge_bound_to_existing_user"
            }));
    }

    [TestMethod]
    public void Phone_code_events_project_the_7_2_1_phone_otp_rows()
    {
        AssertRow(
            new PhoneOtpChallengeStarted("potp_1", "signup", "+1******0137", Ip, "cli_1", null, null, false),
            PhoneOtp721("phone_otp.challenge_started", "+1******0137", "signup", Ip, new
            {
                clientApplicationId = "cli_1",
                authorizationRequestId = (string?)null,
                requestedOrganizationId = (string?)null,
                sent = false
            }));
        AssertRow(
            new PhoneOtpDeliveryFailed("potp_1", "login", "+1******0137", Ip, "cli_1", "org_1", "blocked"),
            PhoneOtp721("phone_otp.send_failed", "+1******0137", "login", Ip, new
            {
                clientApplicationId = "cli_1",
                requestedOrganizationId = "org_1",
                providerStatus = "blocked"
            }));
        AssertRow(
            new PhoneOtpCodeAccepted("potp_1", "login", "+1******0137", Ip, "usr_1", "cli_1", "req_1", "approved"),
            PhoneOtp721("phone_otp.verify_succeeded", "+1******0137", "login", Ip, new
            {
                UserId = "usr_1",
                ClientApplicationId = "cli_1",
                AuthorizationRequestId = "req_1",
                providerStatus = "approved"
            }));
        AssertRow(
            new PhoneOtpCodeRejected("potp_1", "enrollment", "+1******0137", Ip, null, null, "not_started"),
            PhoneOtp721("phone_otp.verify_failed", "+1******0137", "enrollment", Ip, new
            {
                ClientApplicationId = (string?)null,
                AuthorizationRequestId = (string?)null,
                reason = "not_started"
            }));
        AssertRow(
            new PhoneOtpPhoneEnrolled("potp_1", "+1******0137", Ip, "usr_1", "phn_1"),
            PhoneOtp721("phone_otp.phone_added", "+1******0137", "enrollment", Ip, new
            {
                userId = "usr_1",
                phoneNumberId = "phn_1"
            }));
        AssertRow(
            new PhoneOtpSendRateLimited("login", "+1******0137", Ip, "phone", "cli_1", null),
            PhoneOtp721("phone_otp.rate_limit_rejected", "+1******0137", "login", Ip, new
            {
                limit = "phone",
                clientApplicationId = "cli_1",
                requestedOrganizationId = (string?)null
            }));
    }

    [TestMethod]
    public void Sign_in_link_events_project_the_7_2_1_magic_link_rows()
    {
        AssertRow(
            new MagicLinkRequested("tmp_1", "al***@example.test", Ip, "cli_1", "req_1", null, true),
            MagicLink721("magic_link.requested", "al***@example.test", "start", Ip, new
            {
                clientApplicationId = "cli_1",
                authorizationRequestId = "req_1",
                requestedOrganizationId = (string?)null,
                sent = true
            }));
        AssertRow(
            new MagicLinkDeliveryFailed("tmp_1", "al***@example.test", Ip, "cli_1", null, "org_1"),
            MagicLink721("magic_link.send_failed", "al***@example.test", "start", Ip, new
            {
                clientApplicationId = "cli_1",
                authorizationRequestId = (string?)null,
                requestedOrganizationId = "org_1"
            }));
        AssertRow(
            new MagicLinkCompleted("tmp_1", "al***@example.test", Ip, "usr_1", "cli_1", "req_1", null),
            MagicLink721("magic_link.completed", "al***@example.test", "complete", Ip, new
            {
                Id = "usr_1",
                ClientApplicationId = "cli_1",
                AuthorizationRequestId = "req_1",
                RequestedOrganizationId = (string?)null
            }));
        AssertRow(
            new MagicLinkSendRateLimited("al***@example.test", Ip, "email", null, null),
            MagicLink721("magic_link.rate_limit_rejected", "al***@example.test", "start", Ip, new
            {
                limit = "email",
                clientApplicationId = (string?)null,
                requestedOrganizationId = (string?)null
            }));
        AssertRow(
            new MagicLinkNotFound(),
            MagicLink721("magic_link.rejected", null, "complete", null, new { reason = "missing_expired_or_replayed" }));
        AssertRow(
            new MagicLinkReplayed("al***@example.test", Ip, "cli_1", null),
            MagicLink721("magic_link.rejected", "al***@example.test", "complete", Ip, new
            {
                reason = "replayed",
                ClientApplicationId = "cli_1",
                AuthorizationRequestId = (string?)null
            }));
    }

    [TestMethod]
    public void Creating_an_email_verification_link_projects_the_7_2_1_row()
        => AssertRow(
            new EmailVerificationTokenCreated("tmp_1", "usr_1"),
            SqlOSAuditRows.Create(
                SqlOSAuditRows.AuthServerRequest("user.email-verification-token-created", "system", null, userId: "usr_1"),
                "evt_expected",
                Now));

    [TestMethod]
    public void Lifecycle_events_without_a_7_2_1_row_project_none()
    {
        ISqlOSDomainEvent[] unaudited =
        [
            new EmailOtpChallengeIssued("otp_1"),
            new EmailOtpChallengeSuperseded("otp_1"),
            new EmailOtpChallengeWithdrawn("otp_1", "password_reset"),
            new EmailOtpChallengeConsumed("otp_1"),
            new PhoneOtpChallengeIssued("potp_1"),
            new PhoneOtpChallengeSuperseded("potp_1"),
            new PhoneOtpChallengeWithdrawn("potp_1", "password_reset"),
            new PhoneOtpChallengeConsumed("potp_1"),
            new TemporaryTokenIssued("tmp_1", "pending_auth"),
            new TemporaryTokenConsumed("tmp_1", "pending_auth"),
            new TemporaryTokenRetired("tmp_1", "pending_auth"),
            new TemporaryTokenPayloadReplaced("tmp_1", "mfa_challenge")
        ];

        unaudited.Select(domainEvent => SqlOSAuditProjection.Default.Project(domainEvent, Context())).Should().OnlyContain(row => row == null);
    }

    // The 7.2.1 SqlOSEmailOtpService.RecordOtpAuditAsync.
    private static SqlOSAuditEvent EmailOtp721(string eventType, string maskedEmail, string purpose, string? ipAddress, object? data)
        => Row721(eventType, ipAddress, new
        {
            purpose,
            maskedEmail,
            details = data
        });

    // The 7.2.1 SqlOSPhoneOtpService.RecordPhoneOtpAuditAsync.
    private static SqlOSAuditEvent PhoneOtp721(string eventType, string maskedPhone, string purpose, string? ipAddress, object? data)
        => Row721(eventType, ipAddress, new
        {
            purpose,
            maskedPhone,
            details = data
        });

    // The 7.2.1 SqlOSMagicLinkService.RecordMagicLinkAuditAsync.
    private static SqlOSAuditEvent MagicLink721(string eventType, string? maskedEmail, string phase, string? ipAddress, object? data)
        => Row721(eventType, ipAddress, new
        {
            phase,
            maskedEmail,
            details = data
        });

    // The 7.2.1 SqlOSAdminService.RecordAuditAsync(eventType, "system", null, ipAddress: ..., data: ...).
    private static SqlOSAuditEvent Row721(string eventType, string? ipAddress, object data)
        => SqlOSAuditRows.Create(
            SqlOSAuditRows.AuthServerRequest(eventType, "system", null, ipAddress: ipAddress, data: data),
            "evt_expected",
            Now);

    private static void AssertRow(ISqlOSDomainEvent domainEvent, SqlOSAuditEvent expected)
    {
        var projected = SqlOSAuditProjection.Default.Project(domainEvent, Context());

        projected.Should().NotBeNull($"{domainEvent.GetType().Name} is audited");
        projected!.Id.Should().MatchRegex("^evt_[0-9a-f]{24}$");
        projected.Should().BeEquivalentTo(expected, options => options.Excluding(row => row.Id), $"{domainEvent.GetType().Name} projects the 7.2.1 {expected.EventType} row");
        projected.MetadataJson.Should().Be(expected.MetadataJson, "the metadata is byte-identical, member order included");
        projected.ContextJson.Should().Be(expected.ContextJson);
    }

    private static SqlOSAuditProjectionContext Context() => new(Now, SqlOSRequestContext.System);
}
