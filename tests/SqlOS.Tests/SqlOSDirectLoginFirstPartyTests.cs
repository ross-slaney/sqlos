using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Configuration;
using SqlOS.Email.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// Direct login is first-party only (#419). These cover the in-process <see cref="SqlOSAuthService"/>
/// APIs that have no SqlOS HTTP route of their own (host code calls them with a client id); the public
/// routes are covered against real SQL by <c>DirectLoginFirstPartyIntegrationTests</c>.
/// </summary>
[TestClass]
public sealed class SqlOSDirectLoginFirstPartyTests
{
    private const string FirstPartyClientId = "first-party";
    private const string ThirdPartyClientId = "third-party";
    private const string Password = "P@ssword123!";

    [TestMethod]
    public async Task HostCodeClientAuthentication_IsFirstPartyOnly_AndAuditsOneRejection()
    {
        using var harness = await Harness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Host Code", "host-code@example.com", Password));

        var rejected = async () => await harness.Auth.CompleteClientAuthenticationAsync(
            user,
            harness.ThirdParty,
            organizationId: null,
            "custom",
            Harness.CreateHttpContext("/app/custom-sign-in"));

        var error = (await rejected.Should().ThrowAsync<SqlOSPublicAuthException>()).Which;
        error.Error.Should().Be("invalid_client");
        error.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        SqlOSPublicAuthErrorMapper.Map(error, SqlOSPublicAuthErrorSurface.HostedPage).HasDiagnosticDetail
            .Should().BeFalse("the rejection is already audited once; mapping it must not add a second event");
        (await harness.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        var rejection = (await harness.RejectionsAsync()).Should().ContainSingle().Subject;
        rejection.UserId.Should().Be(user.Id);
        rejection.ActorId.Should().Be(harness.ThirdParty.Id);
        ReadMetadata(rejection, "client_id").Should().Be(ThirdPartyClientId);
        ReadMetadata(rejection, "route").Should().Be("/app/custom-sign-in");
        rejection.IpAddress.Should().Be("203.0.113.19");

        var accepted = await harness.Auth.CompleteClientAuthenticationAsync(
            user,
            harness.FirstParty,
            organizationId: null,
            "custom",
            Harness.CreateHttpContext("/app/custom-sign-in"));
        accepted.Tokens.Should().NotBeNull();
        (await harness.RejectionsAsync()).Should().ContainSingle("a first-party client is never rejected");
    }

    [TestMethod]
    public async Task ExternalLogin_ThirdPartyClient_IsRejectedBeforeTheOrganizationPendingToken()
    {
        using var harness = await Harness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Two Orgs", "two-orgs@example.com", Password));
        for (var index = 0; index < 2; index++)
        {
            var organization = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"External Org {index}", null));
            await harness.Admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        }

        var rejected = async () => await harness.Auth.CompleteExternalLoginAsync(
            user,
            harness.ThirdParty,
            "google",
            Harness.CreateHttpContext("/api/social/complete"));

