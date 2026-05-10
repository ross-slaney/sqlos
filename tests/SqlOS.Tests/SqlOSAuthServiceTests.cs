using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.RegularExpressions;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAuthServiceTests
{
    [TestMethod]
    public async Task LoginWithMultipleOrganizations_ReturnsPendingAuthToken()
    {
        using var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions();
        authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
        var options = Options.Create(authOptions);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);

        await crypto.EnsureActiveSigningKeyAsync();
        await admin.UpsertSeededClientsAsync();

        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Alice", "alice@example.com", "P@ssword123!"));
        var org1 = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Org One", null));
        var org2 = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Org Two", null));
        await admin.CreateMembershipAsync(org1.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        await admin.CreateMembershipAsync(org2.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));

        var result = await auth.LoginWithPasswordAsync(new SqlOSPasswordLoginRequest("alice@example.com", "P@ssword123!", "test-client", null), new DefaultHttpContext());

        result.RequiresOrganizationSelection.Should().BeTrue();
        result.PendingAuthToken.Should().NotBeNullOrWhiteSpace();
        result.Tokens.Should().BeNull();
        result.Organizations.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task EmailOtpVerify_WhenAuthorizationChallengeIsUsedAsStandalone_DoesNotConsumeChallenge()
    {
        using var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions();
        authOptions.SeedAuthPage(page => page.EnabledCredentialTypes = ["email_otp"]);
        var options = Options.Create(authOptions);
        var emailSender = new TestAuthEmailSender { IsConfigured = true };
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);

        await settings.UpsertSeededAuthPageSettingsAsync();
        await admin.CreateUserAsync(new SqlOSCreateUserRequest("Alice", "alice@example.com", "P@ssword123!"));

        var challenge = await emailOtp.StartForAuthorizationRequestAsync(
            new SqlOSAuthorizationRequest { Id = "req_bound" },
            "alice@example.com");
        var code = Regex.Match(emailSender.Messages.Single().TextBody!, @"\b\d{4,8}\b").Value;

        var act = async () => await emailOtp.VerifyAsync(
            new SqlOSEmailOtpVerifyRequest(challenge.ChallengeToken, code),
            expectedAuthorizationRequestId: null,
            requireAuthorizationRequestMatch: true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sign-in code is invalid or expired.");

        var storedChallenge = await context.Set<SqlOSEmailOtpChallenge>().SingleAsync();
        storedChallenge.ConsumedAt.Should().BeNull();
    }

    [TestMethod]
    public async Task RequestEmailOtpSignupAsync_SendsChallenge_ForNewUser()
    {
        var harness = await EmailOtpHarness.CreateAsync();

        var start = await harness.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "New User",
            "new-user@example.com",
            "test-client",
            "New Org",
            OrganizationId: null,
            CustomFields: null));

        start.ChallengeToken.Should().NotBeNullOrWhiteSpace();
        start.SignupToken.Should().NotBeNullOrWhiteSpace();
        harness.EmailSender.Messages.Should().ContainSingle();
        harness.EmailSender.Messages.Single().To.Should().Be("new-user@example.com");
    }

    [TestMethod]
    public async Task VerifyEmailOtpSignupAsync_CreatesVerifiedUserMembershipAndTokens()
    {
        var harness = await EmailOtpHarness.CreateAsync();

        var start = await harness.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Verified User",
            "verified-signup@example.com",
            "test-client",
            "Verified Org",
            OrganizationId: null,
            CustomFields: null));

        var result = await harness.Auth.VerifyEmailOtpSignupAsync(
            new SqlOSEmailOtpSignupVerifyRequest(
                start.SignupToken,
                start.ChallengeToken,
                GetLatestCode(harness.EmailSender, "verified-signup@example.com")),
            new DefaultHttpContext());

        result.RequiresOrganizationSelection.Should().BeFalse();
        result.Tokens.Should().NotBeNull();
        result.Tokens!.AccessToken.Should().NotBeNullOrWhiteSpace();

        var email = await harness.Context.Set<SqlOSUserEmail>().SingleAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail("verified-signup@example.com"));
        email.IsVerified.Should().BeTrue();
        var session = await harness.Context.Set<SqlOSSession>().SingleAsync();
        session.AuthenticationMethod.Should().Be("email_otp");
        session.UserId.Should().Be(email.UserId);
        result.Tokens.OrganizationId.Should().NotBeNullOrWhiteSpace();
        var hasMembership = await harness.Context.Set<SqlOSMembership>()
            .AnyAsync(x => x.UserId == email.UserId && x.OrganizationId == result.Tokens.OrganizationId);
        hasMembership.Should().BeTrue();
    }

    [TestMethod]
    public async Task RequestEmailOtpSignupAsync_RejectsExistingUser()
    {
        var harness = await EmailOtpHarness.CreateAsync();
        await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Existing User", "existing@example.com", "P@ssword123!"));

        var act = async () => await harness.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Existing User",
            "existing@example.com",
            "test-client",
            "Existing Org",
            OrganizationId: null,
            CustomFields: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("An account already exists for this email. Sign in with an email code instead.");
    }

    [TestMethod]
    public async Task VerifyEmailOtpSignupAsync_RejectsReusedSignupToken()
    {
        var harness = await EmailOtpHarness.CreateAsync();
        var start = await harness.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Reuse User",
            "reuse@example.com",
            "test-client",
            "Reuse Org",
            OrganizationId: null,
            CustomFields: null));
        var code = GetLatestCode(harness.EmailSender, "reuse@example.com");

        await harness.Auth.VerifyEmailOtpSignupAsync(
            new SqlOSEmailOtpSignupVerifyRequest(start.SignupToken, start.ChallengeToken, code),
            new DefaultHttpContext());

        var act = async () => await harness.Auth.VerifyEmailOtpSignupAsync(
            new SqlOSEmailOtpSignupVerifyRequest(start.SignupToken, start.ChallengeToken, code),
            new DefaultHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sign-in code is invalid or expired.");
    }

    [TestMethod]
    public async Task VerifyEmailOtpSignupAsync_RejectsWrongSignupChallengePair()
    {
        var harness = await EmailOtpHarness.CreateAsync();
        var first = await harness.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "First User",
            "first-pair@example.com",
            "test-client",
            "First Org",
            OrganizationId: null,
            CustomFields: null));
        var second = await harness.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Second User",
            "second-pair@example.com",
            "test-client",
            "Second Org",
            OrganizationId: null,
            CustomFields: null));

        var act = async () => await harness.Auth.VerifyEmailOtpSignupAsync(
            new SqlOSEmailOtpSignupVerifyRequest(
                first.SignupToken,
                second.ChallengeToken,
                GetLatestCode(harness.EmailSender, "second-pair@example.com")),
            new DefaultHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sign-in code is invalid or expired.");
    }

    [TestMethod]
    public async Task RequestEmailOtpSignupAsync_RateLimitsByEmailIpAndClient()
    {
        var byEmail = await EmailOtpHarness.CreateAsync(options =>
        {
            options.EmailOtp.MaxChallengesPerHour = 1;
        });
        await byEmail.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Email Limit",
            "email-limit@example.com",
            "test-client",
            "Org",
            OrganizationId: null,
            CustomFields: null));
        var emailAct = async () => await byEmail.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Email Limit",
            "email-limit@example.com",
            "test-client",
            "Org",
            OrganizationId: null,
            CustomFields: null));
        await emailAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many sign-in code requests. Try again later.");

        var byIp = await EmailOtpHarness.CreateAsync(options =>
        {
            options.EmailOtp.MaxChallengesPerHour = 100;
            options.EmailOtp.MaxChallengesPerIpPerHour = 1;
            options.EmailOtp.MaxChallengesPerClientPerHour = 100;
        });
        var ipContext = new DefaultHttpContext();
        ipContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.10");
        await byIp.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "IP One",
            "ip-one@example.com",
            "test-client",
            "Org",
            OrganizationId: null,
            CustomFields: null), ipContext);
        var ipAct = async () => await byIp.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "IP Two",
            "ip-two@example.com",
            "test-client",
            "Org",
            OrganizationId: null,
            CustomFields: null), ipContext);
        await ipAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many sign-in code requests. Try again later.");

        var byClient = await EmailOtpHarness.CreateAsync(options =>
        {
            options.EmailOtp.MaxChallengesPerHour = 100;
            options.EmailOtp.MaxChallengesPerIpPerHour = 100;
            options.EmailOtp.MaxChallengesPerClientPerHour = 1;
        });
        await byClient.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Client One",
            "client-one@example.com",
            "test-client",
            "Org",
            OrganizationId: null,
            CustomFields: null));
        var clientAct = async () => await byClient.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Client Two",
            "client-two@example.com",
            "test-client",
            "Org",
            OrganizationId: null,
            CustomFields: null));
        await clientAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many sign-in code requests. Try again later.");
    }

    [TestMethod]
    public async Task RequestEmailOtpSignupAsync_UsesCustomEmailMessageBuilder()
    {
        var harness = await EmailOtpHarness.CreateAsync(options =>
        {
            options.EmailOtp.ApplicationName = "ChecklistSquad";
            options.EmailOtp.BuildMessage = context => new SqlOS.AuthServer.Interfaces.SqlOSAuthEmailMessage(
                context.Email,
                $"Custom {context.Purpose} {context.ApplicationName}",
                $"<p>{context.Code}</p>",
                $"Custom body for {context.MaskedEmail}");
        });

        await harness.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Custom Email User",
            "custom-email@example.com",
            "test-client",
            "Custom Org",
            OrganizationId: null,
            CustomFields: null));

        var message = harness.EmailSender.Messages.Single();
        message.Subject.Should().Be("Custom signup ChecklistSquad");
        message.TextBody.Should().Be("Custom body for cu***@example.com");
    }

    /* ─────────────────────────────────────────────────────────────────────────
       Refresh token grace window tests (issue #18)
       ───────────────────────────────────────────────────────────────────────── */

    [TestMethod]
    public async Task Refresh_WithinGraceWindow_ReturnsSameAccessToken()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("alice");

        // First refresh — rotates the token normally.
        var firstRefresh = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        // Second refresh with the SAME (now consumed) original token —
        // should hit the grace window and return the SAME access token.
        var secondRefresh = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        secondRefresh.AccessToken.Should().Be(firstRefresh.AccessToken,
            "the grace window should return the cached access token instead of generating a new one");
        secondRefresh.RefreshToken.Should().NotBeNullOrWhiteSpace();
        secondRefresh.RefreshToken.Should().NotBe(initialTokens.RefreshToken,
            "callers should still get a usable forward refresh token");
    }

    [TestMethod]
    public async Task Refresh_WithinGraceWindow_DoesNotRevokeFamily()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("alice");

        var firstRefresh = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        // Second call within the grace window — should NOT trigger replay detection.
        await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        // The forward refresh token from the first call should still be usable.
        var thirdRefresh = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(firstRefresh.RefreshToken, firstRefresh.OrganizationId));

        thirdRefresh.AccessToken.Should().NotBeNullOrWhiteSpace(
            "the family should not have been revoked by a legitimate concurrent refresh");
    }

    [TestMethod]
    public async Task Refresh_OutsideGraceWindow_TriggersReplayDetection()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 1);
        var initialTokens = await harness.SignUpAsync("alice");

        await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        // Manually expire the grace window by backdating ConsumedAt.
        var consumed = await harness.Context.Set<SqlOSRefreshToken>()
            .FirstAsync(x => x.TokenHash == harness.Crypto.HashToken(initialTokens.RefreshToken));
        consumed.ConsumedAt = DateTime.UtcNow.AddSeconds(-10);
        await harness.Context.SaveChangesAsync();

        // Second call after the window — should throw and revoke the family.
        var act = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Refresh token has already been used.");
    }

    [TestMethod]
    public async Task Refresh_GraceWindowDisabled_TriggersImmediateReplayDetection()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 0);
        var initialTokens = await harness.SignUpAsync("alice");

        await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        // With grace window disabled, even an immediate second call should
        // trigger replay detection.
        var act = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Refresh token has already been used.");
    }

    [TestMethod]
    public async Task Refresh_DefaultGraceWindow_IsThirtySeconds()
    {
        // Verify the default value is the documented 30 seconds (matches Okta).
        var options = new SqlOSAuthServerOptions();
        options.RefreshTokenGraceWindowSeconds.Should().Be(30);
    }

    [TestMethod]
    public async Task Refresh_GraceWindowSettingPersists_ViaSettingsService()
    {
        using var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions { RefreshTokenGraceWindowSeconds = 30 };
        var options = Options.Create(authOptions);
        var settingsService = new SqlOSSettingsService(context, options, new TestAuthEmailSender());

        // Update via the dashboard API surface.
        var updated = await settingsService.UpdateSecuritySettingsAsync(new SqlOSUpdateSecuritySettingsRequest(
            RefreshTokenLifetimeMinutes: 60,
            SessionIdleTimeoutMinutes: 60,
            SessionAbsoluteLifetimeMinutes: 1440,
            SigningKeyRotationIntervalDays: 90,
            SigningKeyGraceWindowDays: 7,
            SigningKeyRetiredCleanupDays: 30,
            RefreshTokenGraceWindowSeconds: 45));

        updated.RefreshTokenGraceWindowSeconds.Should().Be(45);

        // And the resolved settings should reflect it.
        var resolved = await settingsService.GetResolvedSecuritySettingsAsync();
        resolved.RefreshTokenGraceWindow.Should().Be(TimeSpan.FromSeconds(45));
    }

    [TestMethod]
    public async Task Refresh_NegativeGraceWindow_Rejected()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var settingsService = new SqlOSSettingsService(context, options, new TestAuthEmailSender());

        var act = async () => await settingsService.UpdateSecuritySettingsAsync(new SqlOSUpdateSecuritySettingsRequest(
            RefreshTokenLifetimeMinutes: 60,
            SessionIdleTimeoutMinutes: 60,
            SessionAbsoluteLifetimeMinutes: 1440,
            SigningKeyRotationIntervalDays: 90,
            SigningKeyGraceWindowDays: 7,
            SigningKeyRetiredCleanupDays: 30,
            RefreshTokenGraceWindowSeconds: -1));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Refresh token grace window must be 0 or greater.");
    }

    [TestMethod]
    public async Task Refresh_GraceWindowExceedingAccessTokenLifetime_Rejected()
    {
        // Issue #19 review fix #5: a grace window larger than the access token
        // lifetime would let the cached JWT expire while still inside the
        // window, returning unusable cached responses. Validation must reject.
        using var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions
        {
            AccessTokenLifetime = TimeSpan.FromMinutes(10) // 600 seconds
        };
        var options = Options.Create(authOptions);
        var settingsService = new SqlOSSettingsService(context, options, new TestAuthEmailSender());

        var act = async () => await settingsService.UpdateSecuritySettingsAsync(new SqlOSUpdateSecuritySettingsRequest(
            RefreshTokenLifetimeMinutes: 60,
            SessionIdleTimeoutMinutes: 60,
            SessionAbsoluteLifetimeMinutes: 1440,
            SigningKeyRotationIntervalDays: 90,
            SigningKeyGraceWindowDays: 7,
            SigningKeyRetiredCleanupDays: 30,
            RefreshTokenGraceWindowSeconds: 700)); // > 600 seconds

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must not exceed the access token lifetime*");
    }

    [TestMethod]
    public async Task Refresh_GraceWindow_CachedAccessTokenIsEncryptedAtRest()
    {
        // Issue #19 review fix #6: the ReplacementAccessToken column must
        // store an encrypted value, not the raw JWT. We assert by checking
        // that the persisted column does NOT contain the raw access token
        // string AND that the grace window path can still successfully
        // round-trip the value back to the original JWT.
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("encrypt");

        var firstRefresh = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        // Read the persisted row directly and verify the cached value is
        // NOT the raw access token JWT.
        var consumed = await harness.Context.Set<SqlOSRefreshToken>()
            .FirstAsync(x => x.TokenHash == harness.Crypto.HashToken(initialTokens.RefreshToken));

        consumed.ReplacementAccessToken.Should().NotBeNullOrEmpty();
        consumed.ReplacementAccessToken.Should().NotBe(firstRefresh.AccessToken,
            "the cached access token must be encrypted at rest, not stored as plaintext");

        // And the grace window path must still recover the original JWT.
        var graceHit = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
        graceHit.AccessToken.Should().Be(firstRefresh.AccessToken,
            "decryption must round-trip back to the original JWT");
    }

    [TestMethod]
    public async Task Refresh_GraceWindow_ResponseExpiryMatchesCachedJwt()
    {
        // Issue #19 review fix #1: the AccessTokenExpiresAt in the grace
        // window response must match the expiry that was cached at rotation
        // time, NOT a new computation from DateTime.UtcNow.
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("expiry");

        var firstRefresh = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        // Wait briefly so DateTime.UtcNow has visibly drifted from the
        // cached expiry. If the grace window path used UtcNow, the second
        // response's expiry would be visibly later than the first's.
        await Task.Delay(50);

        var graceHit = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        graceHit.AccessTokenExpiresAt.Should().Be(firstRefresh.AccessTokenExpiresAt,
            "the grace window response must echo the cached expiry, not recompute from UtcNow");
    }

    [TestMethod]
    public async Task Refresh_GraceWindow_RejectsOrganizationSwitch()
    {
        // Issue #19 review fix #1: a caller within the grace window must
        // not be able to switch the organization the cached JWT was minted
        // for. Allowing this would skip the membership check.
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("org");

        await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        // Same refresh token, different org id → must throw, not silently
        // return the cached JWT for the original org.
        var act = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, OrganizationId: "org-id-the-caller-does-not-have-membership-in"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Organization does not match the original refresh.");
    }

    [TestMethod]
    public async Task Refresh_GraceWindow_RejectedWhenCachedJwtIsExpired()
    {
        // Issue #19 review fix #1+#5: even if we're inside the grace window
        // by elapsed time, if the cached JWT has expired, we must NOT
        // return it. Backdate ReplacementAccessTokenExpiresAt to simulate.
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("expired");

        await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        // Backdate the cached JWT expiry past now (the grace window itself
        // is still open by ConsumedAt + 30s).
        var consumed = await harness.Context.Set<SqlOSRefreshToken>()
            .FirstAsync(x => x.TokenHash == harness.Crypto.HashToken(initialTokens.RefreshToken));
        consumed.ReplacementAccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await harness.Context.SaveChangesAsync();

        // Caller must not get an expired token; falls through to replay
        // detection.
        var act = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Refresh token has already been used.");
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }

    private static string GetLatestCode(TestAuthEmailSender sender, string email)
    {
        var message = sender.Messages.Last(x => string.Equals(x.To, email, StringComparison.OrdinalIgnoreCase));
        return Regex.Match(message.TextBody ?? string.Empty, @"\b\d{4,8}\b").Value;
    }

    private sealed class EmailOtpHarness : IDisposable
    {
        public required TestSqlOSInMemoryDbContext Context { get; init; }
        public required SqlOSAuthService Auth { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required TestAuthEmailSender EmailSender { get; init; }

        public static async Task<EmailOtpHarness> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);

            var authOptions = new SqlOSAuthServerOptions();
            authOptions.EnableLocalPasswordAuth = false;
            authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            authOptions.SeedAuthPage(page =>
            {
                page.EnabledCredentialTypes = ["email_otp"];
                page.EnablePasswordSignup = false;
            });
            configure?.Invoke(authOptions);

            var options = Options.Create(authOptions);
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var crypto = new SqlOSCryptoService(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.UpsertSeededAuthPageSettingsAsync();

            return new EmailOtpHarness
            {
                Context = context,
                Auth = auth,
                Admin = admin,
                EmailSender = emailSender
            };
        }

        public void Dispose()
            => Context.Dispose();
    }

    /// <summary>
    /// Compact harness for refresh-token tests. Wires up the in-memory
    /// context, options, and an authenticated user with a valid refresh
    /// token ready to exercise refresh flows.
    /// </summary>
    private sealed class TestHarness
    {
        public required TestSqlOSInMemoryDbContext Context { get; init; }
        public required SqlOSAuthService Auth { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSCryptoService Crypto { get; init; }
        public required SqlOSAuthServerOptions Options { get; init; }

        public static async Task<TestHarness> CreateAsync(int graceWindowSeconds = 30)
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);

            var authOptions = new SqlOSAuthServerOptions
            {
                RefreshTokenGraceWindowSeconds = graceWindowSeconds
            };
            authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            var options = Microsoft.Extensions.Options.Options.Create(authOptions);

            // Inject a real ephemeral data protection provider so the
            // ReplacementAccessToken cache is encrypted at rest as in production.
            var crypto = new SqlOSCryptoService(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender();
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();

            return new TestHarness
            {
                Context = context,
                Auth = auth,
                Admin = admin,
                Crypto = crypto,
                Options = authOptions
            };
        }

        public async Task<SqlOSTokenResponse> SignUpAsync(string namePrefix)
        {
            var http = new DefaultHttpContext();
            http.Request.Headers.UserAgent = "GraceWindowTest";
            var signup = await Auth.SignUpAsync(new SqlOSSignupRequest(
                $"{namePrefix} Tester",
                $"{namePrefix}-{Guid.NewGuid():N}@example.com",
                "P@ssword123!",
                $"{namePrefix} Org",
                "test-client",
                null), http);
            return signup.Tokens!;
        }
    }
}
