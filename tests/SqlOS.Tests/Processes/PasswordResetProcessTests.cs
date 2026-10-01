using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.AuthServer.Services;

namespace SqlOS.Tests.Processes;

/// <summary>
/// The password-reset processes (<see cref="RequestPasswordReset"/>, <see cref="SendPasswordResetEmail"/>,
/// <see cref="IssuePasswordResetToken"/> and <see cref="ResetPassword"/>): the one implementation the
/// hosted AuthPage, the headless API, the public API and the admin API run. The surfaces' answers are
/// locked by the behavior lock; these tests pin the processes' outcomes, refusals and audit.
/// </summary>
[TestClass]
public sealed class PasswordResetProcessTests
{
    [TestMethod]
    public async Task A_request_for_an_unknown_address_is_answered_like_any_other_and_sends_nothing()
    {
        var harness = await IdentityProcessHarness.CreateAsync();

        var outcome = await harness.Processes.RequestPasswordReset().ExecuteAsync(
            new RequestPasswordResetCommand("nobody@example.com", ClientId: null, IdentityProcessHarness.Request),
            CancellationToken.None);

        var answered = outcome.Should().BeOfType<PasswordResetRequestOutcome.Answered>().Subject.Result;
        answered.Message.Should().Be(RequestPasswordReset.GenericMessage);
        answered.MaskedEmail.Should().Be("no***@example.com");
        harness.Emails.Messages.Should().BeEmpty();
        var requested = (await harness.AuditAsync("password_reset.requested")).Should().ContainSingle().Subject;
        requested.UserId.Should().BeNull();
        requested.IpAddress.Should().Be(IdentityProcessHarness.IpAddress);
        requested.MetadataJson.Should().Contain("\"eligible\":false");
        (await harness.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "password_reset_request"))
            .Should().Be(1, "every admitted request leaves its marker");
        (await harness.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "password_reset")).Should().Be(0);
    }

    [TestMethod]
    public async Task A_request_without_an_address_is_refused_before_anything_is_written()
    {
        var harness = await IdentityProcessHarness.CreateAsync();

        var outcome = await harness.Processes.RequestPasswordReset().ExecuteAsync(
            new RequestPasswordResetCommand("   ", ClientId: null, IdentityProcessHarness.Request),
            CancellationToken.None);

        outcome.Should().BeOfType<PasswordResetRequestOutcome.Refused>()
            .Which.Refusal.Message.Should().Be("Email address is required.");
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType.StartsWith("password_reset."))).Should().Be(0);
        (await harness.Context.Set<SqlOSTemporaryToken>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task A_newer_link_retires_the_older_one_and_only_it_resets_the_password()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var user = await harness.CreateUserAsync();
        var email = user.DefaultEmail!;

        await harness.Processes.RequestPasswordReset().ExecuteAsync(
            new RequestPasswordResetCommand(email, ClientId: null, IdentityProcessHarness.Request), CancellationToken.None);
        var firstLink = harness.LinkTokenSentTo(email);
        await harness.Processes.RequestPasswordReset().ExecuteAsync(
            new RequestPasswordResetCommand(email, ClientId: null, IdentityProcessHarness.Request), CancellationToken.None);
        var secondLink = harness.LinkTokenSentTo(email);

        secondLink.Should().NotBe(firstLink);
        (await harness.Processes.ResetPassword().ExecuteAsync(new ResetPasswordCommand(firstLink, "First-New-Password1!"), CancellationToken.None))
            .Should().BeOfType<PasswordResetOutcome.Refused>().Which.Refusal.Should().Be(ResetPassword.LinkInvalid);
        (await harness.Processes.ResetPassword().ExecuteAsync(new ResetPasswordCommand(secondLink, "Second-New-Password1!"), CancellationToken.None))
            .Should().BeOfType<PasswordResetOutcome.Reset>().Which.User.Id.Should().Be(user.Id);

        var sent = await harness.AuditAsync("password_reset.email_sent");
        sent.Should().HaveCount(2).And.OnlyContain(row => row.UserId == user.Id && row.ActorType == "user" && row.ActorId == user.Id);
    }

    [TestMethod]
    public async Task A_link_is_spent_once_and_a_replay_is_refused_and_audited()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var user = await harness.CreateUserAsync();
        var link = await IssueLinkAsync(harness, user.DefaultEmail!);

        (await harness.Processes.ResetPassword().ExecuteAsync(new ResetPasswordCommand(link, "Replay-New-Password1!"), CancellationToken.None))
            .Should().BeOfType<PasswordResetOutcome.Reset>();
        var replay = await harness.Processes.ResetPassword().ExecuteAsync(new ResetPasswordCommand(link, "Replay-Other-Password1!"), CancellationToken.None);

        replay.Should().BeOfType<PasswordResetOutcome.Refused>()
            .Which.Refusal.Message.Should().Be("Password reset token is invalid or expired.");
        var refused = (await harness.AuditAsync("password_reset.invalid_or_expired")).Should().ContainSingle().Subject;
        refused.UserId.Should().BeNull("a link that is gone names no account");
        refused.MetadataJson.Should().Be("{\"maskedEmail\":null,\"details\":{\"reason\":\"missing_or_consumed\"}}");
        var completed = (await harness.AuditAsync("password_reset.completed")).Should().ContainSingle().Subject;
        completed.ActorType.Should().Be("user");
        completed.UserId.Should().Be(user.Id);
        completed.MetadataJson.Should().Be("{\"maskedEmail\":null,\"details\":null}");
    }

    [TestMethod]
    public async Task A_refused_password_leaves_the_link_usable()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var user = await harness.CreateUserAsync();
        var link = await IssueLinkAsync(harness, user.DefaultEmail!);

        var blank = await harness.Processes.ResetPassword().ExecuteAsync(new ResetPasswordCommand(link, ""), CancellationToken.None);

        blank.Should().BeOfType<PasswordResetOutcome.Refused>().Which.Refusal.Message.Should().Be("Password is required.");
        (await harness.AuditAsync("password_reset.invalid_or_expired")).Should().BeEmpty("the policy refused before the link was looked at");
        (await harness.Processes.ResetPassword().ExecuteAsync(new ResetPasswordCommand(link, "A-Real-Password1!"), CancellationToken.None))
            .Should().BeOfType<PasswordResetOutcome.Reset>();
    }

    [TestMethod]
    public async Task A_link_of_a_deactivated_account_is_spent_refused_and_audited()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var user = await harness.CreateUserAsync();
        var link = await IssueLinkAsync(harness, user.DefaultEmail!);
        await harness.Admin.DeactivateUserAsync(user.Id);

        var outcome = await harness.Processes.ResetPassword().ExecuteAsync(new ResetPasswordCommand(link, "Inactive-Password1!"), CancellationToken.None);

        outcome.Should().BeOfType<PasswordResetOutcome.Refused>().Which.Refusal.Should().Be(ResetPassword.LinkInvalid);
        var refused = (await harness.AuditAsync("password_reset.invalid_or_expired")).Should().ContainSingle().Subject;
        refused.UserId.Should().Be(user.Id);
        refused.MetadataJson.Should().Contain("inactive_user");
        (await harness.Context.Set<SqlOSTemporaryToken>().AsNoTracking().SingleAsync(x => x.Purpose == "password_reset"))
            .ConsumedAt.Should().NotBeNull("the link is spent before its account is looked at, as in 7.2.1");
    }

    [TestMethod]
    public async Task Completing_a_link_proves_the_mailbox_claims_the_unverified_address_and_ends_every_session()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var user = await harness.CreateUserAsync();
        var signedIn = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, IdentityProcessHarness.Password, IdentityProcessHarness.ClientId, null),
            IdentityProcessHarness.HttpContext());
        signedIn.Tokens.Should().NotBeNull();
        var link = await IssueLinkAsync(harness, user.DefaultEmail!);

        (await harness.Processes.ResetPassword().ExecuteAsync(new ResetPasswordCommand(link, "Claimed-Password1!"), CancellationToken.None))
            .Should().BeOfType<PasswordResetOutcome.Reset>();

        harness.Context.ChangeTracker.Clear();
        (await harness.Context.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == user.Id)).IsVerified.Should().BeTrue();
        (await harness.Context.Set<SqlOSSession>().Where(x => x.UserId == user.Id).ToListAsync())
            .Should().OnlyContain(session => session.RevokedAt != null && session.RevocationReason == "password_reset");
        var claimed = (await harness.AuditAsync("user.email.claimed")).Should().ContainSingle().Subject;
        claimed.MetadataJson.Should().Contain("\"proof\":\"password_reset\"");
        (await harness.AuditAsync("password_reset.completed")).Single().OccurredAt
            .Should().BeOnOrAfter(claimed.OccurredAt, "the completion is recorded after the reset commits");
        var refreshed = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "Claimed-Password1!", IdentityProcessHarness.ClientId, null),
            IdentityProcessHarness.HttpContext());
        refreshed.Tokens.Should().NotBeNull("the reset kept the password it set");
    }

    [TestMethod]
    public async Task An_operator_send_refuses_what_7x_refused()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var withoutPassword = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("No Password", $"nopw-{Guid.NewGuid():N}@example.com", null));

        async Task<string> RefusalAsync(string? userId)
        {
            var outcome = await harness.Processes.SendPasswordResetEmail().ExecuteAsync(
                new SendPasswordResetEmailCommand(new PasswordResetRecipient.User(userId), null, null, IdentityProcessHarness.Request),
                CancellationToken.None);
            return outcome.Should().BeOfType<PasswordResetEmailOutcome.Refused>().Subject.Refusal.Message;
        }

        (await RefusalAsync(" ")).Should().Be("User id is required.");
        (await RefusalAsync("usr_missing")).Should().Be("User not found.");
        (await RefusalAsync(withoutPassword.Id)).Should().Be("Password reset is unavailable for this account.");
        harness.Emails.Messages.Should().BeEmpty();
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType.StartsWith("password_reset."))).Should().Be(0);
    }

    [TestMethod]
    public async Task An_operator_send_emails_the_stored_address_and_is_audited_after_the_send()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var user = await harness.CreateUserAsync();

        var outcome = await harness.Processes.SendPasswordResetEmail().ExecuteAsync(
            new SendPasswordResetEmailCommand(new PasswordResetRecipient.User($"  {user.Id}  "), null, null, IdentityProcessHarness.Request),
            CancellationToken.None);

        var result = outcome.Should().BeOfType<PasswordResetEmailOutcome.Sent>().Subject.Result;
        result.Email.Should().Be(user.DefaultEmail);
        result.Message.Should().Be($"Password reset email queued for {result.MaskedEmail}.");
        harness.Emails.Messages.Should().ContainSingle(message => message.To == user.DefaultEmail);
        var sent = (await harness.AuditAsync("password_reset.email_sent")).Should().ContainSingle().Subject;
        var byOperator = (await harness.AuditAsync("password_reset.admin_email_sent")).Should().ContainSingle().Subject;
        byOperator.ActorType.Should().Be("admin");
        byOperator.ActorId.Should().BeNull();
        byOperator.UserId.Should().Be(user.Id);
        byOperator.OccurredAt.Should().BeAfter(sent.OccurredAt);
        (await harness.Context.Set<SqlOSTemporaryToken>().AsNoTracking().SingleAsync(x => x.Purpose == "password_reset"))
            .ClientApplicationId.Should().BeNull("an operator's link is bound to no client");
    }

    [TestMethod]
    public async Task A_link_that_cannot_be_built_is_withdrawn_audited_and_rethrown()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var user = await harness.CreateUserAsync();

        var act = async () => await harness.Processes.SendPasswordResetEmail().ExecuteAsync(
            new SendPasswordResetEmailCommand(
                new PasswordResetRecipient.Address(user.DefaultEmail!),
                "http://attacker.example.test/reset?token={token}",
                ClientId: null,
                IdentityProcessHarness.Request),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The configured password reset URL must be an absolute HTTPS URL (or loopback HTTP URL) without user information.");
        (await harness.Context.Set<SqlOSTemporaryToken>().AsNoTracking().SingleAsync(x => x.Purpose == "password_reset"))
            .ConsumedAt.Should().NotBeNull("a link that was never delivered cannot be used");
        var failed = (await harness.AuditAsync("password_reset.email_send_failed")).Should().ContainSingle().Subject;
        failed.ActorType.Should().Be("system");
        failed.UserId.Should().Be(user.Id);
        failed.MetadataJson.Should().Contain("must be an absolute HTTPS URL");
        (await harness.AuditAsync("password_reset.email_sent")).Should().BeEmpty();
        harness.Emails.Messages.Should().BeEmpty();
    }

    [TestMethod]
    public async Task A_token_for_host_delivery_is_issued_only_for_a_resettable_account()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var user = await harness.CreateUserAsync();

        var unknown = await harness.Processes.IssuePasswordResetToken().ExecuteAsync(
            new IssuePasswordResetTokenCommand("unknown@example.com", ClientId: null), CancellationToken.None);
        unknown.Should().BeOfType<PasswordResetTokenOutcome.Refused>().Which.Refusal.Message.Should().Be("Unknown email address.");

        var issued = await harness.Processes.IssuePasswordResetToken().ExecuteAsync(
            new IssuePasswordResetTokenCommand(user.DefaultEmail!, IdentityProcessHarness.ClientId), CancellationToken.None);
        var token = issued.Should().BeOfType<PasswordResetTokenOutcome.Issued>().Subject.RawToken;
        harness.Emails.Messages.Should().BeEmpty("the host delivers the link itself");
        (await harness.Context.Set<SqlOSTemporaryToken>().AsNoTracking().SingleAsync(x => x.Purpose == "password_reset"))
            .ClientApplicationId.Should().NotBeNull("the link is bound to the active client the host named");
        (await harness.Processes.ResetPassword().ExecuteAsync(new ResetPasswordCommand(token, "Host-Delivered-Password1!"), CancellationToken.None))
            .Should().BeOfType<PasswordResetOutcome.Reset>();
    }

    private static async Task<string> IssueLinkAsync(IdentityProcessHarness harness, string email)
    {
        var outcome = await harness.Processes.IssuePasswordResetToken().ExecuteAsync(
            new IssuePasswordResetTokenCommand(email, ClientId: null), CancellationToken.None);
        return outcome.Should().BeOfType<PasswordResetTokenOutcome.Issued>().Subject.RawToken;
    }
}