        (await rejected.Should().ThrowAsync<SqlOSPublicAuthException>()).Which.Error.Should().Be("invalid_client");
        (await harness.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "pending_auth")).Should().Be(0);
        (await harness.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await harness.RejectionsAsync()).Should().ContainSingle().Which.UserId.Should().Be(user.Id);

        var accepted = await harness.Auth.CompleteExternalLoginAsync(
            user,
            harness.FirstParty,
            "google",
            Harness.CreateHttpContext("/api/social/complete"));
        accepted.RequiresOrganizationSelection.Should().BeTrue();
    }

    [TestMethod]
    public async Task CodeOnlyStartApis_ThirdPartyClient_SendNoEmailOrSms()
    {
        using var harness = await Harness.CreateAsync();
        var phoneUser = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Phone Victim", "phone-victim@example.com", Password));
        await harness.PhoneOtp.AddVerifiedPhoneNumberAsync(phoneUser, "+12025550140");
        var http = Harness.CreateHttpContext("/app/start");

        var emailSignup = async () => await harness.Auth.RequestEmailOtpSignupAsync(
            new SqlOSEmailOtpSignupStartRequest("Attacker", "signup@example.com", ThirdPartyClientId, null, null, null),
            http);
        var phoneLogin = async () => await harness.Auth.RequestPhoneOtpAsync(
            new SqlOSPhoneOtpStartRequest("+12025550140", ThirdPartyClientId, null),
            http);
        // In-process callers may omit HttpContext; the refusal is still audited, without a route.
        var phoneSignup = async () => await harness.Auth.RequestPhoneOtpSignupAsync(
            new SqlOSPhoneOtpSignupStartRequest("Attacker", "+12025550141", ThirdPartyClientId, null, null, null));

        (await emailSignup.Should().ThrowAsync<SqlOSPublicAuthException>()).Which.Error.Should().Be("invalid_client");
        (await phoneLogin.Should().ThrowAsync<SqlOSPublicAuthException>()).Which.Error.Should().Be("invalid_client");
        (await phoneSignup.Should().ThrowAsync<SqlOSPublicAuthException>()).Which.Error.Should().Be("invalid_client");

        harness.EmailSender.Messages.Should().BeEmpty();
        harness.Channel.StartRequests.Should().BeEmpty("no SMS is sent under the host's branding for a third-party client");
        (await harness.Context.Set<SqlOSEmailOtpChallenge>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSTemporaryToken>().CountAsync()).Should().Be(0);
        var rejections = await harness.RejectionsAsync();
        rejections.Should().HaveCount(3);
        rejections.Select(item => ReadMetadata(item, "route")).Should().BeEquivalentTo(new string?[] { "/app/start", "/app/start", null });

        await harness.Auth.RequestPhoneOtpAsync(new SqlOSPhoneOtpStartRequest("+12025550140", FirstPartyClientId, null), http);
        harness.Channel.StartRequests.Should().ContainSingle("the first-party client still starts phone sign-in");
    }

    [TestMethod]
    public async Task InvitationSignup_ThirdPartyClient_CreatesNoAccount_AndLeavesTheInvitationPending()
    {
        using var harness = await Harness.CreateAsync();
        var organization = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Invite Org", null));
        var invite = await harness.Invitations.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(organization.Id, "invitee@example.com", "member"),
            Harness.CreateHttpContext("/admin/invite"));
        var invitationToken = ExtractToken(invite.InviteUrl);
        var usersBefore = await harness.Context.Set<SqlOSUser>().CountAsync();

        var rejected = async () => await harness.Auth.AcceptEmailInvitationSignupAsync(
            new SqlOSAcceptEmailInvitationSignupRequest(invitationToken, "Invitee", ThirdPartyClientId),
            Harness.CreateHttpContext("/app/invitations/signup"));

        (await rejected.Should().ThrowAsync<SqlOSPublicAuthException>()).Which.Error.Should().Be("invalid_client");
        (await harness.Context.Set<SqlOSUser>().CountAsync()).Should().Be(usersBefore, "no account is created for a rejected client");
        (await harness.Context.Set<SqlOSInvitation>().SingleAsync()).AcceptedAt.Should().BeNull();
        (await harness.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await harness.RejectionsAsync()).Should().ContainSingle();

        var accepted = await harness.Auth.AcceptEmailInvitationSignupAsync(
            new SqlOSAcceptEmailInvitationSignupRequest(invitationToken, "Invitee", FirstPartyClientId),
            Harness.CreateHttpContext("/app/invitations/signup"));
        accepted.Tokens.Should().NotBeNull("the invitation still completes through the host's first-party client");
    }

    [TestMethod]
    public async Task SignupVerification_WithStartsIssuedBeforeTheGate_CreatesNoAccountForThirdPartyClient()
    {
        using var harness = await Harness.CreateAsync();
        var http = Harness.CreateHttpContext("/app/signup/verify");

        // Signup tokens minted while the client could still use direct login (before this release).
        await harness.SetThirdPartyIsFirstPartyAsync(true);
        var emailStart = await harness.Auth.RequestEmailOtpSignupAsync(
            new SqlOSEmailOtpSignupStartRequest("Email Signup", "pre-gate@example.com", ThirdPartyClientId, null, null, null),
            http);
        var phoneStart = await harness.Auth.RequestPhoneOtpSignupAsync(
            new SqlOSPhoneOtpSignupStartRequest("Phone Signup", "+12025550150", ThirdPartyClientId, null, null, null),
            http);
        await harness.SetThirdPartyIsFirstPartyAsync(false);
        var usersBefore = await harness.Context.Set<SqlOSUser>().CountAsync();

        var emailVerify = async () => await harness.Auth.VerifyEmailOtpSignupAsync(
            new SqlOSEmailOtpSignupVerifyRequest(emailStart.SignupToken, emailStart.ChallengeToken, harness.LatestCode("pre-gate@example.com")),
            http);
        var phoneVerify = async () => await harness.Auth.VerifyPhoneOtpSignupAsync(
            new SqlOSPhoneOtpSignupVerifyRequest(phoneStart.SignupToken, phoneStart.ChallengeToken, harness.Channel.ApprovedCode),
            http);

        (await emailVerify.Should().ThrowAsync<SqlOSPublicAuthException>()).Which.Error.Should().Be("invalid_client");
        (await phoneVerify.Should().ThrowAsync<SqlOSPublicAuthException>()).Which.Error.Should().Be("invalid_client");
        (await harness.Context.Set<SqlOSUser>().CountAsync()).Should().Be(usersBefore, "the gate runs before the account is created");
        (await harness.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await harness.RejectionsAsync()).Should().HaveCount(2);
    }

    private static string? ReadMetadata(SqlOSAuditEvent auditEvent, string property)
    {
        using var metadata = JsonDocument.Parse(auditEvent.MetadataJson ?? "{}");
        return metadata.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
    }

    private static string ExtractToken(string? inviteUrl)
    {
        inviteUrl.Should().NotBeNullOrWhiteSpace();
        return Uri.UnescapeDataString(Regex.Match(inviteUrl!, @"[?&]token=([^&]+)").Groups[1].Value);
    }

    private sealed class Harness : IDisposable
    {
        public required TestSqlOSInMemoryDbContext Context { get; init; }
        public required SqlOSAuthService Auth { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSInvitationService Invitations { get; init; }
        public required SqlOSPhoneOtpService PhoneOtp { get; init; }
        public required TestAuthEmailSender EmailSender { get; init; }
        public required RecordingOtpChannel Channel { get; init; }
        public required SqlOSClientApplication FirstParty { get; init; }
        public required SqlOSClientApplication ThirdParty { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var authOptions = new SqlOSAuthServerOptions { PublicOrigin = "https://auth.example.test" };
            authOptions.SeedBrowserClient(FirstPartyClientId, "First Party", "https://first.example.test/callback");
            authOptions.SeedClient(client =>
            {
                client.ClientId = ThirdPartyClientId;
                client.Name = "Third Party";
                client.RedirectUris = ["https://third.example.test/callback"];
                client.IsFirstParty = false;
            });
            authOptions.SeedAuthPage(page =>
            {
                page.EnabledCredentialTypes = ["password", "email_otp", "phone_otp"];
                page.EnablePasswordSignup = true;
            });
            authOptions.ConfigurePhoneOtp(phone =>
            {
                phone.Enabled = true;
                phone.TwilioAccountSid = "AC00000000000000000000000000000000";
                phone.TwilioAuthToken = "test-token";
                phone.TwilioVerifyServiceSid = "VA00000000000000000000000000000000";
                phone.ResendCooldown = TimeSpan.Zero;
            });

            var options = Options.Create(authOptions);
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var crypto = TestCryptoService.Create(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var transactionalEmail = new SqlOSTransactionalEmailService(
                context,
                crypto,
                emailSender,
                new SqlOSEmailTemplateRenderer(),
                Options.Create(new SqlOSEmailOptions()));
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options, transactionalEmail);
            var invitations = new SqlOSInvitationService(context, admin, crypto, emailSender, settings, options, transactionalEmail);
            var channel = new RecordingOtpChannel();
            var phoneOtp = new SqlOSPhoneOtpService(context, admin, crypto, settings, channel, options);
            var auth = new SqlOSAuthService(
                context,
                options,
                admin,
                crypto,
                settings,
                emailOtp,
                invitationService: invitations,
                transactionalEmailService: transactionalEmail,
                phoneOtpService: phoneOtp);

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.UpsertSeededAuthPageSettingsAsync();
            await settings.UpsertSeededAuthEmailSettingsAsync();
            await new SqlOSEmailAdminService(context, crypto, new SqlOSEmailTemplateRenderer()).EnsureBuiltInTemplatesAsync();

            return new Harness
            {
                Context = context,
                Auth = auth,
                Admin = admin,
                Invitations = invitations,
                PhoneOtp = phoneOtp,
                EmailSender = emailSender,
                Channel = channel,
                FirstParty = await context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == FirstPartyClientId),
                ThirdParty = await context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == ThirdPartyClientId)
            };
        }

        public static DefaultHttpContext CreateHttpContext(string path)
        {
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.19");
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("app.example.test");
            http.Request.Path = path;
            return http;
        }

        public async Task SetThirdPartyIsFirstPartyAsync(bool isFirstParty)
        {
            ThirdParty.IsFirstParty = isFirstParty;
            await Context.SaveChangesAsync();
        }

        public async Task<List<SqlOSAuditEvent>> RejectionsAsync()
            => await Context.Set<SqlOSAuditEvent>()
                .Where(x => x.EventType == SqlOSDirectLoginPolicy.RejectedAuditEvent)
                .ToListAsync();

        public string LatestCode(string email)
        {
            var message = EmailSender.Messages.Last(x => string.Equals(x.To, email, StringComparison.OrdinalIgnoreCase));
            return Regex.Match(message.TextBody ?? string.Empty, @"code is (\d{4,8})").Groups[1].Value;
        }

        public void Dispose() => Context.Dispose();
    }

    private sealed class RecordingOtpChannel : ISqlOSOtpDeliveryChannel
    {
        public List<string> StartRequests { get; } = [];

        public string ApprovedCode => "246810";

        public Task<SqlOSOtpDeliveryStartResult> StartAsync(
            string e164PhoneNumber,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
        {
            StartRequests.Add(e164PhoneNumber);
            return Task.FromResult(new SqlOSOtpDeliveryStartResult(true, "test_verify", $"ve-{StartRequests.Count}", "pending"));
        }

        public Task<SqlOSOtpDeliveryCheckResult> CheckAsync(
            string e164PhoneNumber,
            string code,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
        {
            var approved = string.Equals(code, ApprovedCode, StringComparison.Ordinal);
            return Task.FromResult(new SqlOSOtpDeliveryCheckResult(
                approved,
                "test_verify",
                context.ProviderChallengeId,
                approved ? "approved" : "denied",
                approved ? null : "bad_code"));
        }
    }
}
