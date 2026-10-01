using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using static SqlOS.Tests.Domain.DomainTime;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class SqlOSPhoneOtpChallengeTests
{
    private static readonly PhoneNumber Number = PhoneNumber.Parse("+12025550137", "US");
    private static readonly PhoneOtpChallengeContext Context = new("req_1", "cli_1", RequestedOrganizationId: null);

    [TestMethod]
    public void Issuing_keeps_the_recipient_protected_hashed_and_masked()
    {
        var issued = SqlOSPhoneOtpChallenge.Issue(Request(ip: "203.0.113.10", userAgent: "UA"), "protected:+12025550137", TimeSpan.FromMinutes(10), Now);

        var challenge = issued.Challenge;
        issued.Recipient.Should().Be(Number, "the provider sends to the challenge's recipient");
        challenge.Id.Should().MatchRegex("^potp_[0-9a-f]{24}$");
        challenge.ChallengeTokenHash.Should().Be(HashedSecret.Sha256(issued.ChallengeToken).Hash);
        challenge.PhoneNumberHash.Should().Be(HashedSecret.Sha256("+12025550137").Hash);
        challenge.PhoneNumberEncrypted.Should().Be("protected:+12025550137");
        challenge.MaskedPhoneNumber.Should().Be("+1******0137");
        challenge.Purpose.Should().Be("login");
        challenge.UserId.Should().Be("usr_1");
        challenge.UserPhoneNumberId.Should().Be("phn_1");
        challenge.AuthorizationRequestId.Should().Be("req_1");
        challenge.ClientApplicationId.Should().Be("cli_1");
        challenge.ProviderStarted.Should().BeFalse();
        challenge.Provider.Should().Be("twilio_verify");
        challenge.AttemptCount.Should().Be(0);
        challenge.CreatedAt.Should().Be(Now);
        challenge.LastSentAt.Should().Be(Now);
        challenge.ExpiresAt.Should().Be(Now.AddMinutes(10));
        challenge.IpAddress.Should().Be("203.0.113.10");
        challenge.UserAgent.Should().Be("UA");
        Events(challenge).Should().Equal(new PhoneOtpChallengeIssued(challenge.Id));
        issued.ToString().Should().NotContain(issued.ChallengeToken);
    }

    [TestMethod]
    public void A_challenge_is_open_until_it_is_spent_invalidated_or_expired()
    {
        Issue().IsOpen(Now).Should().BeTrue();
        Issue().IsOpen(Now.AddMinutes(10)).Should().BeFalse();

        var rejected = Issue();
        rejected.RejectCode("not_started", Now);
        rejected.IsOpen(Now).Should().BeFalse();

        var completed = Issue();
        completed.Complete(Now);
        completed.IsOpen(Now).Should().BeFalse();
    }

    [TestMethod]
    public void Purpose_request_and_context_compare_exactly()
    {
        var challenge = Issue();

        challenge.IsFor("login").Should().BeTrue();
        challenge.IsFor("signup").Should().BeFalse();
        challenge.AnswersAuthorizationRequest("req_1").Should().BeTrue();
        challenge.AnswersAuthorizationRequest(null).Should().BeFalse();
        challenge.WasStartedIn(Context, "login").Should().BeTrue();
        challenge.WasStartedIn(Context, "signup").Should().BeFalse();
        challenge.WasStartedIn(Context with { ClientApplicationId = "CLI_1" }, "login").Should().BeFalse();
        challenge.WasSentWithin(TimeSpan.FromSeconds(30), Now.AddSeconds(29)).Should().BeTrue();
        challenge.WasSentWithin(TimeSpan.FromSeconds(30), Now.AddSeconds(30)).Should().BeFalse();
    }

    [TestMethod]
    public void A_provider_start_records_the_send_and_the_start()
    {
        var challenge = Issue();
        Drain(challenge);

        challenge.RecordProviderStart(new SqlOSOtpDeliveryStartResult(true, "twilio_verify", "VE1", "pending"));

        challenge.ProviderStarted.Should().BeTrue();
        challenge.Provider.Should().Be("twilio_verify");
        challenge.ProviderChallengeId.Should().Be("VE1");
        challenge.ProviderStatus.Should().Be("pending");
        Events(challenge).Should().Equal(new PhoneOtpChallengeStarted(challenge.Id, "login", "+1******0137", null, "cli_1", "req_1", null, true));

        var withheld = Issue();
        Drain(withheld);
        withheld.RecordStartWithoutSending();
        withheld.ProviderStarted.Should().BeFalse();
        Events(withheld).Should().Equal(new PhoneOtpChallengeStarted(withheld.Id, "login", "+1******0137", null, "cli_1", "req_1", null, false));
    }

    [TestMethod]
    public void A_refused_send_invalidates_the_challenge_and_is_recorded()
    {
        var challenge = Issue();
        Drain(challenge);

        challenge.FailDelivery(new SqlOSOtpDeliveryStartResult(false, "twilio_verify", null, "blocked"), Now);

        challenge.InvalidatedReason.Should().Be("delivery_failed");
        challenge.ProviderStatus.Should().Be("blocked");
        Events(challenge).Should().Equal(new PhoneOtpDeliveryFailed(challenge.Id, "login", "+1******0137", null, "cli_1", null, "blocked"));
    }

    [TestMethod]
    public void A_challenge_gets_one_provider_check()
    {
        var challenge = Issue();
        challenge.RecordProviderStart(new SqlOSOtpDeliveryStartResult(true, "twilio_verify", "VE1", "pending"));

        challenge.RegisterCheck(new SqlOSOtpDeliveryCheckResult(false, "twilio_verify", null, "pending"))
            .Should().Be(PhoneOtpCheckOutcome.Rejected);

        challenge.AttemptCount.Should().Be(1);
        challenge.ProviderStatus.Should().Be("pending");
        challenge.ProviderChallengeId.Should().Be("VE1", "a check without a provider challenge keeps the one the send recorded");
        FluentActions.Invoking(() => challenge.RegisterCheck(new SqlOSOtpDeliveryCheckResult(true, "twilio_verify", "VE2", "approved")))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.AttemptsExhausted);
    }

    [TestMethod]
    public void An_approved_check_completes_the_challenge_and_is_audited_with_the_providers_status()
    {
        var challenge = Issue();
        challenge.RecordProviderStart(new SqlOSOtpDeliveryStartResult(true, "twilio_verify", "VE1", "pending"));
        Drain(challenge);

        challenge.RegisterCheck(new SqlOSOtpDeliveryCheckResult(true, "twilio_verify", "VE2", "approved")).Should().Be(PhoneOtpCheckOutcome.Approved);
        challenge.Complete(Now.AddMinutes(1));

        challenge.ConsumedAt.Should().Be(Now.AddMinutes(1));
        challenge.ProviderChallengeId.Should().Be("VE2");
        Events(challenge).Should().Equal(
            new PhoneOtpChallengeConsumed(challenge.Id),
            new PhoneOtpCodeAccepted(challenge.Id, "login", "+1******0137", null, "usr_1", "cli_1", "req_1", "approved"));
        FluentActions.Invoking(() => challenge.Complete(Now.AddMinutes(2)))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.AlreadyConsumed);
    }

    [TestMethod]
    public void A_rejected_code_invalidates_the_challenge_with_the_reason_the_column_holds()
    {
        var challenge = Issue();
        Drain(challenge);
        var longReason = new string('x', 150);

        challenge.RejectCode(longReason, Now);

        challenge.InvalidatedReason.Should().Be(new string('x', 120));
        Events(challenge).Should().Equal(new PhoneOtpCodeRejected(challenge.Id, "login", "+1******0137", null, "cli_1", "req_1", new string('x', 120)));
    }

    [TestMethod]
    public void Superseding_withdrawing_and_enrolling_are_recorded()
    {
        var superseded = Issue();
        Drain(superseded);
        superseded.Supersede(Now);
        superseded.Supersede(Now.AddSeconds(1));
        superseded.InvalidatedReason.Should().Be("superseded");
        Events(superseded).Should().Equal(new PhoneOtpChallengeSuperseded(superseded.Id));

        var withdrawn = Issue();
        Drain(withdrawn);
        withdrawn.Withdraw("user_deactivated", Now);
        Events(withdrawn).Should().Equal(new PhoneOtpChallengeWithdrawn(withdrawn.Id, "user_deactivated"));

        var enrollment = Issue();
        Drain(enrollment);
        enrollment.RecordEnrollment("usr_1", "phn_9");
        Events(enrollment).Should().Equal(new PhoneOtpPhoneEnrolled(enrollment.Id, "+1******0137", null, "usr_1", "phn_9"));
    }

    private static PhoneOtpChallengeRequest Request(string? ip = null, string? userAgent = null)
        => new(Number, "login", Context, "usr_1", "phn_1", ip, userAgent);

    private static SqlOSPhoneOtpChallenge Issue()
        => SqlOSPhoneOtpChallenge.Issue(Request(), "protected", TimeSpan.FromMinutes(10), Now).Challenge;

    private static IReadOnlyList<ISqlOSDomainEvent> Events(SqlOSPhoneOtpChallenge challenge)
        => ((ISqlOSAggregate)challenge).Events.Drain().Select(raised => raised.Event).ToList();

    private static void Drain(SqlOSPhoneOtpChallenge challenge) => ((ISqlOSAggregate)challenge).Events.Drain();
}
