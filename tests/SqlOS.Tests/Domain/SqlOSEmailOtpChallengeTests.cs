using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using SqlOS.Tests.Infrastructure;
using static SqlOS.Tests.Domain.DomainTime;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class SqlOSEmailOtpChallengeTests
{
    private static readonly EmailOtpChallengeSettings Settings = new(CodeLength: 6, MaxAttempts: 5, Lifetime: TimeSpan.FromMinutes(10));
    private static readonly EmailOtpChallengeContext Context = new("req_1", "cli_1", RequestedOrganizationId: null);

    [TestMethod]
    public void A_challenge_for_an_account_goes_only_to_the_address_stored_on_it()
    {
        var account = AccountEmail("usr_1", "  alice@example.test ");

        var issued = SqlOSEmailOtpChallenge.Issue(Request("ALICE@Example.TEST"), account, Settings, Now);

        var challenge = issued.Challenge;
        challenge.Email.Should().Be("alice@example.test", "the stored address, trimmed, never the typed spelling (#422)");
        challenge.NormalizedEmail.Should().Be("ALICE@EXAMPLE.TEST");
        challenge.UserId.Should().Be("usr_1");
        challenge.UserEmailId.Should().Be(account.Id);
    }

    [TestMethod]
    public void A_challenge_without_an_account_goes_to_the_typed_address()
    {
        var challenge = SqlOSEmailOtpChallenge.Issue(Request("New.User@Example.Test"), accountEmail: null, Settings, Now).Challenge;

        challenge.Email.Should().Be("New.User@Example.Test");
        challenge.NormalizedEmail.Should().Be("NEW.USER@EXAMPLE.TEST");
        challenge.UserId.Should().BeNull();
        challenge.UserEmailId.Should().BeNull();
        challenge.VerificationPurpose.Should().Be("signup");
    }

    [TestMethod]
    public void Issuing_stores_only_hashes_and_the_7x_shape()
    {
        var issued = SqlOSEmailOtpChallenge.Issue(Request("alice@example.test", ip: "203.0.113.10", userAgent: "UA"), AccountEmail("usr_1", "alice@example.test"), Settings, Now);

        var challenge = issued.Challenge;
        challenge.Id.Should().MatchRegex("^otp_[0-9a-f]{24}$");
        issued.Code.Should().MatchRegex("^[0-9]{6}$");
        issued.ChallengeToken.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        challenge.ChallengeTokenHash.Should().Be(HashedSecret.Sha256(issued.ChallengeToken).Hash);
        challenge.CodeHash.Should().Be(HashedSecret.Sha256($"{issued.ChallengeToken}:{issued.Code}").Hash, "the 7.x code hash binds the code to its challenge token");
        challenge.AuthorizationRequestId.Should().Be("req_1");
        challenge.ClientApplicationId.Should().Be("cli_1");
        challenge.RequestedOrganizationId.Should().BeNull();
        challenge.AttemptCount.Should().Be(0);
        challenge.MaxAttempts.Should().Be(5);
        challenge.CreatedAt.Should().Be(Now);
        challenge.LastSentAt.Should().Be(Now);
        challenge.ExpiresAt.Should().Be(Now.AddMinutes(10));
        challenge.IpAddress.Should().Be("203.0.113.10");
        challenge.UserAgent.Should().Be("UA");
        challenge.ConsumedAt.Should().BeNull();
        challenge.InvalidatedAt.Should().BeNull();
        Events(challenge).Should().Equal(new EmailOtpChallengeIssued(challenge.Id));
        issued.ToString().Should().NotContain(issued.Code).And.NotContain(issued.ChallengeToken);
    }

    [TestMethod]
    public void A_challenge_is_open_until_it_is_spent_invalidated_expired_or_out_of_attempts()
    {
        Issue().IsOpen(Now).Should().BeTrue();
        Issue().IsOpen(Now.AddMinutes(10).Tick(-1)).Should().BeTrue();
        Issue().IsOpen(Now.AddMinutes(10)).Should().BeFalse("a challenge is expired from its expiry instant on");

        var superseded = Issue();
        superseded.Supersede(Now);
        superseded.IsOpen(Now).Should().BeFalse();

        var completed = Issue();
        completed.Complete(Now);
        completed.IsOpen(Now).Should().BeFalse();

        var spent = Issue();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            spent.SpendAttemptInMemory();
        }

        spent.HasAttemptLeft.Should().BeFalse();
        spent.IsOpen(Now).Should().BeFalse();

        var noBudget = SqlOSEmailOtpChallenge.Issue(Request("alice@example.test"), null, Settings with { MaxAttempts = 0 }, Now).Challenge;
        noBudget.HasAttemptLeft.Should().BeFalse("a host that configured no attempts gets no attempts, as in 7.x");
        noBudget.IsOpen(Now).Should().BeFalse();
    }

    [TestMethod]
    public void A_challenge_bound_to_an_account_answers_only_while_the_address_is_still_that_accounts()
    {
        var account = AccountEmail("usr_1", "alice@example.test");
        var bound = SqlOSEmailOtpChallenge.Issue(Request("alice@example.test"), account, Settings, Now).Challenge;
        var unbound = SqlOSEmailOtpChallenge.Issue(Request("new@example.test"), null, Settings, Now).Challenge;

        unbound.IsStillAddressedToItsAccount().Should().BeTrue();
        bound.IsStillAddressedToItsAccount().Should().BeFalse("without the account's address loaded it fails closed");

        Load(bound, account);
        bound.IsStillAddressedToItsAccount().Should().BeTrue();

        Load(bound, AccountEmail("usr_2", "alice@example.test"));
        bound.IsStillAddressedToItsAccount().Should().BeFalse("the address moved to another account");

        Load(bound, AccountEmail("usr_1", "alice@another.test"));
        bound.IsStillAddressedToItsAccount().Should().BeFalse("the account's address changed");
    }

    [TestMethod]
    public void A_challenge_answers_only_its_authorization_request()
    {
        var withRequest = Issue();
        var withoutRequest = SqlOSEmailOtpChallenge.Issue(Request("alice@example.test", context: new EmailOtpChallengeContext(null, "cli_1", null)), null, Settings, Now).Challenge;

        withRequest.AnswersAuthorizationRequest("req_1").Should().BeTrue();
        withRequest.AnswersAuthorizationRequest("REQ_1").Should().BeFalse();
        withRequest.AnswersAuthorizationRequest(null).Should().BeFalse();
        withRequest.AnswersAuthorizationRequest(" ").Should().BeFalse();
        withoutRequest.AnswersAuthorizationRequest(null).Should().BeTrue();
        withoutRequest.AnswersAuthorizationRequest("req_1").Should().BeFalse();
    }

    [TestMethod]
    public void The_sign_in_context_and_the_resend_cooldown_compare_exactly()
    {
        var challenge = SqlOSEmailOtpChallenge.Issue(Request("alice@example.test", context: new EmailOtpChallengeContext(null, "cli_1", "workspace-2")), null, Settings, Now).Challenge;

        challenge.WasStartedIn(new EmailOtpChallengeContext(null, "cli_1", "workspace-2")).Should().BeTrue();
        challenge.WasStartedIn(new EmailOtpChallengeContext(null, "cli_1", "WORKSPACE-2")).Should().BeFalse("contexts compare ordinally, as 7.x did in memory");
        challenge.WasStartedIn(new EmailOtpChallengeContext("req_1", "cli_1", "workspace-2")).Should().BeFalse();
        challenge.WasSentWithin(TimeSpan.FromSeconds(30), Now.AddSeconds(29)).Should().BeTrue();
        challenge.WasSentWithin(TimeSpan.FromSeconds(30), Now.AddSeconds(30)).Should().BeFalse();
    }

    [TestMethod]
    public void Superseding_and_withdrawing_close_only_an_open_challenge()
    {
        var superseded = Issue();
        Drain(superseded);
        superseded.Supersede(Now.AddSeconds(1));
        superseded.Supersede(Now.AddSeconds(2));
        superseded.InvalidatedAt.Should().Be(Now.AddSeconds(1));
        superseded.InvalidatedReason.Should().Be("superseded");
        Events(superseded).Should().Equal(new EmailOtpChallengeSuperseded(superseded.Id));

        var withdrawn = Issue();
        Drain(withdrawn);
        withdrawn.Withdraw("password_reset", Now);
        withdrawn.InvalidatedReason.Should().Be("password_reset");
        Events(withdrawn).Should().Equal(new EmailOtpChallengeWithdrawn(withdrawn.Id, "password_reset"));

        var completed = Issue();
        completed.Complete(Now);
        Drain(completed);
        completed.Withdraw("password_reset", Now);
        completed.InvalidatedAt.Should().BeNull("a spent challenge keeps the state its sign-in staged");
        Events(completed).Should().BeEmpty();
    }

    [TestMethod]
    public void A_start_records_the_typed_address_masked_and_whether_the_code_went_out()
    {
        var request = Request("ALICE@Example.TEST", ip: "203.0.113.10");
        var challenge = SqlOSEmailOtpChallenge.Issue(request, AccountEmail("usr_1", "alice@example.test"), Settings, Now).Challenge;
        Drain(challenge);

        challenge.RecordStart(request, codeSent: true);

        Events(challenge).Should().Equal(new EmailOtpChallengeStarted(challenge.Id, "login", "AL***@Example.TEST", "203.0.113.10", "cli_1", "req_1", null, true));
    }

    [TestMethod]
    public void A_failed_delivery_invalidates_the_challenge_and_is_recorded()
    {
        var request = Request("alice@example.test", purpose: "signup", ip: "203.0.113.10");
        var challenge = SqlOSEmailOtpChallenge.Issue(request, null, Settings, Now).Challenge;
        Drain(challenge);

        challenge.FailDelivery(request, Now.AddSeconds(1));

        challenge.InvalidatedAt.Should().Be(Now.AddSeconds(1));
        challenge.InvalidatedReason.Should().Be("delivery_failed");
        Events(challenge).Should().Equal(new EmailOtpDeliveryFailed(challenge.Id, "signup", "al***@example.test", "203.0.113.10", "cli_1", null));
    }

    [TestMethod]
    public void Only_a_reserved_attempt_registers_a_code()
    {
        var issued = IssueWithSecrets();
        var other = IssueWithSecrets();

        FluentActions.Invoking(() => issued.Challenge.RegisterAttempt(new EmailOtpAttemptReservation(other.Challenge.Id), issued.ChallengeToken, issued.Code))
            .Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void The_right_code_is_accepted_and_audited_against_the_stored_recipient()
    {
        var account = AccountEmail("usr_1", "alice@example.test");
        var issued = SqlOSEmailOtpChallenge.Issue(Request("ALICE@example.test", ip: "203.0.113.10"), account, Settings, Now);
        var challenge = issued.Challenge;
        Drain(challenge);

        var outcome = challenge.RegisterAttempt(new EmailOtpAttemptReservation(challenge.Id), issued.ChallengeToken, issued.Code);

        outcome.Should().Be(EmailOtpAttemptOutcome.Accepted);
        Events(challenge).Should().Equal(new EmailOtpCodeAccepted(challenge.Id, "login", "al***@example.test", "203.0.113.10", "usr_1", "cli_1", "req_1"));
        challenge.ConsumedAt.Should().BeNull("accepting a code does not spend the challenge; completing it does");
    }

    [TestMethod]
    public void A_wrong_code_changes_nothing_until_the_rejection_is_recorded()
    {
        var issued = IssueWithSecrets();
        var challenge = issued.Challenge;
        Drain(challenge);
        var wrong = issued.Code == "000000" ? "000001" : "000000";

        challenge.RegisterAttempt(new EmailOtpAttemptReservation(challenge.Id), issued.ChallengeToken, wrong).Should().Be(EmailOtpAttemptOutcome.Rejected);
        challenge.RegisterAttempt(new EmailOtpAttemptReservation(challenge.Id), "another-challenge-token", issued.Code)
            .Should().Be(EmailOtpAttemptOutcome.Rejected, "a code is only valid with the challenge token it was issued with");
        Events(challenge).Should().BeEmpty();

        challenge.RejectCode(attemptsExhausted: false);
        challenge.RejectCode(attemptsExhausted: true);

        Events(challenge).Should().Equal(
            new EmailOtpCodeRejected(challenge.Id, "signup", "al***@example.test", null, "cli_1", "req_1", "wrong_code"),
            new EmailOtpCodeRejected(challenge.Id, "signup", "al***@example.test", null, "cli_1", "req_1", "max_attempts"));
    }

    [TestMethod]
    public void Completing_spends_the_challenge_once_and_proves_its_recipients_mailbox()
    {
        var challenge = SqlOSEmailOtpChallenge.Issue(Request("ALICE@example.test"), AccountEmail("usr_1", "alice@example.test"), Settings, Now).Challenge;
        Drain(challenge);

        var proof = challenge.Complete(Now.AddMinutes(1));

        challenge.ConsumedAt.Should().Be(Now.AddMinutes(1));
        proof.Method.Should().Be(OwnershipProofMethod.EmailOtp);
        proof.MethodName.Should().Be("email_otp");
        proof.Covers("Alice@Example.Test").Should().BeTrue();
        proof.Covers("bob@example.test").Should().BeFalse();
        Events(challenge).Should().Equal(new EmailOtpChallengeConsumed(challenge.Id));
        FluentActions.Invoking(() => challenge.Complete(Now.AddMinutes(2)))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.AlreadyConsumed);
    }

    [TestMethod]
    public void The_tracked_attempt_budget_spends_and_exhausts_once()
    {
        var challenge = SqlOSEmailOtpChallenge.Issue(Request("alice@example.test"), null, Settings with { MaxAttempts = 2 }, Now).Challenge;

        challenge.SpendAttemptInMemory();
        challenge.ExhaustInMemory(Now).Should().BeFalse("an attempt is left");
        challenge.SpendAttemptInMemory();
        challenge.ExhaustInMemory(Now).Should().BeTrue();
        challenge.ExhaustInMemory(Now).Should().BeFalse("only one call invalidates it");

        challenge.AttemptCount.Should().Be(2);
        challenge.InvalidatedReason.Should().Be("max_attempts");
        FluentActions.Invoking(challenge.SpendAttemptInMemory)
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.AttemptsExhausted);
    }

    [TestMethod]
    public void The_atomic_attempt_rules_match_the_in_memory_rules()
    {
        var open = Issue();
        var canSpend = SqlOSEmailOtpChallenge.AtomicAttempts.CanSpend(open.Id, Now).Compile();
        var isSpentOut = SqlOSEmailOtpChallenge.AtomicAttempts.IsSpentOut(open.Id).Compile();

        canSpend(open).Should().BeTrue();
        isSpentOut(open).Should().BeFalse();
        SqlOSEmailOtpChallenge.AtomicAttempts.CanSpend(open.Id, open.ExpiresAt).Compile()(open).Should().Be(open.IsOpen(open.ExpiresAt));
        SqlOSEmailOtpChallenge.AtomicAttempts.CanSpend("otp_other", Now).Compile()(open).Should().BeFalse();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            open.SpendAttemptInMemory();
        }

        canSpend(open).Should().Be(open.IsOpen(Now)).And.BeFalse();
        isSpentOut(open).Should().BeTrue();
        open.ExhaustInMemory(Now);
        isSpentOut(open).Should().BeFalse("an exhausted challenge is invalidated once");
    }

    private static EmailOtpChallengeRequest Request(
        string typed,
        string purpose = "login",
        EmailOtpChallengeContext? context = null,
        string? ip = null,
        string? userAgent = null)
        => new(EmailAddress.Parse(typed), purpose, context ?? Context, ip, userAgent);

    private static SqlOSEmailOtpChallenge Issue() => IssueWithSecrets().Challenge;

    private static IssuedEmailOtpChallenge IssueWithSecrets()
        => SqlOSEmailOtpChallenge.Issue(Request("alice@example.test"), null, Settings, Now);

    // A stored account address exactly as written (a row may predate trimming).
    private static SqlOSUserEmail AccountEmail(string userId, string address)
        => TestRows.Create<SqlOSUserEmail>(new
        {
            Id = $"eml_{Guid.NewGuid():N}"[..28],
            UserId = userId,
            Email = address,
            NormalizedEmail = EmailAddress.Parse(address).Canonical
        });

    private static void Load(SqlOSEmailOtpChallenge challenge, SqlOSUserEmail accountEmail)
        => typeof(SqlOSEmailOtpChallenge).GetProperty(nameof(SqlOSEmailOtpChallenge.UserEmail))!.SetValue(challenge, accountEmail);

    private static IReadOnlyList<ISqlOSDomainEvent> Events(SqlOSEmailOtpChallenge challenge)
        => ((ISqlOSAggregate)challenge).Events.Drain().Select(raised => raised.Event).ToList();

    private static void Drain(SqlOSEmailOtpChallenge challenge) => ((ISqlOSAggregate)challenge).Events.Drain();
}
