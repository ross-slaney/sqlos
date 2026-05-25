using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Configuration;
using SqlOS.Email.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSInvitationServiceTests
{
    [TestMethod]
    public async Task CreateEmailInvitationAsync_SendsInviteAndStoresHashedToken()
    {
        var harness = await InvitationHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Invite Org", null));

        var invite = await harness.Auth.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "new-member@example.com", "admin"),
            harness.Http);

        invite.Status.Should().Be("pending");
        invite.InviteUrl.Should().Contain("/sqlos/auth/invitations/accept?token=");
        harness.EmailSender.Messages.Should().ContainSingle();
        harness.EmailSender.Messages.Single().To.Should().Be("new-member@example.com");

        var stored = await harness.Context.Set<SqlOSInvitation>().SingleAsync();
        stored.TokenHash.Should().NotBeNullOrWhiteSpace();
        invite.InviteUrl.Should().NotContain(stored.TokenHash);
        stored.InvitedEmail.Should().Be("new-member@example.com");
        stored.NormalizedEmail.Should().Be(SqlOSAdminService.NormalizeEmail("new-member@example.com"));
    }

    [TestMethod]
    public async Task AcceptEmailInvitationAsync_VerifiesEmailCreatesMembershipAndConsumesInvite()
    {
        var harness = await InvitationHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Checklist Squad", null));
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Casey", "casey@example.com", "P@ssword123!"));
        var invite = await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "casey@example.com", "member"),
            harness.Http);

        var acceptance = await harness.Auth.AcceptEmailInvitationAsync(
            new SqlOSAcceptEmailInvitationRequest(GetToken(invite), user.Id),
            harness.Http);

        acceptance.OrganizationId.Should().Be(org.Id);
        acceptance.MembershipCreated.Should().BeTrue();
        acceptance.EmailVerified.Should().BeTrue();

        var email = await harness.Context.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == user.Id);
        email.IsVerified.Should().BeTrue();
        var membership = await harness.Context.Set<SqlOSMembership>().SingleAsync(x => x.UserId == user.Id && x.OrganizationId == org.Id);
        membership.Role.Should().Be("member");
        var stored = await harness.Context.Set<SqlOSInvitation>().SingleAsync();
        stored.AcceptedAt.Should().NotBeNull();

        var reuse = async () => await harness.Auth.AcceptEmailInvitationAsync(
            new SqlOSAcceptEmailInvitationRequest(GetToken(invite), user.Id),
            harness.Http);
        await reuse.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invitation is invalid or expired.");
    }

    [TestMethod]
    public async Task AcceptEmailInvitationAsync_RejectsEmailMismatch()
    {
        var harness = await InvitationHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Mismatch Org", null));
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Different", "different@example.com", "P@ssword123!"));
        var invite = await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "invited@example.com", "member"),
            harness.Http);

        var act = async () => await harness.Auth.AcceptEmailInvitationAsync(
            new SqlOSAcceptEmailInvitationRequest(GetToken(invite), user.Id),
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("This invitation was sent to another email address.");

        var stored = await harness.Context.Set<SqlOSInvitation>().SingleAsync();
        stored.AcceptedAt.Should().BeNull();
        (await harness.Context.Set<SqlOSMembership>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task AcceptEmailInvitationAsync_ExistingMembershipIsIdempotentAndDoesNotDowngrade()
    {
        var harness = await InvitationHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Existing Org", null));
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Pat", "pat@example.com", "P@ssword123!"));
        await harness.Admin.CreateMembershipAsync(org.Id, new SqlOSCreateMembershipRequest(user.Id, "owner"));
        var invite = await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "pat@example.com", "viewer"),
            harness.Http);

        var acceptance = await harness.Auth.AcceptEmailInvitationAsync(
            new SqlOSAcceptEmailInvitationRequest(GetToken(invite), user.Id),
            harness.Http);

        acceptance.MembershipCreated.Should().BeFalse();
        acceptance.MembershipReactivated.Should().BeFalse();
        acceptance.Role.Should().Be("owner");
        var membership = await harness.Context.Set<SqlOSMembership>().SingleAsync(x => x.UserId == user.Id && x.OrganizationId == org.Id);
        membership.Role.Should().Be("owner");
    }

    [TestMethod]
    public async Task AcceptEmailInvitationAsync_InactiveMembershipReactivatesWithInviteRole()
    {
        var harness = await InvitationHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Rejoin Org", null));
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Riley", "riley@example.com", "P@ssword123!"));
        await harness.Admin.CreateMembershipAsync(org.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        var membership = await harness.Context.Set<SqlOSMembership>().SingleAsync(x => x.UserId == user.Id && x.OrganizationId == org.Id);
        membership.IsActive = false;
        membership.Role = "viewer";
        await harness.Context.SaveChangesAsync();
        var invite = await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "riley@example.com", "admin"),
            harness.Http);

        var acceptance = await harness.Auth.AcceptEmailInvitationAsync(
            new SqlOSAcceptEmailInvitationRequest(GetToken(invite), user.Id),
            harness.Http);

        acceptance.MembershipCreated.Should().BeFalse();
        acceptance.MembershipReactivated.Should().BeTrue();
        acceptance.Role.Should().Be("admin");
        membership = await harness.Context.Set<SqlOSMembership>().SingleAsync(x => x.UserId == user.Id && x.OrganizationId == org.Id);
        membership.IsActive.Should().BeTrue();
        membership.Role.Should().Be("admin");
    }

    [TestMethod]
    public async Task ResendEmailInvitationAsync_InvalidatesPreviousToken()
    {
        var harness = await InvitationHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Resend Org", null));
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Sam", "sam@example.com", "P@ssword123!"));
        var invite = await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "sam@example.com", "member"),
            harness.Http);
        var originalToken = GetToken(invite);

        var resent = await harness.Auth.ResendEmailInvitationAsync(
            new SqlOSResendEmailInvitationRequest(invite.Id),
            harness.Http);

        GetToken(resent).Should().NotBe(originalToken);
        var oldAccept = async () => await harness.Auth.AcceptEmailInvitationAsync(
            new SqlOSAcceptEmailInvitationRequest(originalToken, user.Id),
            harness.Http);
        await oldAccept.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invitation is invalid or expired.");

        var acceptance = await harness.Auth.AcceptEmailInvitationAsync(
            new SqlOSAcceptEmailInvitationRequest(GetToken(resent), user.Id),
            harness.Http);
        acceptance.OrganizationId.Should().Be(org.Id);
    }

    [TestMethod]
    public async Task RevokeEmailInvitationAsync_PreventsAcceptance()
    {
        var harness = await InvitationHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Revoke Org", null));
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Taylor", "taylor@example.com", "P@ssword123!"));
        var invite = await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "taylor@example.com", "member"),
            harness.Http);

        var revoked = await harness.Auth.RevokeEmailInvitationAsync(
            new SqlOSRevokeEmailInvitationRequest(invite.Id, "mistyped_email"),
            harness.Http);

        revoked.Status.Should().Be("revoked");
        var act = async () => await harness.Auth.AcceptEmailInvitationAsync(
            new SqlOSAcceptEmailInvitationRequest(GetToken(invite), user.Id),
            harness.Http);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invitation is invalid or expired.");
    }

    [TestMethod]
    public async Task ResolveEmailInvitationAsync_RejectsExpiredInvite()
    {
        var harness = await InvitationHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Expired Org", null));
        var invite = await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "expired@example.com", "member", ExpiresAt: DateTime.UtcNow.AddMinutes(5)),
            harness.Http);
        var stored = await harness.Context.Set<SqlOSInvitation>().SingleAsync();
        stored.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await harness.Context.SaveChangesAsync();

        var act = async () => await harness.InvitationService.ResolveEmailInvitationAsync(GetToken(invite), harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invitation is invalid or expired.");
    }

    [TestMethod]
    public async Task CreateEmailInvitationAsync_RateLimitsByEmail()
    {
        var harness = await InvitationHarness.CreateAsync(options => options.Invitations.MaxInvitationsPerEmailPerHour = 1);
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Rate Limit Org", null));
        await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "limited@example.com", "member"),
            harness.Http);

        var act = async () => await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "limited@example.com", "member"),
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many invitation emails have been requested for this address. Try again later.");
        var audit = await harness.Context.Set<SqlOSAuditEvent>()
            .AnyAsync(x => x.EventType == "invitation.rate_limit_rejected");
        audit.Should().BeTrue();
    }

    [TestMethod]
    public async Task CreateEmailInvitationAsync_RateLimitsByIpOrganizationAndInviter()
    {
        var ipHarness = await InvitationHarness.CreateAsync(options => options.Invitations.MaxInvitationsPerIpPerHour = 1);
        var ipOrg = await ipHarness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("IP Org", null));
        await ipHarness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(ipOrg.Id, "ip-one@example.com", "member"),
            ipHarness.Http);
        var ipLimited = async () => await ipHarness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(ipOrg.Id, "ip-two@example.com", "member"),
            ipHarness.Http);
        await ipLimited.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many invitation emails have been requested. Try again later.");

        var orgHarness = await InvitationHarness.CreateAsync(options => options.Invitations.MaxInvitationsPerOrganizationPerHour = 1);
        var limitedOrg = await orgHarness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Org Limit", null));
        await orgHarness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(limitedOrg.Id, "org-one@example.com", "member"),
            orgHarness.Http);
        var orgLimited = async () => await orgHarness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(limitedOrg.Id, "org-two@example.com", "member"),
            orgHarness.Http);
        await orgLimited.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many invitation emails have been requested for this organization. Try again later.");

        var inviterHarness = await InvitationHarness.CreateAsync(options => options.Invitations.MaxInvitationsPerInviterPerHour = 1);
        var inviterOrg = await inviterHarness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Inviter Org", null));
        var inviter = await inviterHarness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Owner", "owner@example.com", "P@ssword123!"));
        await inviterHarness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(inviterOrg.Id, "inviter-one@example.com", "member", InvitedByUserId: inviter.Id),
            inviterHarness.Http);
        var inviterLimited = async () => await inviterHarness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(inviterOrg.Id, "inviter-two@example.com", "member", InvitedByUserId: inviter.Id),
            inviterHarness.Http);
        await inviterLimited.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many invitation emails have been requested by this inviter. Try again later.");
    }

    [TestMethod]
    public async Task CreateEmailInvitationAsync_UsesCustomInviteMessageBuilder()
    {
        var harness = await InvitationHarness.CreateAsync(options =>
        {
            options.Invitations.ApplicationName = "ChecklistSquad";
            options.Invitations.BuildMessage = context => new SqlOS.AuthServer.Interfaces.SqlOSAuthEmailMessage(
                context.Email,
                $"{context.ApplicationName}: join {context.OrganizationName}",
                "<p>custom invite</p>",
                $"Invite link: {context.AcceptUrl}");
        });
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Custom Org", null));

        await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "custom@example.com", "member"),
            harness.Http);

        harness.EmailSender.Messages.Single().Subject.Should().Be("ChecklistSquad: join Custom Org");
        harness.EmailSender.Messages.Single().TextBody.Should().Contain("/invitations/accept?token=");
    }

    [TestMethod]
    public async Task CreateEmailInvitationAsync_UsesSeededEmailBrandingForDefaultTemplate()
    {
        var harness = await InvitationHarness.CreateAsync(options =>
        {
            options.SeedAuthEmails(email =>
            {
                email.ApplicationName = "Acme Portal";
                email.LogoBase64 = "data:image/png;base64,invite-logo";
                email.PrimaryColor = "#7c3aed";
                email.AccentColor = "#18181b";
                email.BackgroundColor = "#faf5ff";
            });
        });
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Branded Org", null));

        await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "branded-invite@example.com", "member"),
            harness.Http);

        var message = harness.EmailSender.Messages.Single();
        message.Subject.Should().Be("You're invited to Branded Org");
        message.HtmlBody.Should().Contain("data:image/png;base64,invite-logo");
        message.HtmlBody.Should().Contain("#7c3aed");
        message.HtmlBody.Should().Contain("#18181b");
        message.HtmlBody.Should().Contain("#faf5ff");
        message.TextBody.Should().Contain("You're invited to Branded Org as member");
    }

    [TestMethod]
    public async Task AcceptEmailInvitationSignupAsync_CreatesVerifiedUserMembershipAndTokensWithoutSendingOtp()
    {
        var harness = await InvitationHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Invite Signup Org", null));
        var invite = await harness.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(org.Id, "new-invite-signup@example.com", "admin"),
            harness.Http);

        var result = await harness.Auth.AcceptEmailInvitationSignupAsync(
            new SqlOSAcceptEmailInvitationSignupRequest(
                GetToken(invite),
                "Invite Signup",
                "test-client"),
            harness.Http);

        result.RequiresOrganizationSelection.Should().BeFalse();
        result.Tokens.Should().NotBeNull();
        result.Tokens!.OrganizationId.Should().Be(org.Id);
        harness.EmailSender.Messages.Should().ContainSingle("only the invitation email should be sent");

        var email = await harness.Context.Set<SqlOSUserEmail>()
            .SingleAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail("new-invite-signup@example.com"));
        email.IsVerified.Should().BeTrue();
        var membership = await harness.Context.Set<SqlOSMembership>()
            .SingleAsync(x => x.UserId == email.UserId && x.OrganizationId == org.Id);
        membership.Role.Should().Be("admin");
        var stored = await harness.Context.Set<SqlOSInvitation>().SingleAsync();
        stored.AcceptedAt.Should().NotBeNull();

        var session = await harness.Context.Set<SqlOSSession>().SingleAsync();
        session.AuthenticationMethod.Should().Be("invitation");
        session.UserId.Should().Be(email.UserId);
    }

    private static string GetToken(SqlOSEmailInvitationResult result)
    {
        result.InviteUrl.Should().NotBeNullOrWhiteSpace();
        var marker = "token=";
        var index = result.InviteUrl!.IndexOf(marker, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0);
        var token = result.InviteUrl[(index + marker.Length)..];
        var ampersand = token.IndexOf('&');
        if (ampersand >= 0)
        {
            token = token[..ampersand];
        }

        return Uri.UnescapeDataString(token);
    }

    private sealed class InvitationHarness : IDisposable
    {
        public required TestSqlOSInMemoryDbContext Context { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSAuthService Auth { get; init; }
        public required SqlOSInvitationService InvitationService { get; init; }
        public required TestAuthEmailSender EmailSender { get; init; }
        public required DefaultHttpContext Http { get; init; }

        public static async Task<InvitationHarness> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);

            var authOptions = new SqlOSAuthServerOptions
            {
                PublicOrigin = "https://auth.example.test"
            };
            authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            configure?.Invoke(authOptions);
            var options = Options.Create(authOptions);
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var crypto = new SqlOSCryptoService(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var transactionalEmailService = new SqlOSTransactionalEmailService(
                context,
                crypto,
                emailSender,
                new SqlOSEmailTemplateRenderer(),
                Options.Create(new SqlOSEmailOptions()));
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options, transactionalEmailService);
            var invitation = new SqlOSInvitationService(context, admin, crypto, emailSender, settings, options, transactionalEmailService);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp, invitation, transactionalEmailService: transactionalEmailService);
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.test");

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.EnsureDefaultAuthPageSettingsAsync();
            await settings.UpsertSeededAuthEmailSettingsAsync();
            await new SqlOSEmailAdminService(context, crypto, new SqlOSEmailTemplateRenderer()).EnsureBuiltInTemplatesAsync();

            return new InvitationHarness
            {
                Context = context,
                Admin = admin,
                Auth = auth,
                InvitationService = invitation,
                EmailSender = emailSender,
                Http = http
            };
        }

        public void Dispose()
            => Context.Dispose();
    }
}
