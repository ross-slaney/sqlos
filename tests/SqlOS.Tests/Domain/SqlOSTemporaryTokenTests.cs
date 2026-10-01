using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using static SqlOS.Tests.Domain.DomainTime;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class SqlOSTemporaryTokenTests
{
    private static readonly TemporaryTokenKind<PendingAuthPayload> Pending = SqlOSTemporaryTokenKinds.PendingAuth;
    private static readonly TemporaryTokenBinding Binding = new(UserId: "usr_1", ClientApplicationId: "cli_1");

    [TestMethod]
    public void Issuing_stores_only_the_hash_of_a_fresh_raw_token()
    {
        var issued = Issue();

        var token = issued.Token;
        issued.RawToken.Should().MatchRegex("^[A-Za-z0-9_-]{43}$", "32 random bytes in unpadded base64url, the 7.x format");
        token.TokenHash.Should().Be(HashedSecret.Sha256(issued.RawToken).Hash);
        token.TokenHash.Should().NotContain(issued.RawToken);
        token.Secret.Matches(issued.RawToken).Should().BeTrue();
        token.Id.Should().MatchRegex("^tmp_[0-9a-f]{24}$");
        token.Purpose.Should().Be("pending_auth");
        token.UserId.Should().Be("usr_1");
        token.ClientApplicationId.Should().Be("cli_1");
        token.OrganizationId.Should().BeNull();
        token.IssuerSessionFamilyId.Should().BeNull();
        token.PayloadJson.Should().Be("{\"ClientId\":\"client\",\"AuthenticationMethod\":\"password\"}");
        token.CreatedAt.Should().Be(Now);
        token.ExpiresAt.Should().Be(Now.AddMinutes(10));
        token.ConsumedAt.Should().BeNull();
        Issue().RawToken.Should().NotBe(issued.RawToken);
        issued.ToString().Should().NotContain(issued.RawToken);
    }

    [TestMethod]
    public void Issuing_raises_the_issue_event_and_the_kinds_own_event()
    {
        var plain = Issue();
        var verification = SqlOSTemporaryToken.Issue(
            SqlOSTemporaryTokenKinds.EmailVerification,
            new EmailVerificationPayload("eml_1"),
            new TemporaryTokenBinding(UserId: "usr_9"),
            TimeSpan.FromDays(1),
            Now);

        Events(plain.Token).Should().Equal(new TemporaryTokenIssued(plain.Token.Id, "pending_auth"));
        Events(verification.Token).Should().Equal(
            new TemporaryTokenIssued(verification.Token.Id, "email_verification"),
            new EmailVerificationTokenCreated(verification.Token.Id, "usr_9"));
    }

    [TestMethod]
    public void Issuing_refuses_a_binding_the_kind_does_not_define()
        => FluentActions.Invoking(() => SqlOSTemporaryToken.Issue(
                Pending,
                new PendingAuthPayload("client", "password"),
                new TemporaryTokenBinding(OrganizationId: "org_1"),
                TimeSpan.FromMinutes(10),
                Now))
            .Should().Throw<ArgumentException>();

    [TestMethod]
    public void A_token_is_usable_until_its_expiry_instant()
    {
        var token = Issue().Token;

        token.IsUsable(Now).Should().BeTrue();
        token.IsUsable(token.ExpiresAt.Tick(-1)).Should().BeTrue();
        token.IsUsable(token.ExpiresAt).Should().BeFalse("a token is expired from its expiry instant on");
    }

    [TestMethod]
    public void A_single_use_token_is_consumed_once()
    {
        var token = Issue().Token;
        Drain(token);

        token.Consume(Pending, Now.AddMinutes(1));

        token.ConsumedAt.Should().Be(Now.AddMinutes(1));
        token.IsUsable(Now.AddMinutes(1)).Should().BeFalse();
        Events(token).Should().Equal(new TemporaryTokenConsumed(token.Id, "pending_auth"));
        FluentActions.Invoking(() => token.Consume(Pending, Now.AddMinutes(2)))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.AlreadyConsumed);
        token.ConsumedAt.Should().Be(Now.AddMinutes(1));
    }

    [TestMethod]
    public void An_expired_token_cannot_be_consumed()
    {
        var token = Issue().Token;

        FluentActions.Invoking(() => token.Consume(Pending, token.ExpiresAt))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.Expired);
        token.ConsumedAt.Should().BeNull();
    }

    [TestMethod]
    public void A_token_is_used_only_through_its_own_kind()
    {
        var token = Issue().Token;

        FluentActions.Invoking(() => token.Consume(SqlOSTemporaryTokenKinds.MagicLink, Now))
            .Should().Throw<InvalidOperationException>().WithMessage("*'pending_auth', not 'auth.magic_link'*");
        FluentActions.Invoking(() => token.ReadPayload(SqlOSTemporaryTokenKinds.AuthPagePending))
            .Should().Throw<InvalidOperationException>();
        token.Is(Pending).Should().BeTrue();
        token.Is(SqlOSTemporaryTokenKinds.AuthPagePending).Should().BeFalse();
        token.ReadPayload(Pending).Should().Be(new PendingAuthPayload("client", "password"));
        token.ConsumedAt.Should().BeNull();
    }

    [TestMethod]
    public void A_session_token_is_never_spent_by_use_only_retired()
    {
        var session = SqlOSTemporaryToken.Issue(
            SqlOSTemporaryTokenKinds.IssuerSession,
            new IssuerSessionPayload("password", Now),
            new TemporaryTokenBinding(UserId: "usr_1", IssuerSessionFamilyId: "aps_1"),
            TimeSpan.FromHours(1),
            Now).Token;

        FluentActions.Invoking(() => session.Consume(SqlOSTemporaryTokenKinds.IssuerSession, Now))
            .Should().Throw<InvalidOperationException>().WithMessage("*retire*");
        session.Retire(Now);
        session.ConsumedAt.Should().Be(Now);
    }

    [TestMethod]
    public void Retiring_is_idempotent_and_keeps_the_first_time()
    {
        var token = Issue().Token;
        Drain(token);

        token.Retire(Now.AddMinutes(1));
        token.Retire(Now.AddMinutes(2));

        token.ConsumedAt.Should().Be(Now.AddMinutes(1));
        Events(token).Should().Equal(new TemporaryTokenRetired(token.Id, "pending_auth"));
        token.Retire(token.ExpiresAt.AddDays(1));
        token.ConsumedAt.Should().Be(Now.AddMinutes(1), "an expired token can be retired, and a retired one stays retired");
    }

    [TestMethod]
    public void A_payload_is_replaced_through_its_kind()
    {
        var challenge = SqlOSTemporaryToken.Issue(
            SqlOSTemporaryTokenKinds.MfaChallenge,
            new SqlOS.AuthServer.Contracts.SqlOSMfaChallengePayload("client", "app", "password"),
            new TemporaryTokenBinding(UserId: "usr_1", ClientApplicationId: "cli_1"),
            TimeSpan.FromMinutes(5),
            Now).Token;
        Drain(challenge);
        var payload = challenge.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)!;

        challenge.ReplacePayload(SqlOSTemporaryTokenKinds.MfaChallenge, payload with { FailedAttempts = 1 });

        challenge.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)!.FailedAttempts.Should().Be(1);
        Events(challenge).Should().Equal(new TemporaryTokenPayloadReplaced(challenge.Id, "mfa_challenge"));
    }

    [TestMethod]
    public void An_outcome_is_recorded_only_on_a_token_of_its_kind_and_identity()
    {
        var verification = SqlOSTemporaryToken.Issue(
            SqlOSTemporaryTokenKinds.EmailVerification,
            new EmailVerificationPayload("eml_1"),
            new TemporaryTokenBinding(UserId: "usr_1"),
            TimeSpan.FromDays(1),
            Now).Token;
        var other = Issue().Token;
        Drain(verification);

        FluentActions.Invoking(() => other.Record(new EmailVerificationTokenCreated(other.Id, "usr_1")))
            .Should().Throw<InvalidOperationException>("an email-verification outcome cannot be recorded on a pending-auth token");
        FluentActions.Invoking(() => verification.Record(new EmailVerificationTokenCreated("tmp_other", "usr_1")))
            .Should().Throw<ArgumentException>();

        verification.Record(new EmailVerificationTokenCreated(verification.Id, "usr_1"));
        Events(verification).Should().Equal(new EmailVerificationTokenCreated(verification.Id, "usr_1"));
    }

    [TestMethod]
    public void The_query_forms_match_the_in_memory_rules()
    {
        var token = Issue().Token;
        var usable = SqlOSTemporaryToken.UsableAt(Now).Compile();
        var expiredAtInstant = SqlOSTemporaryToken.UsableAt(token.ExpiresAt).Compile();
        var ofKind = SqlOSTemporaryToken.OfKind(Pending).Compile();
        var presented = SqlOSTemporaryToken.Presented("raw").Compile();

        usable(token).Should().Be(token.IsUsable(Now));
        expiredAtInstant(token).Should().Be(token.IsUsable(token.ExpiresAt));
        ofKind(token).Should().BeTrue();
        SqlOSTemporaryToken.OfKind(SqlOSTemporaryTokenKinds.MagicLink).Compile()(token).Should().BeFalse();
        presented(token).Should().BeFalse();
    }

    private static IssuedTemporaryToken Issue()
        => SqlOSTemporaryToken.Issue(Pending, new PendingAuthPayload("client", "password"), Binding, TimeSpan.FromMinutes(10), Now);

    private static IReadOnlyList<ISqlOSDomainEvent> Events(SqlOSTemporaryToken token)
        => ((ISqlOSAggregate)token).Events.Drain().Select(raised => raised.Event).ToList();

    private static void Drain(SqlOSTemporaryToken token) => ((ISqlOSAggregate)token).Events.Drain();
}
