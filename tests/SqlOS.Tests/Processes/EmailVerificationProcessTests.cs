using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;

namespace SqlOS.Tests.Processes;

/// <summary>
/// The email-verification processes (<see cref="RequestEmailVerification"/>,
/// <see cref="IssueEmailVerificationToken"/> and <see cref="VerifyEmail"/>): the public API's routes
/// and the hosted verification page run them.
/// </summary>
[TestClass]
public sealed class EmailVerificationProcessTests
{
    [TestMethod]
    public async Task A_request_is_audited_and_answered_alike_whatever_the_address_is()
    {
        var harness = await IdentityProcessHarness.CreateAsync();

        var unknown = await harness.Processes.RequestEmailVerification().ExecuteAsync(
            new RequestEmailVerificationCommand("nobody@example.com", IdentityProcessHarness.Request),
            CancellationToken.None);

        unknown.Should().BeOfType<EmailVerificationRequestOutcome.Answered>()
            .Which.Result.Message.Should().Be(RequestEmailVerification.GenericMessage);
        harness.Emails.Messages.Should().BeEmpty();
        var requested = (await harness.AuditAsync("user.email-verification-requested")).Should().ContainSingle().Subject;
        requested.UserId.Should().BeNull();
        requested.IpAddress.Should().Be(IdentityProcessHarness.IpAddress);
        requested.MetadataJson.Should().Be("{\"maskedEmail\":\"no***@example.com\",\"eligible\":false}");

        var blank = await harness.Processes.RequestEmailVerification().ExecuteAsync(
            new RequestEmailVerificationCommand(" ", IdentityProcessHarness.Request),
            CancellationToken.None);
        blank.Should().BeOfType<EmailVerificationRequestOutcome.Refused>()
            .Which.Refusal.Message.Should().Be("Email address is required.");
    }

    [TestMethod]
    public async Task An_unverified_address_gets_one_link_a_minute_and_the_link_verifies_it_once()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var user = await harness.CreateUserAsync();
        var email = user.DefaultEmail!;

        for (var request = 0; request < 2; request++)
        {
            (await harness.Processes.RequestEmailVerification().ExecuteAsync(
                    new RequestEmailVerificationCommand(email, IdentityProcessHarness.Request),
                    CancellationToken.None))
                .Should().BeOfType<EmailVerificationRequestOutcome.Answered>();
        }

        harness.Emails.Messages.Should().ContainSingle(message => message.To == email, "a second request within a minute sends nothing");
        (await harness.AuditAsync("user.email-verification-requested")).Should().HaveCount(2);
        (await harness.AuditAsync("user.email-verification-token-created")).Should().ContainSingle()
            .Which.UserId.Should().Be(user.Id);
        var sent = (await harness.AuditAsync("user.email-verification-sent")).Should().ContainSingle().Subject;
        sent.MetadataJson.Should().StartWith("{\"maskedEmail\":");

        var link = harness.LinkTokenSentTo(email);
        (await harness.Processes.VerifyEmail().ExecuteAsync(new VerifyEmailCommand(link), CancellationToken.None))
            .Should().BeOfType<EmailVerificationOutcome.Verified>().Which.User.Id.Should().Be(user.Id);
        (await harness.Processes.VerifyEmail().ExecuteAsync(new VerifyEmailCommand(link), CancellationToken.None))
            .Should().BeOfType<EmailVerificationOutcome.Refused>()
            .Which.Refusal.Message.Should().Be("Email verification token is invalid or expired.");

        harness.Context.ChangeTracker.Clear();
        (await harness.Context.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == user.Id)).IsVerified.Should().BeTrue();
        (await harness.AuditAsync("user.email-verified")).Should().ContainSingle();
        (await harness.AuditAsync("user.email.claimed")).Should().BeEmpty("the confirmation link verifies without claiming");

        (await harness.Processes.RequestEmailVerification().ExecuteAsync(
                new RequestEmailVerificationCommand(email, IdentityProcessHarness.Request),
                CancellationToken.None))
            .Should().BeOfType<EmailVerificationRequestOutcome.Answered>();
        harness.Emails.Messages.Should().ContainSingle(message => message.To == email, "a verified address is sent nothing");
    }

    [TestMethod]
    public async Task A_link_that_cannot_be_sent_is_withdrawn_and_the_failure_audited()
    {
        var harness = await IdentityProcessHarness.CreateAsync(options => options.PublicOrigin = null);
        var user = await harness.CreateUserAsync();
        harness.Options.Issuer = "not-an-absolute-uri";

        (await harness.Processes.RequestEmailVerification().ExecuteAsync(
                new RequestEmailVerificationCommand(user.DefaultEmail!, IdentityProcessHarness.Request),
                CancellationToken.None))
            .Should().BeOfType<EmailVerificationRequestOutcome.Answered>("a failed send still answers generically");

        (await harness.Context.Set<SqlOSTemporaryToken>().AsNoTracking().SingleAsync(x => x.Purpose == "email_verification"))
            .ConsumedAt.Should().NotBeNull("the link that was never sent is withdrawn");
        var failed = (await harness.AuditAsync("user.email-verification-send-failed")).Should().ContainSingle().Subject;
        failed.UserId.Should().Be(user.Id);
        failed.MetadataJson.Should().Contain("AuthServer.Issuer must be an absolute URI");
        harness.Emails.Messages.Should().BeEmpty();
    }

    [TestMethod]
    public async Task A_token_for_host_delivery_needs_an_address_an_account_owns()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var user = await harness.CreateUserAsync();

        (await harness.Processes.IssueEmailVerificationToken().ExecuteAsync(new IssueEmailVerificationTokenCommand("nobody@example.com"), CancellationToken.None))
            .Should().BeOfType<EmailVerificationTokenOutcome.Refused>()
            .Which.Refusal.Message.Should().Be("Unknown email address.");

        var issued = await harness.Processes.IssueEmailVerificationToken().ExecuteAsync(
            new IssueEmailVerificationTokenCommand(user.DefaultEmail!), CancellationToken.None);
        var token = issued.Should().BeOfType<EmailVerificationTokenOutcome.Issued>().Subject.RawToken;
        (await harness.Processes.VerifyEmail().ExecuteAsync(new VerifyEmailCommand(token), CancellationToken.None))
            .Should().BeOfType<EmailVerificationOutcome.Verified>();
    }
}
