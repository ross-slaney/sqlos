using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAuthLifecycleTests
{
    [TestMethod]
    public async Task OAuthRefresh_OmittedOrganization_AfterMembershipRemoval_IsRejected()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var subject = await harness.CreateOrganizationSubjectAsync("refresh");
        var tokens = await harness.IssueTokensAsync(subject);

        subject.Membership.IsActive = false;
        await harness.Context.SaveChangesAsync();

        var act = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(tokens.RefreshToken, OrganizationId: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Session is no longer active.");
        (await harness.Context.Set<SqlOSSession>().SingleAsync(x => x.Id == tokens.SessionId))
            .RevocationReason.Should().Be("membership_inactive");
        (await harness.Context.Set<SqlOSRefreshToken>().SingleAsync(x => x.SessionId == tokens.SessionId))
            .RevokedAt.Should().NotBeNull();
        (await harness.Context.Set<SqlOSAuditEvent>()
            .AnyAsync(x => x.EventType == "auth.lifecycle.denied"
                && x.UserId == subject.User.Id
                && x.OrganizationId == subject.Organization.Id)).Should().BeTrue();
    }

    [TestMethod]
    public async Task OAuthRefresh_GraceWindow_AfterMembershipRemoval_DoesNotReturnCachedToken()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var subject = await harness.CreateOrganizationSubjectAsync("refresh-grace");
        var tokens = await harness.IssueTokensAsync(subject);
        var firstRotation = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(tokens.RefreshToken, subject.Organization.Id));

        subject.Membership.IsActive = false;
        await harness.Context.SaveChangesAsync();

        var replay = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(tokens.RefreshToken, OrganizationId: null));

        await replay.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Session is no longer active.");
        (await harness.Context.Set<SqlOSRefreshToken>()
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(firstRotation.RefreshToken)))
            .RevokedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task AuthPageSession_AfterMembershipRemoval_CannotIssueAuthorizationCode()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var subject = await harness.CreateOrganizationSubjectAsync("auth-page");
        var rawCookie = await harness.CreateAuthPageSessionAsync(subject);

        subject.Membership.IsActive = false;
        await harness.Context.SaveChangesAsync();

        var cookieRequest = new DefaultHttpContext();
        cookieRequest.Request.Headers.Cookie = $"sqlos_auth_page={rawCookie}";
        (await harness.AuthPage.TryGetSessionAsync(cookieRequest)).Should().BeNull();

        var authorizationRequest = new SqlOSAuthorizationRequest
        {
            Id = $"req_{Guid.NewGuid():N}",
            ClientApplicationId = subject.Client.Id,
            ClientApplication = subject.Client,
            RedirectUri = "https://client.example.test/callback",
            State = "lifecycle-state",
            Scope = "openid profile",
            CodeChallenge = harness.Crypto.CreatePkceCodeChallenge("verifier-lifecycle-verifier-lifecycle-123"),
            CodeChallengeMethod = "S256",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        var issue = async () => await harness.Authorization.IssueAuthorizationRedirectAsync(
            authorizationRequest,
            subject.User,
            subject.Organization.Id,
            "password",
            new DefaultHttpContext());

        await issue.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authentication session is no longer active.");
        (await harness.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
        (await harness.Crypto.FindTemporaryTokenAsync("auth_page_session", rawCookie)).Should().BeNull();
    }

    [TestMethod]
    public async Task AuthorizationCode_AfterMembershipRemoval_CannotCreateSession()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var subject = await harness.CreateOrganizationSubjectAsync("authorization-code");
        const string verifier = "authorization-code-verifier-12345678901234567890";
        var authorizationRequest = new SqlOSAuthorizationRequest
        {
            Id = $"req_{Guid.NewGuid():N}",
            ClientApplicationId = subject.Client.Id,
            ClientApplication = subject.Client,
            RedirectUri = "https://client.example.test/callback",
            State = "authorization-code-state",
            Scope = "openid profile",
            CodeChallenge = harness.Crypto.CreatePkceCodeChallenge(verifier),
            CodeChallengeMethod = "S256",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        harness.Context.Set<SqlOSAuthorizationRequest>().Add(authorizationRequest);
        await harness.Context.SaveChangesAsync();
        var redirect = await harness.Authorization.IssueAuthorizationRedirectAsync(
            authorizationRequest,
            subject.User,
            subject.Organization.Id,
            "password",
            new DefaultHttpContext());
        var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();

        subject.Membership.IsActive = false;
        await harness.Context.SaveChangesAsync();
        var exchange = async () => await harness.Authorization.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                code,
                authorizationRequest.RedirectUri,
                subject.Client.ClientId,
                verifier,
                RefreshToken: null,
                Resource: null),
            new DefaultHttpContext());

        await exchange.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Session is no longer active.");
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == subject.User.Id)).Should().Be(0);
        (await harness.Context.Set<SqlOSAuthorizationCode>().SingleAsync(x => x.CodeHash == harness.Crypto.HashToken(code)))
            .ConsumedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task InactiveOrganization_IsExcludedFromSelectionAndTokenIssuance()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var subject = await harness.CreateOrganizationSubjectAsync("inactive-org");

        await harness.Admin.UpdateOrganizationAsync(
            subject.Organization.Id,
            new SqlOSUpdateOrganizationRequest(
                subject.Organization.Name,
                subject.Organization.Slug,
                subject.Organization.PrimaryDomain,
                IsActive: false));

        (await harness.Admin.GetUserOrganizationsAsync(subject.User.Id)).Should().BeEmpty();
        (await harness.Admin.UserHasMembershipAsync(subject.User.Id, subject.Organization.Id)).Should().BeFalse();

        var issue = async () => await harness.IssueTokensAsync(subject);
        await issue.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Session is no longer active.");
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == subject.User.Id)).Should().Be(0);
    }

    [TestMethod]
    public async Task ValidateAccessToken_IdleExpiredOrLifecycleInvalidSession_IsRejected()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var idleSubject = await harness.CreateOrganizationSubjectAsync("idle");
        var idleTokens = await harness.IssueTokensAsync(idleSubject);
        var idleSession = await harness.Context.Set<SqlOSSession>().SingleAsync(x => x.Id == idleTokens.SessionId);
        idleSession.IdleExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await harness.Context.SaveChangesAsync();

        (await harness.Auth.ValidateAccessTokenAsync(idleTokens.AccessToken, idleSubject.Client.Audience))
            .Should().BeNull();
        idleSession.RevocationReason.Should().Be("session_idle_expired");

        var membershipSubject = await harness.CreateOrganizationSubjectAsync("membership");
        var membershipTokens = await harness.IssueTokensAsync(membershipSubject);
        membershipSubject.Membership.IsActive = false;
        await harness.Context.SaveChangesAsync();

        (await harness.Auth.ValidateAccessTokenAsync(
            membershipTokens.AccessToken,
            membershipSubject.Client.Audience)).Should().BeNull();
        (await harness.Context.Set<SqlOSSession>().SingleAsync(x => x.Id == membershipTokens.SessionId))
            .RevocationReason.Should().Be("membership_inactive");
    }

    [TestMethod]
    public async Task MembershipOffboarding_DoesNotRevokeAnotherOrganizationSession()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var first = await harness.CreateOrganizationSubjectAsync("tenant-a");
        var secondOrganization = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"tenant-b {Guid.NewGuid():N}", null));
        var secondMembership = await harness.Admin.CreateMembershipAsync(
            secondOrganization.Id,
            new SqlOSCreateMembershipRequest(first.User.Id, "member"));
        var second = new OrganizationSubject(
            first.User,
            secondOrganization,
            secondMembership,
            first.Client);
        var firstTokens = await harness.IssueTokensAsync(first);
        var secondTokens = await harness.IssueTokensAsync(second);
        var secondAuthPageCookie = await harness.CreateAuthPageSessionAsync(second);

        first.Membership.IsActive = false;
        await harness.Context.SaveChangesAsync();

        (await harness.Auth.ValidateAccessTokenAsync(firstTokens.AccessToken, first.Client.Audience))
            .Should().BeNull();
        (await harness.Auth.ValidateAccessTokenAsync(secondTokens.AccessToken, second.Client.Audience))
            .Should().NotBeNull();
        (await harness.Context.Set<SqlOSSession>().SingleAsync(x => x.Id == secondTokens.SessionId))
            .RevokedAt.Should().BeNull();
        (await harness.Crypto.FindTemporaryTokenAsync("auth_page_session", secondAuthPageCookie))
            .Should().NotBeNull();
    }

    [TestMethod]
    public async Task LogoutAll_AndOrgRevocation_InvalidateAuthPageSession()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var logoutSubject = await harness.CreateOrganizationSubjectAsync("logout-all");
        var logoutTokens = await harness.IssueTokensAsync(logoutSubject);
        var logoutCookie = await harness.CreateAuthPageSessionAsync(logoutSubject);

        await harness.Auth.LogoutAllAsync(logoutSubject.User.Id);

        (await harness.Crypto.FindTemporaryTokenAsync("auth_page_session", logoutCookie)).Should().BeNull();
        var refreshAfterLogout = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(logoutTokens.RefreshToken, logoutSubject.Organization.Id));
        await refreshAfterLogout.Should().ThrowAsync<InvalidOperationException>();

        var organizationSubject = await harness.CreateOrganizationSubjectAsync("org-revoke");
        var organizationCookie = await harness.CreateAuthPageSessionAsync(organizationSubject);
        await harness.Admin.UpdateOrganizationAsync(
            organizationSubject.Organization.Id,
            new SqlOSUpdateOrganizationRequest(
                organizationSubject.Organization.Name,
                organizationSubject.Organization.Slug,
                organizationSubject.Organization.PrimaryDomain,
                IsActive: false));

        (await harness.Crypto.FindTemporaryTokenAsync("auth_page_session", organizationCookie)).Should().BeNull();
    }

    [TestMethod]
    public async Task LogoutAll_WithOnlyAuthPageSession_InvalidatesCookie()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var subject = await harness.CreateOrganizationSubjectAsync("auth-page-only");
        var authPageCookie = await harness.CreateAuthPageSessionAsync(subject);

        await harness.Auth.LogoutAllAsync(subject.User.Id);

        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == subject.User.Id)).Should().Be(0);
        (await harness.Crypto.FindTemporaryTokenAsync("auth_page_session", authPageCookie)).Should().BeNull();
    }

    [TestMethod]
    public async Task PasswordReset_InvalidatesOAuthAndAuthPageSessions()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var subject = await harness.CreateOrganizationSubjectAsync("password-reset");
        var tokens = await harness.IssueTokensAsync(subject);
        var authPageCookie = await harness.CreateAuthPageSessionAsync(subject);
        const string verifier = "password-reset-verifier-123456789012345678901";
        var authorizationRequest = new SqlOSAuthorizationRequest
        {
            Id = $"req_{Guid.NewGuid():N}",
            ClientApplicationId = subject.Client.Id,
            ClientApplication = subject.Client,
            RedirectUri = "https://client.example.test/callback",
            State = "password-reset-state",
            Scope = "openid",
            CodeChallenge = harness.Crypto.CreatePkceCodeChallenge(verifier),
            CodeChallengeMethod = "S256",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        harness.Context.Set<SqlOSAuthorizationRequest>().Add(authorizationRequest);
        await harness.Context.SaveChangesAsync();
        var codeRedirect = await harness.Authorization.IssueAuthorizationRedirectAsync(
            authorizationRequest,
            subject.User,
            subject.Organization.Id,
            "password",
            new DefaultHttpContext());
        var pendingCode = QueryHelpers.ParseQuery(new Uri(codeRedirect).Query)["code"].ToString();
        var pendingMfaToken = await harness.Crypto.CreateTemporaryTokenAsync(
            SqlOSAuthService.MfaChallengePurpose,
            subject.User.Id,
            subject.Client.Id,
            subject.Organization.Id,
            new { Flow = "client" },
            TimeSpan.FromMinutes(5));
        var now = DateTime.UtcNow;
        var pendingDevice = new SqlOSDeviceAuthorization
        {
            Id = $"dev_{Guid.NewGuid():N}",
            DeviceCodeHash = harness.Crypto.HashToken("pending-device-code"),
            UserCodeHash = harness.Crypto.HashToken("PENDING1"),
            UserCode = "PENDING1",
            ClientApplicationId = subject.Client.Id,
            Status = SqlOSDeviceAuthorizationService.ApprovedStatus,
            ApprovedUserId = subject.User.Id,
            ApprovedOrganizationId = subject.Organization.Id,
            AuthenticationMethod = "password",
            CreatedAt = now,
            ApprovedAt = now,
            ExpiresAt = now.AddMinutes(10)
        };
        var emailChallenge = new SqlOSEmailOtpChallenge
        {
            Id = $"emc_{Guid.NewGuid():N}",
            ChallengeTokenHash = harness.Crypto.HashToken("email-challenge"),
            CodeHash = harness.Crypto.HashToken("email-code"),
            Email = subject.User.DefaultEmail!,
            NormalizedEmail = SqlOSAdminService.NormalizeEmail(subject.User.DefaultEmail!),
            UserId = subject.User.Id,
            RequestedOrganizationId = subject.Organization.Id,
            CreatedAt = now,
            LastSentAt = now,
            ExpiresAt = now.AddMinutes(10)
        };
        var phoneChallenge = new SqlOSPhoneOtpChallenge
        {
            Id = $"phc_{Guid.NewGuid():N}",
            PhoneNumberHash = harness.Crypto.HashToken("+15555550199"),
            PhoneNumberEncrypted = "protected",
            MaskedPhoneNumber = "+1******0199",
            UserId = subject.User.Id,
            RequestedOrganizationId = subject.Organization.Id,
            CreatedAt = now,
            LastSentAt = now,
            ExpiresAt = now.AddMinutes(10)
        };
        harness.Context.Set<SqlOSDeviceAuthorization>().Add(pendingDevice);
        harness.Context.Set<SqlOSEmailOtpChallenge>().Add(emailChallenge);
        harness.Context.Set<SqlOSPhoneOtpChallenge>().Add(phoneChallenge);
        await harness.Context.SaveChangesAsync();
        var resetToken = await harness.Auth.CreatePasswordResetTokenAsync(
            new SqlOSForgotPasswordRequest(subject.User.DefaultEmail!));

        await harness.Auth.ResetPasswordAsync(
            new SqlOSResetPasswordRequest(resetToken, "NewPassword123!"));

        (await harness.Crypto.FindTemporaryTokenAsync("auth_page_session", authPageCookie)).Should().BeNull();
        (await harness.Context.Set<SqlOSSession>().SingleAsync(x => x.Id == tokens.SessionId))
            .RevocationReason.Should().Be("password_reset");
        (await harness.Context.Set<SqlOSAuthorizationCode>()
            .SingleAsync(x => x.CodeHash == harness.Crypto.HashToken(pendingCode))).ConsumedAt.Should().NotBeNull();
        (await harness.Crypto.FindTemporaryTokenAsync(SqlOSAuthService.MfaChallengePurpose, pendingMfaToken))
            .Should().BeNull();
        pendingDevice.Status.Should().Be(SqlOSDeviceAuthorizationService.DeniedStatus);
        pendingDevice.DeniedAt.Should().NotBeNull();
        emailChallenge.InvalidatedReason.Should().Be("password_reset");
        phoneChallenge.InvalidatedReason.Should().Be("password_reset");
        var refresh = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(tokens.RefreshToken, subject.Organization.Id));
        await refresh.Should().ThrowAsync<InvalidOperationException>();
        var codeExchange = async () => await harness.Authorization.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                pendingCode,
                authorizationRequest.RedirectUri,
                subject.Client.ClientId,
                verifier,
                RefreshToken: null,
                Resource: null),
            new DefaultHttpContext());
        await codeExchange.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authorization code is no longer valid.");
    }

    [TestMethod]
    public async Task ClientDisable_StillInvalidatesAccessAndRefreshTokens()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var subject = await harness.CreateOrganizationSubjectAsync("client-disable");
        var tokens = await harness.IssueTokensAsync(subject);

        await harness.Admin.DisableClientAsync(subject.Client.Id);

        (await harness.Auth.ValidateAccessTokenAsync(tokens.AccessToken, subject.Client.Audience)).Should().BeNull();
        var refresh = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(tokens.RefreshToken, subject.Organization.Id));
        await refresh.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task InactiveUser_PasswordAuthenticationUsesGenericFailure()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var subject = await harness.CreateOrganizationSubjectAsync("inactive-password");
        subject.User.IsActive = false;
        await harness.Context.SaveChangesAsync();

        var action = async () => await harness.Authorization.AuthenticatePasswordAsync(
            subject.User.DefaultEmail!,
            "OldPassword123!");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
    }

    private sealed class LifecycleHarness : IAsyncDisposable
    {
        public required TestSqlOSInMemoryDbContext Context { get; init; }
        public required SqlOSCryptoService Crypto { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSAuthService Auth { get; init; }
        public required SqlOSAuthPageSessionService AuthPage { get; init; }
        public required SqlOSAuthorizationServerService Authorization { get; init; }

        public static async Task<LifecycleHarness> CreateAsync()
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var authOptions = new SqlOSAuthServerOptions();
            authOptions.SeedBrowserClient(
                "lifecycle-client",
                "Lifecycle Client",
                "https://client.example.test/callback");
            var options = Options.Create(authOptions);
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var crypto = new SqlOSCryptoService(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
            var authPage = new SqlOSAuthPageSessionService(context, crypto, settings);
            var authorization = new SqlOSAuthorizationServerService(
                context,
                admin,
                auth,
                crypto,
                settings,
                authPage,
                options);

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.EnsureDefaultSettingsAsync();

            return new LifecycleHarness
            {
                Context = context,
                Crypto = crypto,
                Admin = admin,
                Auth = auth,
                AuthPage = authPage,
                Authorization = authorization
            };
        }

        public async Task<OrganizationSubject> CreateOrganizationSubjectAsync(string prefix)
        {
            var user = await Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                $"{prefix} user",
                $"{prefix}-{Guid.NewGuid():N}@example.com",
                "OldPassword123!"));
            var organization = await Admin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest($"{prefix} organization {Guid.NewGuid():N}", null));
            var membership = await Admin.CreateMembershipAsync(
                organization.Id,
                new SqlOSCreateMembershipRequest(user.Id, "member"));
            var client = await Context.Set<SqlOSClientApplication>()
                .SingleAsync(x => x.ClientId == "lifecycle-client");
            return new OrganizationSubject(user, organization, membership, client);
        }

        public Task<SqlOSTokenResponse> IssueTokensAsync(OrganizationSubject subject)
            => Auth.CreateSessionTokensForUserAsync(
                subject.User,
                subject.Client,
                subject.Organization.Id,
                "password",
                "LifecycleHarness",
                "203.0.113.20");

        public Task<string> CreateAuthPageSessionAsync(OrganizationSubject subject)
            => Crypto.CreateTemporaryTokenAsync(
                "auth_page_session",
                subject.User.Id,
                clientApplicationId: null,
                organizationId: subject.Organization.Id,
                payload: new { AuthenticationMethod = "password" },
                lifetime: TimeSpan.FromMinutes(30));

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed record OrganizationSubject(
        SqlOSUser User,
        SqlOSOrganization Organization,
        SqlOSMembership Membership,
        SqlOSClientApplication Client);
}
