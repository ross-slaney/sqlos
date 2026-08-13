using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Configuration;
using SqlOS.Email.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAuthServiceTests
{
    private const string UnauthorizedOrganizationJoinMessage =
        "Joining an existing organization requires an invitation or approved join policy.";
    private const string ValidPkceCodeChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task LoginWithMultipleOrganizations_ReturnsPendingAuthToken()
    {
        using var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions();
        authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
        var options = Options.Create(authOptions);
        var crypto = TestCryptoService.Create(context, options);
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
    public async Task TotpEnrollment_WithDefaultOptionalPolicy_StoresProtectedSecretAndRecoveryCodes()
    {
        var harness = await TestHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Mfa User",
            $"mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));

        var enrollment = await harness.Auth.StartTotpEnrollmentAsync(
            user.Id,
            new SqlOSTotpEnrollmentStartRequest("Test Authenticator"));
        var code = harness.Totp.GenerateCodeForTesting(enrollment.Secret);
        var result = await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, code),
            CreatePasswordHttpContext("203.0.113.200"));

        result.AuthenticatorId.Should().Be(enrollment.AuthenticatorId);
        result.RecoveryCodes.Should().HaveCount(harness.Options.Mfa.Totp.RecoveryCodeCount);
        enrollment.ProvisioningUri.Should().StartWith("otpauth://totp/");
        enrollment.QrCodeDataUrl.Should().StartWith("data:image/svg+xml;charset=utf-8,");
        Uri.UnescapeDataString(enrollment.QrCodeDataUrl).Should().Contain("<svg");

        var row = await harness.Context.Set<SqlOSUserAuthenticator>()
            .SingleAsync(x => x.Id == enrollment.AuthenticatorId);
        row.IsConfirmed.Should().BeTrue();
        row.SecretProtected.Should().StartWith("dp:");
        row.SecretProtected.Should().NotContain(enrollment.Secret);

        var status = await harness.Auth.GetMfaStatusAsync(user.Id);
        status.MfaEnabled.Should().BeTrue();
        status.Required.Should().BeTrue();
        status.EnrollmentRequired.Should().BeFalse();
        status.HasTotp.Should().BeTrue();
        status.RecoveryCodeCount.Should().Be(harness.Options.Mfa.Totp.RecoveryCodeCount);
    }

    [TestMethod]
    public async Task HeadlessPasswordLogin_WhenMfaEnrollmentRequired_ReturnsTotpEnrollmentQrCode()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            options.Mfa.Enabled = true;
            options.Mfa.RequireForAllUsersByDefault = true;
            options.Mfa.AllowUserSelfEnrollmentByDefault = true;
            options.Mfa.RecoveryCodesEnabledByDefault = true;
            options.UseHeadlessAuthPage(headless =>
            {
                headless.BuildUiUrl = ctx =>
                    $"https://app.example.test/authorize?request={Uri.EscapeDataString(ctx.RequestId ?? string.Empty)}&view={Uri.EscapeDataString(ctx.View)}";
            });
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Headless MFA",
            $"headless-mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var authorizationRequest = await harness.Authorization.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                "test-client",
                "https://client.example.test/callback",
                "headless-mfa",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                user.DefaultEmail,
                null,
                null,
                "headless",
                null));

        var loginResult = await harness.Headless.PasswordLoginAsync(
            CreatePasswordHttpContext("203.0.113.210"),
            new SqlOSHeadlessPasswordLoginRequest(
                authorizationRequest.Id,
                user.DefaultEmail!,
                "P@ssword123!"));

        loginResult.Type.Should().Be("view");
        loginResult.ViewModel.Should().NotBeNull();
        loginResult.ViewModel!.View.Should().Be("mfa-enroll");
        loginResult.ViewModel.MfaToken.Should().NotBeNullOrWhiteSpace();
        loginResult.ViewModel.RequiresMfaEnrollment.Should().BeTrue();
        loginResult.ViewModel.TotpEnrollment.Should().NotBeNull();
        loginResult.ViewModel.TotpEnrollment!.QrCodeDataUrl.Should().StartWith("data:image/svg+xml;charset=utf-8,");

        var verificationCode = harness.Totp.GenerateCodeForTesting(loginResult.ViewModel.TotpEnrollment.Secret);
        var verifyResult = await harness.Headless.VerifyMfaTotpEnrollmentAsync(
            CreatePasswordHttpContext("203.0.113.210"),
            new SqlOSHeadlessMfaTotpEnrollmentVerifyRequest(
                authorizationRequest.Id,
                loginResult.ViewModel.MfaToken!,
                loginResult.ViewModel.TotpEnrollment.EnrollmentToken,
                verificationCode));

        verifyResult.Type.Should().Be("redirect");
        verifyResult.RedirectUrl.Should().StartWith("https://client.example.test/callback");
        verifyResult.RedirectUrl.Should().Contain("code=");
    }

    [TestMethod]
    public async Task HeadlessPasswordLogin_WhenUserHasTotp_ReturnsMfaChallengeAndCompletes()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            options.Mfa.Enabled = true;
            options.Mfa.AllowUserSelfEnrollmentByDefault = true;
            options.Mfa.RecoveryCodesEnabledByDefault = true;
            options.UseHeadlessAuthPage(headless =>
            {
                headless.BuildUiUrl = ctx =>
                    $"https://app.example.test/authorize?request={Uri.EscapeDataString(ctx.RequestId ?? string.Empty)}&view={Uri.EscapeDataString(ctx.View)}";
            });
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Headless MFA Challenge",
            $"headless-mfa-challenge-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var enrollment = await harness.Auth.StartTotpEnrollmentAsync(
            user.Id,
            new SqlOSTotpEnrollmentStartRequest("Challenge Authenticator"));
        var enrollmentCode = harness.Totp.GenerateCodeForTesting(enrollment.Secret);
        await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, enrollmentCode),
            CreatePasswordHttpContext("203.0.113.211"));
        var authorizationRequest = await harness.Authorization.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                "test-client",
                "https://client.example.test/callback",
                "headless-mfa-challenge",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                user.DefaultEmail,
                null,
                null,
                "headless",
                null));

        var loginResult = await harness.Headless.PasswordLoginAsync(
            CreatePasswordHttpContext("203.0.113.211"),
            new SqlOSHeadlessPasswordLoginRequest(
                authorizationRequest.Id,
                user.DefaultEmail!,
                "P@ssword123!"));

        loginResult.Type.Should().Be("view");
        loginResult.ViewModel.Should().NotBeNull();
        loginResult.ViewModel!.View.Should().Be("mfa");
        loginResult.ViewModel.MfaToken.Should().NotBeNullOrWhiteSpace();
        loginResult.ViewModel.RequiresMfaEnrollment.Should().BeFalse();
        loginResult.ViewModel.TotpEnrollment.Should().BeNull();
        loginResult.ViewModel.MfaMethods.Should().NotBeNull();
        loginResult.ViewModel.MfaMethods!.Should().Contain(SqlOSMfaFactorTypes.Totp);

        var challengeCode = harness.Totp.GenerateCodeForTesting(
            enrollment.Secret,
            DateTimeOffset.UtcNow.AddSeconds(harness.Options.Mfa.Totp.PeriodSeconds));
        var verifyResult = await harness.Headless.VerifyMfaAsync(
            CreatePasswordHttpContext("203.0.113.211"),
            new SqlOSHeadlessMfaVerifyRequest(
                authorizationRequest.Id,
                loginResult.ViewModel.MfaToken!,
                challengeCode));

        verifyResult.Type.Should().Be("redirect");
        verifyResult.RedirectUrl.Should().StartWith("https://client.example.test/callback");
        verifyResult.RedirectUrl.Should().Contain("code=");
    }

    [TestMethod]
    public async Task HeadlessPasswordLogin_WithInvitationAndRequiredOrgMfa_ForcesEnrollmentBeforeRedirect()
    {
        var harness = await TestHarness.CreateAsync(configure: ConfigureHeadlessMfa);
        var organization = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"Invite MFA {Guid.NewGuid():N}", null));
        await RequireMfaForAllUsersAsync(harness, organization.Id);
        var invitedEmail = $"invite-mfa-{Guid.NewGuid():N}@example.com";
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Invite MFA User",
            invitedEmail,
            "P@ssword123!"));
        var invitation = await harness.Invitation.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(
                organization.Id,
                invitedEmail,
                "member",
                SendEmail: false),
            CreateInvitationHttpContext());
        var authorizationRequest = await CreateHeadlessAuthorizationRequestAsync(
            harness,
            "state-invite-mfa",
            invitedEmail);

        var loginResult = await harness.Headless.PasswordLoginAsync(
            CreatePasswordHttpContext("203.0.113.212"),
            new SqlOSHeadlessPasswordLoginRequest(
                authorizationRequest.Id,
                invitedEmail,
                "P@ssword123!",
                ExtractInvitationToken(invitation.InviteUrl!)));

        loginResult.Type.Should().Be("view");
        loginResult.ViewModel.Should().NotBeNull();
        loginResult.ViewModel!.View.Should().Be("mfa-enroll");
        loginResult.ViewModel.MfaToken.Should().NotBeNullOrWhiteSpace();
        loginResult.ViewModel.RequiresMfaEnrollment.Should().BeTrue();
        loginResult.ViewModel.TotpEnrollment.Should().NotBeNull();
        (await harness.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == authorizationRequest.Id))
            .Should().Be(0);

        var storedInvitation = await harness.Context.Set<SqlOSInvitation>().SingleAsync(x => x.Id == invitation.Id);
        storedInvitation.AcceptedAt.Should().NotBeNull();
        storedInvitation.AcceptedByUserId.Should().Be(user.Id);
        var membership = await harness.Context.Set<SqlOSMembership>()
            .SingleAsync(x => x.UserId == user.Id && x.OrganizationId == organization.Id);
        membership.Role.Should().Be("member");
    }

    [TestMethod]
    public async Task HeadlessInvitationSignup_WithRequiredOrgMfa_ForcesEnrollmentBeforeRedirect()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            ConfigureHeadlessMfa(options);
            options.SeedAuthPage(page =>
            {
                page.EnabledCredentialTypes = ["password", "email_otp"];
                page.EnablePasswordSignup = true;
            });
        });
        var organization = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"Invite Signup MFA {Guid.NewGuid():N}", null));
        await RequireMfaForAllUsersAsync(harness, organization.Id);
        var invitedEmail = $"invite-signup-mfa-{Guid.NewGuid():N}@example.com";
        var invitation = await harness.Invitation.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(
                organization.Id,
                invitedEmail,
                "admin",
                SendEmail: false),
            CreateInvitationHttpContext());
        var authorizationRequest = await CreateHeadlessAuthorizationRequestAsync(
            harness,
            "state-invite-signup-mfa",
            invitedEmail);

        var signupResult = await harness.Headless.SignUpWithInvitationAsync(
            CreatePasswordHttpContext("203.0.113.213"),
            new SqlOSHeadlessInvitationSignupRequest(
                authorizationRequest.Id,
                "Invite Signup MFA",
                invitedEmail,
                new JsonObject(),
                ExtractInvitationToken(invitation.InviteUrl!)));

        signupResult.Type.Should().Be("view");
        signupResult.ViewModel.Should().NotBeNull();
        signupResult.ViewModel!.View.Should().Be("mfa-enroll");
        signupResult.ViewModel.MfaToken.Should().NotBeNullOrWhiteSpace();
        signupResult.ViewModel.RequiresMfaEnrollment.Should().BeTrue();
        signupResult.ViewModel.TotpEnrollment.Should().NotBeNull();
        (await harness.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == authorizationRequest.Id))
            .Should().Be(0);

        var storedInvitation = await harness.Context.Set<SqlOSInvitation>().SingleAsync(x => x.Id == invitation.Id);
        storedInvitation.AcceptedAt.Should().NotBeNull();
        var user = await harness.Context.Set<SqlOSUserEmail>().SingleAsync(x => x.Email == invitedEmail);
        var membership = await harness.Context.Set<SqlOSMembership>()
            .SingleAsync(x => x.UserId == user.UserId && x.OrganizationId == organization.Id);
        membership.Role.Should().Be("admin");
    }

    [TestMethod]
    public async Task LoginWithPassword_WhenOrganizationRequiresMfa_ForcesEnrollmentBeforeTokens()
    {
        var harness = await TestHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Required Mfa User",
            $"required-mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("MFA Required Org", null));
        await harness.Admin.CreateMembershipAsync(org.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        await harness.Settings.UpdateOrganizationMfaPolicyAsync(
            org.Id,
            new SqlOSUpdateOrganizationMfaPolicyRequest(
                IsEnabled: true,
                RequireMfaForAllUsers: true,
                RequireMfaForOwnersAndAdmins: false,
                UserSelfEnrollmentEnabled: true,
                RecoveryCodesEnabled: true,
                RequiredRoles: ["owner", "admin"],
                AvailableFactors: [SqlOSMfaFactorTypes.Totp, SqlOSMfaFactorTypes.RecoveryCode]));

        var login = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", org.Id),
            CreatePasswordHttpContext("203.0.113.201"));

        login.RequiresMfa.Should().BeTrue();
        login.RequiresMfaEnrollment.Should().BeTrue();
        login.MfaToken.Should().NotBeNullOrWhiteSpace();
        login.Tokens.Should().BeNull();

        var enrollment = await harness.Auth.StartTotpEnrollmentForChallengeAsync(
            login.MfaToken!,
            new SqlOSTotpEnrollmentStartRequest("Required Authenticator"));
        var code = harness.Totp.GenerateCodeForTesting(enrollment.Secret);
        var verified = await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, code, login.MfaToken),
            CreatePasswordHttpContext("203.0.113.201"));

        verified.Tokens.Should().NotBeNull();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(verified.Tokens!.AccessToken);
        jwt.Claims.Where(x => x.Type == "amr").Select(x => x.Value)
            .Should().BeEquivalentTo("password", "totp");
    }

    [TestMethod]
    public async Task MfaChallenge_ExistingAuthenticator_CannotEnrollReplacementAfterFirstFactor()
    {
        var harness = await TestHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Existing MFA User",
            $"existing-mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var enrollment = await harness.Auth.StartTotpEnrollmentAsync(
            user.Id,
            new SqlOSTotpEnrollmentStartRequest("Existing authenticator"));
        var recovery = await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(
                enrollment.EnrollmentToken,
                harness.Totp.GenerateCodeForTesting(enrollment.Secret)));
        var recoveryHashes = await harness.Context.Set<SqlOSRecoveryCode>()
            .Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .Select(x => x.CodeHash)
            .ToArrayAsync();

        var login = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext("203.0.113.220"));
        login.RequiresMfa.Should().BeTrue();
        login.RequiresMfaEnrollment.Should().BeFalse();
        var persistedChallenge = await harness.Crypto.FindTemporaryTokenAsync(
            SqlOSAuthService.MfaChallengePurpose,
            login.MfaToken!);
        var persistedPolicy = harness.Crypto.DeserializePayload<SqlOSMfaChallengePayload>(persistedChallenge!);
        persistedPolicy!.EnrollmentRequired.Should().BeFalse();
        persistedPolicy.PermittedEnrollmentFactors.Should().BeEmpty();

        var act = async () => await harness.Auth.StartTotpEnrollmentForChallengeAsync(
            login.MfaToken!,
            new SqlOSTotpEnrollmentStartRequest("Attacker authenticator"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("MFA enrollment is not authorized for this challenge.");
        (await harness.Context.Set<SqlOSUserAuthenticator>()
            .CountAsync(x => x.UserId == user.Id && x.RevokedAt == null)).Should().Be(1);
        (await harness.Context.Set<SqlOSRecoveryCode>()
            .Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .Select(x => x.CodeHash)
            .ToArrayAsync()).Should().BeEquivalentTo(recoveryHashes);
        recovery.RecoveryCodes.Should().HaveCount(recoveryHashes.Length);
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
        (await harness.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x =>
            x.EventType == "user.mfa.enrollment.challenge_rejected" && x.UserId == user.Id)).Should().Be(1);
    }

    [TestMethod]
    public async Task MfaChallenge_EnrollmentTokenForDifferentUser_IsRejectedWithoutIssuance()
    {
        var harness = await TestHarness.CreateAsync(configure: ConfigureRequiredMfa);
        var userA = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "MFA User A",
            $"mfa-user-a-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var userB = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "MFA User B",
            $"mfa-user-b-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var loginA = await LoginForRequiredMfaAsync(harness, userA, "test-client");
        var loginB = await LoginForRequiredMfaAsync(harness, userB, "test-client");
        var persistedChallenge = await harness.Crypto.FindTemporaryTokenAsync(
            SqlOSAuthService.MfaChallengePurpose,
            loginA.MfaToken!);
        var persistedPolicy = harness.Crypto.DeserializePayload<SqlOSMfaChallengePayload>(persistedChallenge!);
        persistedPolicy!.EnrollmentRequired.Should().BeTrue();
        persistedPolicy.PermittedEnrollmentFactors.Should().ContainSingle(SqlOSMfaFactorTypes.Totp);
        var enrollmentA = await harness.Auth.StartTotpEnrollmentForChallengeAsync(
            loginA.MfaToken!,
            new SqlOSTotpEnrollmentStartRequest("User A authenticator"));
        var codeA = harness.Totp.GenerateCodeForTesting(enrollmentA.Secret);

        var act = async () => await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(enrollmentA.EnrollmentToken, codeA, loginB.MfaToken));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("MFA enrollment is not authorized for this challenge.");
        (await harness.Context.Set<SqlOSUserAuthenticator>()
            .SingleAsync(x => x.Id == enrollmentA.AuthenticatorId)).IsConfirmed.Should().BeFalse();
        (await harness.Context.Set<SqlOSRecoveryCode>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSDeviceAuthorization>().CountAsync(x => x.ApprovedAt != null)).Should().Be(0);

        var correct = await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(enrollmentA.EnrollmentToken, codeA, loginA.MfaToken));
        correct.Tokens.Should().NotBeNull();
        new JwtSecurityTokenHandler().ReadJwtToken(correct.Tokens!.AccessToken).Subject.Should().Be(userA.Id);
    }

    [TestMethod]
    public async Task MfaChallenge_EnrollmentTokenForDifferentClient_IsRejected()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            ConfigureRequiredMfa(options);
            options.SeedBrowserClient("other-client", "Other Client", "https://other.example.test/callback");
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Client Bound MFA",
            $"client-bound-mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var testClientLogin = await LoginForRequiredMfaAsync(harness, user, "test-client");
        var otherClientLogin = await LoginForRequiredMfaAsync(harness, user, "other-client");
        var enrollment = await harness.Auth.StartTotpEnrollmentForChallengeAsync(
            testClientLogin.MfaToken!,
            new SqlOSTotpEnrollmentStartRequest());

        var act = async () => await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(
                enrollment.EnrollmentToken,
                harness.Totp.GenerateCodeForTesting(enrollment.Secret),
                otherClientLogin.MfaToken));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("MFA enrollment is not authorized for this challenge.");
        (await harness.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSRecoveryCode>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task MfaChallenge_EnrollmentTokenForDifferentOrganization_IsRejected()
    {
        var harness = await TestHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Organization Bound MFA",
            $"org-bound-mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var orgA = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("MFA Org A", null));
        var orgB = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("MFA Org B", null));
        await harness.Admin.CreateMembershipAsync(orgA.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        await harness.Admin.CreateMembershipAsync(orgB.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        await RequireMfaForAllUsersAsync(harness, orgA.Id);
        await RequireMfaForAllUsersAsync(harness, orgB.Id);
        var loginA = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", orgA.Id),
            CreatePasswordHttpContext("203.0.113.221"));
        var loginB = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", orgB.Id),
            CreatePasswordHttpContext("203.0.113.222"));
        var enrollment = await harness.Auth.StartTotpEnrollmentForChallengeAsync(
            loginA.MfaToken!,
            new SqlOSTotpEnrollmentStartRequest());

        var act = async () => await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(
                enrollment.EnrollmentToken,
                harness.Totp.GenerateCodeForTesting(enrollment.Secret),
                loginB.MfaToken));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("MFA enrollment is not authorized for this challenge.");
        (await harness.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSRecoveryCode>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task HeadlessMfaChallenge_DifferentAuthorizationRequest_IsRejectedThenOriginalCompletes()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            ConfigureRequiredMfa(options);
            options.UseHeadlessAuthPage(headless =>
            {
                headless.BuildUiUrl = ctx =>
                    $"https://app.example.test/authorize?request={Uri.EscapeDataString(ctx.RequestId ?? string.Empty)}&view={Uri.EscapeDataString(ctx.View)}";
            });
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Request Bound MFA",
            $"request-bound-mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var requestA = await CreateHeadlessAuthorizationRequestAsync(harness, "state-mfa-request-a", user.DefaultEmail!);
        var requestB = await CreateHeadlessAuthorizationRequestAsync(harness, "state-mfa-request-b", user.DefaultEmail!);
        var login = await harness.Headless.PasswordLoginAsync(
            CreatePasswordHttpContext("203.0.113.223"),
            new SqlOSHeadlessPasswordLoginRequest(requestA.Id, user.DefaultEmail!, "P@ssword123!"));
        var enrollment = login.ViewModel!.TotpEnrollment!;
        var code = harness.Totp.GenerateCodeForTesting(enrollment.Secret);

        var rejected = await harness.Headless.VerifyMfaTotpEnrollmentAsync(
            CreatePasswordHttpContext("203.0.113.223"),
            new SqlOSHeadlessMfaTotpEnrollmentVerifyRequest(
                requestB.Id,
                login.ViewModel.MfaToken!,
                enrollment.EnrollmentToken,
                code));

        rejected.Type.Should().Be("view");
        rejected.ViewModel!.Error.Should().Be("The request could not be completed.");
        (await harness.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSRecoveryCode>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSUserAuthenticator>()
            .SingleAsync(x => x.Id == enrollment.AuthenticatorId)).IsConfirmed.Should().BeFalse();

        var accepted = await harness.Headless.VerifyMfaTotpEnrollmentAsync(
            CreatePasswordHttpContext("203.0.113.223"),
            new SqlOSHeadlessMfaTotpEnrollmentVerifyRequest(
                requestA.Id,
                login.ViewModel.MfaToken!,
                enrollment.EnrollmentToken,
                code));
        accepted.Type.Should().Be("redirect");
        (await harness.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == requestA.Id)).Should().Be(1);
        (await harness.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == requestB.Id)).Should().Be(0);
    }

    [TestMethod]
    public async Task MfaChallenge_ChallengeBoundEnrollmentCannotUseAccountVerificationOrReplay()
    {
        var harness = await TestHarness.CreateAsync(configure: ConfigureRequiredMfa);
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Replay Bound MFA",
            $"replay-bound-mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var login = await LoginForRequiredMfaAsync(harness, user, "test-client");
        var enrollment = await harness.Auth.StartTotpEnrollmentForChallengeAsync(
            login.MfaToken!,
            new SqlOSTotpEnrollmentStartRequest());
        var code = harness.Totp.GenerateCodeForTesting(enrollment.Secret);

        var unbound = async () => await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, code));
        await unbound.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Challenge-bound enrollment must be verified with its original MFA challenge.");
        (await harness.Context.Set<SqlOSRecoveryCode>().CountAsync()).Should().Be(0);

        var first = await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, code, login.MfaToken));
        first.Tokens.Should().NotBeNull();
        var sessionCount = await harness.Context.Set<SqlOSSession>().CountAsync();
        var refreshCount = await harness.Context.Set<SqlOSRefreshToken>().CountAsync();
        var recoveryHashes = await harness.Context.Set<SqlOSRecoveryCode>()
            .Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .Select(x => x.CodeHash)
            .ToArrayAsync();

        var replay = async () => await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, code, login.MfaToken));
        await replay.Should().ThrowAsync<InvalidOperationException>();
        (await harness.Context.Set<SqlOSSession>().CountAsync()).Should().Be(sessionCount);
        (await harness.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(refreshCount);
        (await harness.Context.Set<SqlOSRecoveryCode>()
            .Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .Select(x => x.CodeHash)
            .ToArrayAsync()).Should().BeEquivalentTo(recoveryHashes);
    }

    [TestMethod]
    public async Task MfaChallenge_EnrollmentRequired_CannotUseFactorAddedThroughAccountEnrollment()
    {
        var harness = await TestHarness.CreateAsync(configure: ConfigureRequiredMfa);
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Alternate Path MFA",
            $"alternate-path-mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var login = await LoginForRequiredMfaAsync(harness, user, "test-client");

        var accountEnrollment = await harness.Auth.StartTotpEnrollmentAsync(
            user.Id,
            new SqlOSTotpEnrollmentStartRequest("Account settings authenticator"));
        var accountResult = await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(
                accountEnrollment.EnrollmentToken,
                harness.Totp.GenerateCodeForTesting(accountEnrollment.Secret)));
        var authenticator = await harness.Context.Set<SqlOSUserAuthenticator>()
            .SingleAsync(x => x.Id == accountEnrollment.AuthenticatorId);
        var acceptedStep = authenticator.LastAcceptedTimeStep;
        var recoveryHashes = await harness.Context.Set<SqlOSRecoveryCode>()
            .Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .Select(x => x.CodeHash)
            .ToArrayAsync();
        var nextCode = harness.Totp.GenerateCodeForTesting(
            accountEnrollment.Secret,
            DateTimeOffset.UtcNow.AddSeconds(harness.Options.Mfa.Totp.PeriodSeconds));

        var act = async () => await harness.Auth.VerifyMfaChallengeAsync(
            new SqlOSMfaChallengeVerifyRequest(login.MfaToken!, nextCode),
            CreatePasswordHttpContext("203.0.113.225"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("MFA enrollment must be completed with its challenge-bound enrollment proof.");
        var recoveryAct = async () => await harness.Auth.VerifyMfaChallengeAsync(
            new SqlOSMfaChallengeVerifyRequest(login.MfaToken!, accountResult.RecoveryCodes.First()),
            CreatePasswordHttpContext("203.0.113.225"));
        await recoveryAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("MFA enrollment must be completed with its challenge-bound enrollment proof.");
        authenticator.LastAcceptedTimeStep.Should().Be(acceptedStep);
        (await harness.Context.Set<SqlOSRecoveryCode>()
            .Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .Select(x => x.CodeHash)
            .ToArrayAsync()).Should().BeEquivalentTo(recoveryHashes);
        (await harness.Context.Set<SqlOSRecoveryCode>()
            .CountAsync(x => x.UserId == user.Id && x.ConsumedAt != null)).Should().Be(0);
        accountResult.RecoveryCodes.Should().HaveCount(recoveryHashes.Length);
        (await harness.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
        var challenge = await harness.Crypto.FindTemporaryTokenAsync(
            SqlOSAuthService.MfaChallengePurpose,
            login.MfaToken!);
        challenge.Should().NotBeNull();
        challenge!.ConsumedAt.Should().BeNull();
    }

    [TestMethod]
    public async Task HostedMfaChallenge_EnrollmentRequired_CannotUseConcurrentAccountFactor()
    {
        var harness = await TestHarness.CreateAsync(configure: ConfigureRequiredMfa);
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Hosted Alternate Path MFA",
            $"hosted-alternate-mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var authorizationRequest = await CreateHeadlessAuthorizationRequestAsync(
            harness,
            "hosted-alternate-mfa",
            user.DefaultEmail!);
        var authentication = await harness.Authorization.AuthenticatePasswordAsync(
            user.DefaultEmail!,
            "P@ssword123!",
            httpContext: CreatePasswordHttpContext("203.0.113.226"),
            clientKey: "test-client",
            authorizationRequestId: authorizationRequest.Id,
            surface: "hosted");
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            authorizationRequest,
            authentication.User,
            authentication.AuthenticationMethod,
            CreatePasswordHttpContext("203.0.113.226"));
        completion.RequiresMfaEnrollment.Should().BeTrue();

        var accountEnrollment = await harness.Auth.StartTotpEnrollmentAsync(
            user.Id,
            new SqlOSTotpEnrollmentStartRequest("Concurrent account authenticator"));
        await harness.Auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
            accountEnrollment.EnrollmentToken,
            harness.Totp.GenerateCodeForTesting(accountEnrollment.Secret)));
        var authenticator = await harness.Context.Set<SqlOSUserAuthenticator>()
            .SingleAsync(x => x.Id == accountEnrollment.AuthenticatorId);
        var acceptedStep = authenticator.LastAcceptedTimeStep;
        var nextCode = harness.Totp.GenerateCodeForTesting(
            accountEnrollment.Secret,
            DateTimeOffset.UtcNow.AddSeconds(harness.Options.Mfa.Totp.PeriodSeconds));

        var act = async () => await harness.Authorization.CompleteMfaChallengeAsync(
            completion.MfaToken!,
            nextCode,
            CreatePasswordHttpContext("203.0.113.226"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("MFA enrollment must be completed with its challenge-bound enrollment proof.");
        authenticator.LastAcceptedTimeStep.Should().Be(acceptedStep);
        (await harness.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == authorizationRequest.Id)).Should().Be(0);
        var challenge = await harness.Crypto.FindTemporaryTokenAsync(
            SqlOSAuthService.MfaChallengePurpose,
            completion.MfaToken!);
        challenge.Should().NotBeNull();
        challenge!.ConsumedAt.Should().BeNull();
    }

    [TestMethod]
    public async Task MfaChallenge_WrongCodesConsumeChallengeAtConfiguredCap()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            ConfigureRequiredMfa(options);
            options.Mfa.Totp.MaxFailedAttemptsPerChallenge = 3;
            options.Mfa.Totp.MaxFailedAttemptsPerUser = 6;
            options.Mfa.Totp.MaxFailedAttemptsPerIp = 10;
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Bounded MFA",
            $"bounded-mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var enrollment = await harness.Auth.StartTotpEnrollmentAsync(
            user.Id,
            new SqlOSTotpEnrollmentStartRequest("Bounded authenticator"));
        await harness.Auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
            enrollment.EnrollmentToken,
            harness.Totp.GenerateCodeForTesting(enrollment.Secret)));
        var login = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext("203.0.113.230"));
        login.RequiresMfa.Should().BeTrue();
        login.RequiresMfaEnrollment.Should().BeFalse();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var reject = async () => await harness.Auth.VerifyMfaChallengeAsync(
                new SqlOSMfaChallengeVerifyRequest(login.MfaToken!, "not-a-valid-code"),
                CreatePasswordHttpContext("203.0.113.230"));
            await reject.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(SqlOSAuthService.MfaChallengeFailureMessage);
        }

        var persisted = await harness.Context.Set<SqlOSTemporaryToken>()
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(login.MfaToken!));
        persisted.ConsumedAt.Should().NotBeNull();
        harness.Crypto.DeserializePayload<SqlOSMfaChallengePayload>(persisted)!.FailedAttempts.Should().Be(3);
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x =>
            x.Action == "user.mfa.challenge_failed" && x.UserId == user.Id)).Should().Be(3);

        var correctAfterCap = async () => await harness.Auth.VerifyMfaChallengeAsync(
            new SqlOSMfaChallengeVerifyRequest(
                login.MfaToken!,
                harness.Totp.GenerateCodeForTesting(
                    enrollment.Secret,
                    DateTimeOffset.UtcNow.AddSeconds(harness.Options.Mfa.Totp.PeriodSeconds))),
            CreatePasswordHttpContext("203.0.113.230"));
        await correctAfterCap.Should().ThrowAsync<InvalidOperationException>();
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
    }

    [TestMethod]
    public async Task MfaChallenge_ReissuingChallengesCannotBypassUserThrottle()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            ConfigureRequiredMfa(options);
            options.Mfa.Totp.MaxFailedAttemptsPerChallenge = 2;
            options.Mfa.Totp.MaxFailedAttemptsPerUser = 3;
            options.Mfa.Totp.MaxFailedAttemptsPerIp = 20;
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Throttled MFA",
            $"throttled-mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var enrollment = await harness.Auth.StartTotpEnrollmentAsync(
            user.Id,
            new SqlOSTotpEnrollmentStartRequest("Throttled authenticator"));
        await harness.Auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
            enrollment.EnrollmentToken,
            harness.Totp.GenerateCodeForTesting(enrollment.Secret)));

        var firstLogin = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext("203.0.113.231"));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reject = async () => await harness.Auth.VerifyMfaChallengeAsync(
                new SqlOSMfaChallengeVerifyRequest(firstLogin.MfaToken!, "wrong"),
                CreatePasswordHttpContext("203.0.113.231"));
            await reject.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(SqlOSAuthService.MfaChallengeFailureMessage);
        }

        var secondLogin = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext("203.0.113.232"));
        var finalFailure = async () => await harness.Auth.VerifyMfaChallengeAsync(
            new SqlOSMfaChallengeVerifyRequest(secondLogin.MfaToken!, "wrong"),
            CreatePasswordHttpContext("203.0.113.232"));
        await finalFailure.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSAuthService.MfaChallengeFailureMessage);

        var reissue = async () => await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext("203.0.113.233"));
        await reissue.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSAuthService.MfaChallengeFailureMessage);
        (await harness.Context.Set<SqlOSTemporaryToken>().CountAsync(x =>
            x.Purpose == SqlOSAuthService.MfaChallengePurpose && x.UserId == user.Id)).Should().Be(2);
    }

    [TestMethod]
    public async Task MfaChallenge_IpThrottleSpansUsersWithoutBlockingAnotherIp()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            ConfigureRequiredMfa(options);
            options.Mfa.Totp.MaxFailedAttemptsPerChallenge = 2;
            options.Mfa.Totp.MaxFailedAttemptsPerUser = 10;
            options.Mfa.Totp.MaxFailedAttemptsPerIp = 2;
        });
        var first = await CreateEnrolledMfaUserAsync(harness, "IP throttle first");
        var second = await CreateEnrolledMfaUserAsync(harness, "IP throttle second");
        var blocked = await CreateEnrolledMfaUserAsync(harness, "IP throttle blocked");
        var control = await CreateEnrolledMfaUserAsync(harness, "IP throttle control");
        var sharedIp = "203.0.113.240";

        var firstLogin = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(first.User.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext(sharedIp));
        var secondLogin = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(second.User.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext(sharedIp));
        var blockedLogin = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(blocked.User.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext(sharedIp));
        var controlLogin = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(control.User.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext("203.0.113.241"));

        foreach (var mfaToken in new[] { firstLogin.MfaToken!, secondLogin.MfaToken! })
        {
            var reject = async () => await harness.Auth.VerifyMfaChallengeAsync(
                new SqlOSMfaChallengeVerifyRequest(mfaToken, "wrong"),
                CreatePasswordHttpContext(sharedIp));
            await reject.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(SqlOSAuthService.MfaChallengeFailureMessage);
        }

        var blockedCorrect = async () => await harness.Auth.VerifyMfaChallengeAsync(
            new SqlOSMfaChallengeVerifyRequest(
                blockedLogin.MfaToken!,
                harness.Totp.GenerateCodeForTesting(
                    blocked.Secret,
                    DateTimeOffset.UtcNow.AddSeconds(harness.Options.Mfa.Totp.PeriodSeconds))),
            CreatePasswordHttpContext(sharedIp));
        await blockedCorrect.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSAuthService.MfaChallengeFailureMessage);

        var controlResult = await harness.Auth.VerifyMfaChallengeAsync(
            new SqlOSMfaChallengeVerifyRequest(
                controlLogin.MfaToken!,
                harness.Totp.GenerateCodeForTesting(
                    control.Secret,
                    DateTimeOffset.UtcNow.AddSeconds(harness.Options.Mfa.Totp.PeriodSeconds))),
            CreatePasswordHttpContext("203.0.113.241"));
        controlResult.Tokens.Should().NotBeNull();

        var blockedChallenge = await harness.Context.Set<SqlOSTemporaryToken>()
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(blockedLogin.MfaToken!));
        blockedChallenge.ConsumedAt.Should().BeNull();
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x =>
            x.Action == "user.mfa.challenge_failed" && x.IpAddress == sharedIp)).Should().Be(2);
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == blocked.User.Id)).Should().Be(0);
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == control.User.Id)).Should().Be(1);
    }

    [TestMethod]
    public async Task RecoveryCode_CanSatisfyMfaOnlyOnce()
    {
        var harness = await TestHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Recovery User",
            $"recovery-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Recovery MFA Org", null));
        await harness.Admin.CreateMembershipAsync(org.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        await harness.Settings.UpdateOrganizationMfaPolicyAsync(
            org.Id,
            new SqlOSUpdateOrganizationMfaPolicyRequest(
                IsEnabled: true,
                RequireMfaForAllUsers: true,
                RequireMfaForOwnersAndAdmins: false,
                UserSelfEnrollmentEnabled: true,
                RecoveryCodesEnabled: true,
                RequiredRoles: ["owner", "admin"],
                AvailableFactors: [SqlOSMfaFactorTypes.Totp, SqlOSMfaFactorTypes.RecoveryCode]));

        var enrollment = await harness.Auth.StartTotpEnrollmentAsync(
            user.Id,
            new SqlOSTotpEnrollmentStartRequest("Recovery Authenticator"),
            org.Id);
        var enrollmentCode = harness.Totp.GenerateCodeForTesting(enrollment.Secret);
        var enrollmentResult = await harness.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, enrollmentCode),
            CreatePasswordHttpContext("203.0.113.202"));
        var recoveryCode = enrollmentResult.RecoveryCodes.First();

        var firstLogin = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", org.Id),
            CreatePasswordHttpContext("203.0.113.202"));
        var firstVerify = await harness.Auth.VerifyMfaChallengeAsync(
            new SqlOSMfaChallengeVerifyRequest(firstLogin.MfaToken!, recoveryCode),
            CreatePasswordHttpContext("203.0.113.202"));
        firstVerify.Tokens.Should().NotBeNull();

        var secondLogin = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", org.Id),
            CreatePasswordHttpContext("203.0.113.202"));
        var act = async () => await harness.Auth.VerifyMfaChallengeAsync(
            new SqlOSMfaChallengeVerifyRequest(secondLogin.MfaToken!, recoveryCode),
            CreatePasswordHttpContext("203.0.113.202"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("MFA code is invalid.");
    }

    [TestMethod]
    public async Task LoginWithPasswordAsync_RepeatedFailures_LocksAccountOrBacksOff()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 2;
            options.PasswordLogin.LockoutDuration = TimeSpan.FromMinutes(10);
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Lockout User",
            $"lockout-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var act = async () => await harness.Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(user.DefaultEmail!, "wrong-password", "test-client", null),
                CreatePasswordHttpContext("203.0.113.10"));

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        var lockedAct = async () => await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext("203.0.113.10"));

        await lockedAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);

        var emailBucket = await harness.Context.Set<SqlOSPasswordLoginBucket>()
            .SingleAsync(x => x.Scope == "email" && x.BucketKey == SqlOSAdminService.NormalizeEmail(user.DefaultEmail!));
        emailBucket.LockedUntil.Should().BeAfter(DateTime.UtcNow);
    }

    [TestMethod]
    public async Task AuthenticatePasswordAsync_UnknownEmailAndWrongPassword_ReturnUniformPublicFailure()
    {
        var harness = await TestHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Uniform User",
            $"uniform-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));

        var unknownAct = async () => await harness.Authorization.AuthenticatePasswordAsync(
            $"unknown-{Guid.NewGuid():N}@example.com",
            "anything",
            cancellationToken: default,
            httpContext: CreatePasswordHttpContext("203.0.113.20"),
            clientKey: "test-client",
            surface: "hosted");
        var wrongAct = async () => await harness.Authorization.AuthenticatePasswordAsync(
            user.DefaultEmail!,
            "wrong-password",
            cancellationToken: default,
            httpContext: CreatePasswordHttpContext("203.0.113.21"),
            clientKey: "test-client",
            surface: "hosted");

        var unknownFailure = await unknownAct.Should().ThrowAsync<InvalidOperationException>();
        var wrongFailure = await wrongAct.Should().ThrowAsync<InvalidOperationException>();

        unknownFailure.Which.Message.Should().Be(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        wrongFailure.Which.Message.Should().Be(unknownFailure.Which.Message);
    }

    [TestMethod]
    public async Task PasswordLogin_PerIpLimit_BlocksPasswordSprayAcrossAccounts()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 10;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 2;
            options.PasswordLogin.LockoutDuration = TimeSpan.FromMinutes(10);
        });
        var users = new[]
        {
            await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Spray One", $"spray-1-{Guid.NewGuid():N}@example.com", "P@ssword123!")),
            await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Spray Two", $"spray-2-{Guid.NewGuid():N}@example.com", "P@ssword123!")),
            await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Spray Three", $"spray-3-{Guid.NewGuid():N}@example.com", "P@ssword123!"))
        };

        foreach (var user in users.Take(2))
        {
            var fail = async () => await harness.Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(user.DefaultEmail!, "wrong-password", "test-client", null),
                CreatePasswordHttpContext("203.0.113.30"));

            await fail.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        var blocked = async () => await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(users[2].DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext("203.0.113.30"));

        await blocked.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);

        var ipBucket = await harness.Context.Set<SqlOSPasswordLoginBucket>()
            .SingleAsync(x => x.Scope == "ip" && x.BucketKey == "203.0.113.30");
        ipBucket.LockedUntil.Should().BeAfter(DateTime.UtcNow);
    }

    [TestMethod]
    public async Task HostedPasswordLogin_UsesSameThrottleStateAsApiLogin()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 1;
            options.PasswordLogin.LockoutDuration = TimeSpan.FromMinutes(10);
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Hosted Shared",
            $"hosted-shared-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));

        var apiFailure = async () => await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "wrong-password", "test-client", null),
            CreatePasswordHttpContext("203.0.113.40"));
        await apiFailure.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);

        var hostedSuccessBypass = async () => await harness.Authorization.AuthenticatePasswordAsync(
            user.DefaultEmail!,
            "P@ssword123!",
            cancellationToken: default,
            httpContext: CreatePasswordHttpContext("203.0.113.40"),
            clientKey: "test-client",
            surface: "hosted");

        await hostedSuccessBypass.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
    }

    [TestMethod]
    public async Task PasswordLogin_SuccessAfterFailures_RecordsSuccessAndResetsOrDecaysCounters()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 3;
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Reset User",
            $"reset-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));

        var failure = async () => await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "wrong-password", "test-client", null),
            CreatePasswordHttpContext("203.0.113.50"));
        await failure.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);

        var success = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext("203.0.113.50"));
        success.Tokens.Should().NotBeNull();

        var emailBucket = await harness.Context.Set<SqlOSPasswordLoginBucket>()
            .SingleAsync(x => x.Scope == "email" && x.BucketKey == SqlOSAdminService.NormalizeEmail(user.DefaultEmail!));
        emailBucket.FailureCount.Should().Be(0);
        emailBucket.LockedUntil.Should().BeNull();
        emailBucket.LastSuccessAt.Should().NotBeNull();

        (await harness.Context.Set<SqlOSAuditEvent>()
            .AnyAsync(x => x.EventType == "password.login.succeeded" && x.UserId == user.Id)).Should().BeTrue();
    }

    [TestMethod]
    public async Task PasswordLogin_LockoutAndFailure_WriteAuditEvents()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 1;
            options.PasswordLogin.LockoutDuration = TimeSpan.FromMinutes(10);
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Audit User",
            $"audit-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));

        var failure = async () => await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "wrong-password", "test-client", null),
            CreatePasswordHttpContext("203.0.113.60"));
        await failure.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);

        var rejected = async () => await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreatePasswordHttpContext("203.0.113.60"));
        await rejected.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);

        var eventTypes = await harness.Context.Set<SqlOSAuditEvent>()
            .Select(x => x.EventType)
            .ToListAsync();
        eventTypes.Should().Contain("password.login.failed");
        eventTypes.Should().Contain("password.login.locked");
        eventTypes.Should().Contain("password.login.rate_limit_rejected");
    }

    [TestMethod]
    public async Task SignUpAsync_WithExistingOrganizationId_WithoutInvitation_DoesNotCreateMembership()
    {
        var harness = await TestHarness.CreateAsync();
        var existingOrganization = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"Existing {Guid.NewGuid():N}", null));
        var email = $"attacker-{Guid.NewGuid():N}@example.com";

        var act = async () => await harness.Auth.SignUpAsync(
            new SqlOSSignupRequest(
                "Mallory",
                email,
                "P@ssword123!",
                OrganizationName: null,
                ClientId: "test-client",
                OrganizationId: existingOrganization.Id),
            new DefaultHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(UnauthorizedOrganizationJoinMessage);

        (await harness.Context.Set<SqlOSMembership>()
            .CountAsync(x => x.OrganizationId == existingOrganization.Id)).Should().Be(0);
        (await harness.Context.Set<SqlOSUserEmail>()
            .CountAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(email))).Should().Be(0);
    }

    [TestMethod]
    public async Task SignUpAsync_WithUnknownClient_DoesNotCreateUser()
    {
        var harness = await TestHarness.CreateAsync();
        var email = $"unknown-client-{Guid.NewGuid():N}@example.com";

        var act = async () => await harness.Auth.SignUpAsync(
            new SqlOSSignupRequest(
                "Unknown Client",
                email,
                "P@ssword123!",
                $"Org {Guid.NewGuid():N}",
                ClientId: "missing-client",
                OrganizationId: null),
            new DefaultHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unknown client 'missing-client'.");

        (await harness.Context.Set<SqlOSUserEmail>()
            .CountAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(email))).Should().Be(0);
        (await harness.Context.Set<SqlOSOrganization>()
            .CountAsync(x => x.Name.StartsWith("Org "))).Should().Be(0);
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "user.signup")).Should().Be(0);
    }

    [TestMethod]
    public async Task SignUpAsync_WithEmptyPassword_DoesNotCreateUser()
    {
        var harness = await TestHarness.CreateAsync();
        var email = $"empty-password-{Guid.NewGuid():N}@example.com";

        var act = async () => await harness.Auth.SignUpAsync(
            new SqlOSSignupRequest(
                "No Password",
                email,
                "   ",
                OrganizationName: null,
                ClientId: "test-client",
                OrganizationId: null),
            new DefaultHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSSignupOrchestration.PasswordRequiredMessage);

        (await harness.Context.Set<SqlOSUserEmail>()
            .CountAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(email))).Should().Be(0);
    }

    [TestMethod]
    public async Task PublicSignup_UnknownOrgAndExistingOrg_ReturnUniformPublicFailure()
    {
        var harness = await TestHarness.CreateAsync();
        var existingOrganization = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"Uniform {Guid.NewGuid():N}", null));

        var existingAct = async () => await harness.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Existing Org Probe",
                    $"existing-probe-{Guid.NewGuid():N}@example.com",
                    "P@ssword123!",
                    OrganizationName: null,
                    ClientId: "test-client",
                    OrganizationId: existingOrganization.Id),
                new DefaultHttpContext());

        var unknownAct = async () => await harness.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Unknown Org Probe",
                    $"unknown-probe-{Guid.NewGuid():N}@example.com",
                    "P@ssword123!",
                    OrganizationName: null,
                    ClientId: "test-client",
                    OrganizationId: $"org_{Guid.NewGuid():N}"),
                new DefaultHttpContext());

        var existingFailure = await existingAct.Should().ThrowAsync<InvalidOperationException>();
        var unknownFailure = await unknownAct.Should().ThrowAsync<InvalidOperationException>();

        existingFailure.Which.Message.Should().Be(UnauthorizedOrganizationJoinMessage);
        unknownFailure.Which.Message.Should().Be(existingFailure.Which.Message);
        existingFailure.Which.Message.Should().NotContain(existingOrganization.Id);
    }

    [TestMethod]
    public async Task PasswordResetEmail_Request_KnownUser_SendsBrandedEmail()
    {
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.SeedAuthEmails(email =>
            {
                email.ApplicationName = "Reset App";
                email.PrimaryColor = "#0D9488";
                email.AccentColor = "#1A1A1A";
                email.BackgroundColor = "#FAFAF8";
            });
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Reset User",
            "reset-user@example.com",
            "OldPassword123!"));

        var result = await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(user.DefaultEmail!, ClientId: "test-client"),
            CreatePasswordHttpContext("203.0.113.90"));

        result.Message.Should().Be("If an account can be reset, you'll receive a password reset email shortly.");
        harness.EmailSender.Messages.Should().ContainSingle();
        var message = harness.EmailSender.Messages.Single();
        message.To.Should().Be(user.DefaultEmail);
        message.Subject.Should().Be("Reset your Reset App password");
        message.TextBody.Should().Contain("/sqlos/auth/password/reset?token=");
        message.HtmlBody.Should().Contain("#0D9488");
    }

    [TestMethod]
    public async Task PasswordResetEmail_PublicRequest_UsesTrustedOriginInsteadOfRequestHeaders()
    {
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.PublicOrigin = "https://auth.example.test";
            options.Issuer = "https://auth.example.test/sqlos/auth";
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Header Attack Reset",
            "header-attack-reset@example.com",
            "OldPassword123!"));
        var context = CreatePasswordHttpContext("203.0.113.100");
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("attacker.example");
        context.Request.Headers["Forwarded"] = "host=forwarded-attacker.example;proto=https";
        context.Request.Headers["X-Forwarded-Host"] = "forwarded-attacker.example";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(user.DefaultEmail!, "test-client"),
            context);

        var body = harness.EmailSender.Messages.Should().ContainSingle().Subject.TextBody;
        body.Should().Contain("https://auth.example.test/sqlos/auth/password/reset?token=");
        body.Should().NotContain("attacker.example");
    }

    [TestMethod]
    public async Task PasswordResetEmail_ServerBuilder_ReceivesOnlyResolvedFirstPartyClient()
    {
        var observedClientIds = new List<string?>();
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.SeedClient(client =>
            {
                client.ClientId = "external-client";
                client.Name = "External Client";
                client.IsFirstParty = false;
                client.RedirectUris = ["https://external.example/callback"];
            });
            options.PasswordReset.BuildResetUrl = context =>
            {
                observedClientIds.Add(context.ClientId);
                var origin = context.ClientId == "test-client"
                    ? "https://first-party.example"
                    : "https://auth.example.test";
                return $"{origin}/reset?token={Uri.EscapeDataString(context.Token)}";
            };
        });
        var firstPartyUser = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "First Party Reset",
            "first-party-reset@example.com",
            "OldPassword123!"));
        var externalUser = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "External Reset",
            "external-reset@example.com",
            "OldPassword123!"));

        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(firstPartyUser.DefaultEmail!, "test-client"));
        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(externalUser.DefaultEmail!, "external-client"));

        observedClientIds.Should().Equal("test-client", null);
        harness.EmailSender.Messages[0].TextBody.Should().Contain("https://first-party.example/reset?token=");
        harness.EmailSender.Messages[1].TextBody.Should().Contain("https://auth.example.test/reset?token=");
    }

    [DataTestMethod]
    [DataRow("//attacker.example/reset")]
    [DataRow("javascript:alert(1)")]
    [DataRow("https://trusted.example@attacker.example/reset")]
    [DataRow("https://trusted.example\\@attacker.example/reset")]
    [DataRow("http://attacker.example/reset")]
    [DataRow("https:%2f%2fattacker.example/reset")]
    [DataRow("https://trusted.example%2f@attacker.example/reset")]
    [DataRow("https://trusted.example/reset\r\nBcc: victim@example.com")]
    public async Task PasswordResetEmail_UnsafeConfiguredUrl_FailsClosedAndInvalidatesToken(string unsafeUrl)
    {
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.PasswordReset.BuildResetUrl = _ => unsafeUrl;
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Unsafe URL Reset",
            $"unsafe-url-{Guid.NewGuid():N}@example.com",
            "OldPassword123!"));

        var act = async () => await harness.Auth.SendPasswordResetEmailAsync(
            new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*configured password reset URL must be an absolute HTTPS URL (or loopback HTTP URL) without user information*");
        harness.EmailSender.Messages.Should().BeEmpty();
        (await harness.Context.Set<SqlOSTemporaryToken>()
                .CountAsync(token => token.UserId == user.Id && token.Purpose == "password_reset" && token.ConsumedAt == null))
            .Should().Be(0);
        (await harness.Context.Set<SqlOSAuditEvent>()
                .CountAsync(audit => audit.EventType == "password_reset.email_send_failed" && audit.UserId == user.Id))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task PasswordResetEmail_LoopbackHttpConfiguredUrl_IsAllowedForDevelopment()
    {
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.PasswordReset.BuildResetUrl = context =>
                $"http://localhost:3000/reset?token={Uri.EscapeDataString(context.Token)}";
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Loopback Reset",
            "loopback-reset@example.com",
            "OldPassword123!"));

        await harness.Auth.SendPasswordResetEmailAsync(
            new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!));

        harness.EmailSender.Messages.Should().ContainSingle()
            .Which.TextBody.Should().Contain("http://localhost:3000/reset?token=");
    }

    [TestMethod]
    public async Task PasswordResetEmail_TokenInConfiguredAuthority_FailsClosed()
    {
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.PasswordReset.BuildResetUrl = context => $"https://{context.Token}.attacker.example/reset";
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Authority Token Reset",
            "authority-token-reset@example.com",
            "OldPassword123!"));

        var act = async () => await harness.Auth.SendPasswordResetEmailAsync(
            new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The password reset token cannot appear in the URL authority.");
        harness.EmailSender.Messages.Should().BeEmpty();
        (await harness.Context.Set<SqlOSTemporaryToken>()
                .CountAsync(token => token.UserId == user.Id && token.Purpose == "password_reset" && token.ConsumedAt == null))
            .Should().Be(0);
    }

    [TestMethod]
    public async Task PasswordResetEmail_TrustedTemplate_AppendsTokenBeforeFragment()
    {
        using var harness = await PasswordResetHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Fragment Reset",
            "fragment-reset@example.com",
            "OldPassword123!"));

        await harness.Auth.SendPasswordResetEmailAsync(
            new SqlOSSendPasswordResetEmailRequest(
                user.DefaultEmail!,
                "https://app.example/reset?view=password#form"));

        var body = harness.EmailSender.Messages.Should().ContainSingle().Subject.TextBody;
        body.Should().MatchRegex(@"https://app\.example/reset\?view=password&amp;token=[A-Za-z0-9_-]+#form|https://app\.example/reset\?view=password&token=[A-Za-z0-9_-]+#form");
    }

    [TestMethod]
    public async Task PasswordResetEmail_LinkGenerationFailure_ReturnsGenericResultAndInvalidatesToken()
    {
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.PasswordReset.BuildResetUrl = _ => throw new InvalidOperationException("private link failure");
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Builder Failure Reset",
            "builder-failure-reset@example.com",
            "OldPassword123!"));

        var result = await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(user.DefaultEmail!),
            CreatePasswordHttpContext("203.0.113.101"));

        result.Message.Should().Be("If an account can be reset, you'll receive a password reset email shortly.");
        harness.EmailSender.Messages.Should().BeEmpty();
        (await harness.Context.Set<SqlOSTemporaryToken>()
                .CountAsync(token => token.UserId == user.Id && token.Purpose == "password_reset" && token.ConsumedAt == null))
            .Should().Be(0);
        (await harness.Context.Set<SqlOSAuditEvent>()
                .CountAsync(audit => audit.EventType == "password_reset.email_send_failed" && audit.UserId == user.Id))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task PasswordResetEmail_RequestCancellationAfterTokenCreation_InvalidatesToken()
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.PasswordReset.BuildResetUrl = _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            };
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Cancelled Reset",
            "cancelled-reset@example.com",
            "OldPassword123!"));

        var act = async () => await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(user.DefaultEmail!),
            CreatePasswordHttpContext("203.0.113.102"),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.EmailSender.Messages.Should().BeEmpty();
        (await harness.Context.Set<SqlOSTemporaryToken>()
                .CountAsync(token => token.UserId == user.Id && token.Purpose == "password_reset" && token.ConsumedAt == null))
            .Should().Be(0);
        (await harness.Context.Set<SqlOSAuditEvent>()
                .CountAsync(audit => audit.EventType == "password_reset.email_send_failed" && audit.UserId == user.Id))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task PasswordResetEmail_Request_UnknownEmail_ReturnsGenericSuccessAndDoesNotSend()
    {
        using var harness = await PasswordResetHarness.CreateAsync();

        var result = await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest("missing@example.com"),
            CreatePasswordHttpContext("203.0.113.91"));

        result.Message.Should().Be("If an account can be reset, you'll receive a password reset email shortly.");
        result.MaskedEmail.Should().Be("mi***@example.com");
        harness.EmailSender.Messages.Should().BeEmpty();
        (await harness.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "password_reset")).Should().Be(0);
    }

    [TestMethod]
    public async Task PasswordResetEmail_Request_InactiveUser_ReturnsGenericSuccessAndDoesNotSend()
    {
        using var harness = await PasswordResetHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Inactive Reset",
            "inactive-reset@example.com",
            "OldPassword123!"));
        user.IsActive = false;
        await harness.Context.SaveChangesAsync();

        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(user.DefaultEmail!),
            CreatePasswordHttpContext("203.0.113.92"));

        harness.EmailSender.Messages.Should().BeEmpty();
        (await harness.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "password_reset")).Should().Be(0);
    }

    [TestMethod]
    public async Task PasswordResetEmail_Request_LocalPasswordDisabled_DoesNotSendOrCreatePassword()
    {
        using var harness = await PasswordResetHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Disabled Reset",
            "disabled-reset@example.com",
            "OldPassword123!"));
        var token = await harness.Auth.CreatePasswordResetTokenAsync(new SqlOSForgotPasswordRequest(user.DefaultEmail!));

        harness.Options.EnableLocalPasswordAuth = false;
        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(user.DefaultEmail!),
            CreatePasswordHttpContext("203.0.113.93"));
        var resetAct = async () => await harness.Auth.ResetPasswordAsync(new SqlOSResetPasswordRequest(token, "NewPassword123!"));

        harness.EmailSender.Messages.Should().BeEmpty();
        await resetAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Local password authentication is disabled.");
        var credential = await harness.Context.Set<SqlOSCredential>().SingleAsync(x => x.UserId == user.Id && x.Type == "password");
        harness.Crypto.VerifyPassword(credential.SecretHash, "OldPassword123!").Should().BeTrue();
    }

    [TestMethod]
    public async Task PasswordResetEmail_Request_RateLimitsByEmail()
    {
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.PasswordReset.MaxRequestsPerEmailPerWindow = 1;
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Limited Reset",
            "limited-reset@example.com",
            "OldPassword123!"));

        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(user.DefaultEmail!),
            CreatePasswordHttpContext("203.0.113.94"));
        var second = await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(user.DefaultEmail!),
            CreatePasswordHttpContext("203.0.113.94"));

        second.Message.Should().Be("If an account can be reset, you'll receive a password reset email shortly.");
        harness.EmailSender.Messages.Should().ContainSingle();
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "password_reset.rate_limit_rejected"))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task PasswordResetEmail_Request_RateLimitsByIp()
    {
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.PasswordReset.MaxRequestsPerIpPerWindow = 1;
        });
        var first = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "IP Limited One",
            "ip-limited-one@example.com",
            "OldPassword123!"));
        var second = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "IP Limited Two",
            "ip-limited-two@example.com",
            "OldPassword123!"));

        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(first.DefaultEmail!),
            CreatePasswordHttpContext("203.0.113.96"));
        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(second.DefaultEmail!),
            CreatePasswordHttpContext("203.0.113.96"));

        harness.EmailSender.Messages.Should().ContainSingle();
        harness.EmailSender.Messages.Single().To.Should().Be(first.DefaultEmail);
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "password_reset.rate_limit_rejected"))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task PasswordResetEmail_Request_RateLimitsByClient()
    {
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.PasswordReset.MaxRequestsPerClientPerWindow = 1;
        });
        var first = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Client Limited One",
            "client-limited-one@example.com",
            "OldPassword123!"));
        var second = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Client Limited Two",
            "client-limited-two@example.com",
            "OldPassword123!"));

        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(first.DefaultEmail!, ClientId: "test-client"),
            CreatePasswordHttpContext("203.0.113.97"));
        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(second.DefaultEmail!, ClientId: "test-client"),
            CreatePasswordHttpContext("203.0.113.98"));

        harness.EmailSender.Messages.Should().ContainSingle();
        harness.EmailSender.Messages.Single().To.Should().Be(first.DefaultEmail);
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "password_reset.rate_limit_rejected"))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task PasswordResetEmail_DeliveryFailure_AuditsAndReturnsSafePublicResult()
    {
        using var harness = await PasswordResetHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Failed Delivery Reset",
            "failed-delivery-reset@example.com",
            "OldPassword123!"));
        harness.EmailSender.IsConfigured = false;

        var result = await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(user.DefaultEmail!),
            CreatePasswordHttpContext("203.0.113.99"));

        result.Message.Should().Be("If an account can be reset, you'll receive a password reset email shortly.");
        harness.EmailSender.Messages.Should().BeEmpty();
        (await harness.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "password_reset" && x.ConsumedAt == null))
            .Should().Be(0);
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "password_reset.email_send_failed"))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task PasswordResetEmail_Request_SupersedesPriorActiveResetToken()
    {
        using var harness = await PasswordResetHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Superseded Reset",
            "superseded-reset@example.com",
            "OldPassword123!"));

        await harness.Auth.SendPasswordResetEmailAsync(new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!));
        var firstToken = ExtractResetToken(harness.EmailSender.Messages.Last().TextBody);
        await harness.Auth.SendPasswordResetEmailAsync(new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!));
        var secondToken = ExtractResetToken(harness.EmailSender.Messages.Last().TextBody);

        var firstAct = async () => await harness.Auth.ResetPasswordAsync(new SqlOSResetPasswordRequest(firstToken, "FirstNewPassword123!"));
        await firstAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Password reset token is invalid or expired.");

        await harness.Auth.ResetPasswordAsync(new SqlOSResetPasswordRequest(secondToken, "SecondNewPassword123!"));
        var login = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "SecondNewPassword123!", "test-client", null),
            CreatePasswordHttpContext("203.0.113.95"));
        login.Tokens.Should().NotBeNull();
    }

    [TestMethod]
    public async Task PasswordResetEmail_CustomMessageBuilder_IsUsed()
    {
        using var harness = await PasswordResetHarness.CreateAsync(options =>
        {
            options.PasswordReset.BuildMessage = ctx => new SqlOS.AuthServer.Interfaces.SqlOSAuthEmailMessage(
                ctx.Email,
                "Custom reset",
                $"<a href=\"{ctx.ResetUrl}\">Reset</a>",
                $"Custom reset link: {ctx.ResetUrl}");
        });
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Custom Reset",
            "custom-reset@example.com",
            "OldPassword123!"));

        await harness.Auth.SendPasswordResetEmailAsync(new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!));

        harness.EmailSender.Messages.Should().ContainSingle();
        harness.EmailSender.Messages.Single().Subject.Should().Be("Custom reset");
        harness.EmailSender.Messages.Single().TextBody.Should().Contain("/sqlos/auth/password/reset?token=");
    }

    [TestMethod]
    public async Task EmailOtpVerify_WhenAuthorizationChallengeIsUsedAsStandalone_DoesNotConsumeChallenge()
    {
        using var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions();
        authOptions.SeedAuthPage(page => page.EnabledCredentialTypes = ["email_otp"]);
        var options = Options.Create(authOptions);
        var emailSender = new TestAuthEmailSender { IsConfigured = true };
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var transactionalEmailService = CreateTransactionalEmailService(context, crypto, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options, transactionalEmailService);

        await CreateEmailAdmin(context, crypto).EnsureBuiltInTemplatesAsync();
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
    public async Task EmailOtpSignup_WithExistingOrganizationId_WithoutPolicy_DoesNotCreateChallengeOrMembership()
    {
        var harness = await EmailOtpHarness.CreateAsync();
        var existingOrganization = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"OTP Existing {Guid.NewGuid():N}", null));
        var email = $"otp-attacker-{Guid.NewGuid():N}@example.com";

        var act = async () => await harness.Auth.RequestEmailOtpSignupAsync(
            new SqlOSEmailOtpSignupStartRequest(
                "OTP Mallory",
                email,
                "test-client",
                OrganizationName: null,
                OrganizationId: existingOrganization.Id,
                CustomFields: null),
            new DefaultHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(UnauthorizedOrganizationJoinMessage);

        harness.EmailSender.Messages.Should().BeEmpty();
        (await harness.Context.Set<SqlOSEmailOtpChallenge>().CountAsync(x => x.Email == email)).Should().Be(0);
        (await harness.Context.Set<SqlOSUserEmail>()
            .CountAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(email))).Should().Be(0);
        (await harness.Context.Set<SqlOSMembership>()
            .CountAsync(x => x.OrganizationId == existingOrganization.Id)).Should().Be(0);
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
    public async Task RequestEmailOtpSignupAsync_ReturnsUniformStartForExistingAndUnknownEmails()
    {
        var harness = await EmailOtpHarness.CreateAsync();
        const string existingEmail = "aa-existing@example.com";
        const string unknownEmail = "aa-unknown@example.com";
        await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Existing User", existingEmail, "P@ssword123!"));

        var existingStart = await harness.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Existing User",
            existingEmail,
            "test-client",
            "Uniform Org",
            OrganizationId: null,
            CustomFields: null));
        var unknownStart = await harness.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Unknown User",
            unknownEmail,
            "test-client",
            "Uniform Org",
            OrganizationId: null,
            CustomFields: null));

        existingStart.ChallengeToken.Should().NotBeNullOrWhiteSpace();
        existingStart.SignupToken.Should().NotBeNullOrWhiteSpace();
        existingStart.MaskedEmail.Should().Be(unknownStart.MaskedEmail);
        existingStart.Message.Should().Be(unknownStart.Message);
        existingStart.Message.Should().NotContain("already exists");
        harness.EmailSender.Messages.Select(static message => message.To)
            .Should().BeEquivalentTo(existingEmail, unknownEmail);

        var existingVerify = async () => await harness.Auth.VerifyEmailOtpSignupAsync(
            new SqlOSEmailOtpSignupVerifyRequest(
                existingStart.SignupToken,
                existingStart.ChallengeToken,
                GetLatestCode(harness.EmailSender, existingEmail)),
            new DefaultHttpContext());

        await existingVerify.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sign-in code is invalid or expired.");
        (await harness.Context.Set<SqlOSUserEmail>()
            .CountAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(existingEmail))).Should().Be(1);
        (await harness.Context.Set<SqlOSUserEmail>()
            .CountAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(unknownEmail))).Should().Be(0);
        (await harness.Context.Set<SqlOSAuditEvent>()
            .AnyAsync(x => x.EventType == "email_otp.signup_existing_email"
                && (x.DataJson ?? string.Empty).Contains("existing_email"))).Should().BeTrue();
        (await harness.Context.Set<SqlOSAuditEvent>()
            .AnyAsync(x => x.EventType == "email_otp.signup_existing_email_rejected"
                && (x.DataJson ?? string.Empty).Contains("challenge_bound_to_existing_user"))).Should().BeTrue();
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

    [TestMethod]
    public async Task RequestEmailOtpSignupAsync_UsesSeededEmailBrandingForDefaultTemplate()
    {
        var harness = await EmailOtpHarness.CreateAsync(options =>
        {
            options.SeedAuthEmails(email =>
            {
                email.ApplicationName = "Acme Portal";
                email.LogoBase64 = "data:image/png;base64,abc123";
                email.PrimaryColor = "#16a34a";
                email.AccentColor = "#111827";
                email.BackgroundColor = "#f0fdf4";
            });
        });

        await harness.Auth.RequestEmailOtpSignupAsync(new SqlOSEmailOtpSignupStartRequest(
            "Branded Email User",
            "branded-email@example.com",
            "test-client",
            "Branded Org",
            OrganizationId: null,
            CustomFields: null));

        var message = harness.EmailSender.Messages.Single();
        message.Subject.Should().Be("Your Acme Portal sign-up code");
        message.HtmlBody.Should().Contain("data:image/png;base64,abc123");
        message.HtmlBody.Should().Contain("#16a34a");
        message.HtmlBody.Should().Contain("#111827");
        message.HtmlBody.Should().Contain("#f0fdf4");
        message.TextBody.Should().Contain("Your Acme Portal sign-up code");
    }

    [TestMethod]
    public async Task MagicLink_Start_ReturnsGenericResponseForUnknownEmail()
    {
        var harness = await MagicLinkHarness.CreateAsync();

        var start = await harness.Auth.RequestMagicLinkAsync(
            new SqlOSMagicLinkStartRequest("unknown@example.com", "test-client", OrganizationId: null),
            new DefaultHttpContext());

        start.Message.Should().Be("If an account exists for un***@example.com, check your email for a sign-in link.");
        harness.EmailSender.Messages.Should().BeEmpty();
        var token = await harness.Context.Set<SqlOSTemporaryToken>().SingleAsync();
        token.Purpose.Should().Be(SqlOSMagicLinkService.TokenPurpose);
        token.UserId.Should().BeNull();
        token.PayloadJson.Should().Contain("\"Sent\":false");
    }

    [TestMethod]
    public async Task MagicLink_Start_StoresOnlyTokenHash()
    {
        var harness = await MagicLinkHarness.CreateAsync();
        await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Magic User", "magic@example.com", "P@ssword123!"));

        await harness.Auth.RequestMagicLinkAsync(
            new SqlOSMagicLinkStartRequest("magic@example.com", "test-client", OrganizationId: null),
            new DefaultHttpContext());

        var rawToken = ExtractMagicLinkToken(harness.EmailSender.Messages.Single().TextBody);
        var stored = await harness.Context.Set<SqlOSTemporaryToken>().SingleAsync();
        stored.TokenHash.Should().Be(harness.Crypto.HashToken(rawToken));
        stored.TokenHash.Should().NotBe(rawToken);
        stored.PayloadJson.Should().NotContain(rawToken);
    }

    [TestMethod]
    public async Task MagicLink_Start_DoesNotUseRequestHostForEmailedLink()
    {
        var harness = await MagicLinkHarness.CreateAsync(options =>
        {
            options.PublicOrigin = null;
            options.Issuer = "https://identity.example.test/sqlos/auth";
        });
        await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Magic User", "magic@example.com", "P@ssword123!"));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("attacker.example");

        await harness.Auth.RequestMagicLinkAsync(
            new SqlOSMagicLinkStartRequest("magic@example.com", "test-client", OrganizationId: null),
            httpContext);

        var message = harness.EmailSender.Messages.Single();
        message.TextBody.Should().Contain("https://identity.example.test/sqlos/auth/login/magic-link/complete?token=");
        message.TextBody.Should().NotContain("attacker.example");
    }

    [TestMethod]
    public async Task MagicLink_Start_EnforcesLocalRateLimitForUnknownAccounts()
    {
        var harness = await MagicLinkHarness.CreateAsync(options =>
        {
            options.MagicLink.ResendCooldown = TimeSpan.Zero;
            options.MagicLink.MaxLinksPerEmailPerWindow = 1;
        });
        var context = new DefaultHttpContext();

        await harness.Auth.RequestMagicLinkAsync(
            new SqlOSMagicLinkStartRequest("unknown-rate@example.com", "test-client", OrganizationId: null),
            context);
        var act = async () => await harness.Auth.RequestMagicLinkAsync(
            new SqlOSMagicLinkStartRequest("unknown-rate@example.com", "test-client", OrganizationId: null),
            context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many sign-in link requests. Try again later.");
        harness.EmailSender.Messages.Should().BeEmpty();
        (await harness.Context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "magic_link.rate_limit_rejected"))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task MagicLink_Complete_ValidToken_IssuesSessionWithMagicLinkMethod()
    {
        var harness = await MagicLinkHarness.CreateAsync();
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Magic User", "valid-magic@example.com", "P@ssword123!"));
        var organization = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Magic Org", null));
        await harness.Admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));

        await harness.Auth.RequestMagicLinkAsync(
            new SqlOSMagicLinkStartRequest("valid-magic@example.com", "test-client", organization.Id),
            new DefaultHttpContext());
        var rawToken = ExtractMagicLinkToken(harness.EmailSender.Messages.Single().TextBody);

        var result = await harness.Auth.CompleteMagicLinkAsync(
            new SqlOSMagicLinkCompleteRequest(rawToken),
            new DefaultHttpContext());

        result.RequiresOrganizationSelection.Should().BeFalse();
        result.Tokens.Should().NotBeNull();
        result.Tokens!.OrganizationId.Should().Be(organization.Id);
        var session = await harness.Context.Set<SqlOSSession>().SingleAsync();
        session.AuthenticationMethod.Should().Be("magic_link");
        session.UserId.Should().Be(user.Id);
    }

    [TestMethod]
    public async Task MagicLink_Complete_ReplayedToken_IsRejected()
    {
        var harness = await MagicLinkHarness.CreateAsync();
        await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Replay User", "replay-magic@example.com", "P@ssword123!"));

        await harness.Auth.RequestMagicLinkAsync(
            new SqlOSMagicLinkStartRequest("replay-magic@example.com", "test-client", OrganizationId: null),
            new DefaultHttpContext());
        var rawToken = ExtractMagicLinkToken(harness.EmailSender.Messages.Single().TextBody);

        await harness.Auth.CompleteMagicLinkAsync(new SqlOSMagicLinkCompleteRequest(rawToken), new DefaultHttpContext());

        var act = async () => await harness.Auth.CompleteMagicLinkAsync(
            new SqlOSMagicLinkCompleteRequest(rawToken),
            new DefaultHttpContext());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sign-in link is invalid or expired.");
    }

    [TestMethod]
    public async Task MagicLink_Complete_ExpiredToken_IsRejectedGenerically()
    {
        var harness = await MagicLinkHarness.CreateAsync();
        await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Magic User", "expired-magic@example.com", "P@ssword123!"));
        await harness.Auth.RequestMagicLinkAsync(
            new SqlOSMagicLinkStartRequest("expired-magic@example.com", "test-client", OrganizationId: null),
            new DefaultHttpContext());
        var rawToken = ExtractMagicLinkToken(harness.EmailSender.Messages.Single().TextBody);
        var stored = await harness.Context.Set<SqlOSTemporaryToken>().SingleAsync();
        stored.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await harness.Context.SaveChangesAsync();

        var act = async () => await harness.Auth.CompleteMagicLinkAsync(
            new SqlOSMagicLinkCompleteRequest(rawToken),
            new DefaultHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sign-in link is invalid or expired.");
    }

    [TestMethod]
    public async Task MagicLink_Complete_WrongOAuthRequestBinding_IsRejected()
    {
        var harness = await TestHarness.CreateAsync(configure: options =>
        {
            options.EnableLocalPasswordAuth = false;
            options.SeedAuthPage(page =>
            {
                page.EnabledCredentialTypes = ["magic_link"];
                page.EnablePasswordSignup = false;
            });
        });
        await CreateEmailAdmin(harness.Context, harness.Crypto).EnsureBuiltInTemplatesAsync();
        var emailSender = new TestAuthEmailSender { IsConfigured = true };
        var magicLink = new SqlOSMagicLinkService(
            harness.Context,
            harness.Admin,
            harness.Crypto,
            harness.Settings,
            emailSender,
            Options.Create(harness.Options),
            CreateTransactionalEmailService(harness.Context, harness.Crypto, emailSender));
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Bound Magic", "bound-magic@example.com", "P@ssword123!"));
        var first = await CreateHeadlessAuthorizationRequestAsync(harness, "magic-first", user.DefaultEmail);
        var second = await CreateHeadlessAuthorizationRequestAsync(harness, "magic-second", user.DefaultEmail);

        await magicLink.StartForAuthorizationRequestAsync(first, user.DefaultEmail!, CreatePasswordHttpContext("203.0.113.240"));
        var rawToken = ExtractMagicLinkToken(emailSender.Messages.Single().TextBody);

        var act = async () => await magicLink.CompleteAsync(
            new SqlOSMagicLinkCompleteRequest(rawToken),
            second.Id,
            requireAuthorizationRequestMatch: true);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sign-in link is invalid or expired.");
    }

    [TestMethod]
    public void MagicLink_DoesNotSatisfyMfaByDefault()
    {
        var policy = new SqlOSMfaPolicyService(Options.Create(new SqlOSAuthServerOptions()));

        policy.SatisfiesStrongMfa("magic_link").Should().BeFalse();
    }

    /* ─────────────────────────────────────────────────────────────────────────
       Refresh token grace window tests (issue #18)
       ───────────────────────────────────────────────────────────────────────── */

    [TestMethod]
    public async Task Refresh_WithinGraceWindow_ReturnsSameTokenPair()
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
        secondRefresh.RefreshToken.Should().Be(firstRefresh.RefreshToken,
            "a retry must converge on the winner's refresh token instead of creating a sibling lineage");
    }

    [TestMethod]
    public async Task Refresh_WithinGraceWindow_DoesNotRevokeFamily()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("alice");

        var firstRefresh = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        // Second call within the grace window — should NOT trigger replay detection.
        var retry = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
        retry.RefreshToken.Should().Be(firstRefresh.RefreshToken);

        // The forward refresh token from the first call should still be usable.
        var thirdRefresh = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(firstRefresh.RefreshToken, firstRefresh.OrganizationId));

        thirdRefresh.AccessToken.Should().NotBeNullOrWhiteSpace(
            "the family should not have been revoked by a legitimate concurrent refresh");
    }

    [TestMethod]
    public async Task Refresh_GraceWindow_RejectsRevokedSessionBeforeReleasingCachedPair()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("revoked-session");
        await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        var original = await harness.Context.Set<SqlOSRefreshToken>()
            .Include(x => x.Session)
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(initialTokens.RefreshToken));
        original.Session!.RevokedAt = DateTime.UtcNow;
        original.Session.RevocationReason = "security_event";
        await harness.Context.SaveChangesAsync();

        var act = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Session is no longer active.");
    }

    [TestMethod]
    public async Task Refresh_GraceWindow_RejectsExpiredSessionBeforeReleasingCachedPair()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("expired-session");
        await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        var original = await harness.Context.Set<SqlOSRefreshToken>()
            .Include(x => x.Session)
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(initialTokens.RefreshToken));
        original.Session!.AbsoluteExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await harness.Context.SaveChangesAsync();

        var act = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Session is no longer active.");
    }

    [TestMethod]
    public async Task Refresh_GraceWindowManyRetries_LeavesOneActiveReplacement()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("many-retries");

        var winner = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var retry = await harness.Auth.RefreshAsync(
                new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
            retry.AccessToken.Should().Be(winner.AccessToken);
            retry.RefreshToken.Should().Be(winner.RefreshToken);
        }

        var originalHash = harness.Crypto.HashToken(initialTokens.RefreshToken);
        var original = await harness.Context.Set<SqlOSRefreshToken>()
            .SingleAsync(x => x.TokenHash == originalHash);
        var family = await harness.Context.Set<SqlOSRefreshToken>()
            .Where(x => x.FamilyId == original.FamilyId)
            .ToListAsync();

        family.Should().HaveCount(2);
        family.Should().ContainSingle(x => x.ConsumedAt == null && x.RevokedAt == null);
        family.Single(x => x.ConsumedAt == null).TokenHash.Should().Be(
            harness.Crypto.HashToken(winner.RefreshToken));
    }

    [TestMethod]
    public async Task Refresh_AttackerAndLegitimateBranches_CannotCoexist()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("branch-convergence");

        var legitimateR1 = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
        var attackerR1 = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
        attackerR1.RefreshToken.Should().Be(legitimateR1.RefreshToken);

        var legitimateR2 = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(legitimateR1.RefreshToken, legitimateR1.OrganizationId));
        var attackerR2 = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(attackerR1.RefreshToken, attackerR1.OrganizationId));

        attackerR2.AccessToken.Should().Be(legitimateR2.AccessToken);
        attackerR2.RefreshToken.Should().Be(legitimateR2.RefreshToken);

        var original = await harness.Context.Set<SqlOSRefreshToken>()
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(initialTokens.RefreshToken));
        var family = await harness.Context.Set<SqlOSRefreshToken>()
            .Where(x => x.FamilyId == original.FamilyId)
            .ToListAsync();
        family.Should().HaveCount(3, "R0 -> R1 -> R2 is one linear lineage");
        family.Should().ContainSingle(x => x.ConsumedAt == null && x.RevokedAt == null);
    }

    [TestMethod]
    public async Task Refresh_OlderParentAfterReplacementAdvanced_DoesNotMintSibling()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("advanced-parent");

        var r1 = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
        var r2 = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(r1.RefreshToken, r1.OrganizationId));

        var staleParentRetry = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
        staleParentRetry.RefreshToken.Should().Be(r1.RefreshToken,
            "an older parent can only return its existing direct replacement");

        var converged = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(staleParentRetry.RefreshToken, staleParentRetry.OrganizationId));
        converged.RefreshToken.Should().Be(r2.RefreshToken);

        var original = await harness.Context.Set<SqlOSRefreshToken>()
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(initialTokens.RefreshToken));
        (await harness.Context.Set<SqlOSRefreshToken>()
            .CountAsync(x => x.FamilyId == original.FamilyId)).Should().Be(3);
    }

    [TestMethod]
    public async Task OAuthTokenEndpoint_RefreshGraceRetry_ReturnsSameTokenPair()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("protocol-refresh");
        var request = new SqlOSTokenRequest(
            SqlOSOAuthGrantTypes.RefreshToken,
            null,
            null,
            null,
            null,
            initialTokens.RefreshToken,
            null);

        var winner = await harness.Authorization.ExchangeAuthorizationCodeAsync(
            request,
            new DefaultHttpContext());
        var retry = await harness.Authorization.ExchangeAuthorizationCodeAsync(
            request,
            new DefaultHttpContext());

        retry.Tokens.AccessToken.Should().Be(winner.Tokens.AccessToken);
        retry.Tokens.RefreshToken.Should().Be(winner.Tokens.RefreshToken);
    }

    [TestMethod]
    public async Task Refresh_GraceWindowWithoutDataProtection_FailsClosedWithoutConsumingParent()
    {
        var harness = await TestHarness.CreateAsync(
            graceWindowSeconds: 30,
            includeDataProtection: false);
        var initialTokens = await harness.SignUpAsync("no-data-protection");

        var act = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*require ASP.NET Core Data Protection*");

        harness.Context.ChangeTracker.Clear();
        var stored = await harness.Context.Set<SqlOSRefreshToken>()
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(initialTokens.RefreshToken));
        stored.ConsumedAt.Should().BeNull();
        stored.ReplacedByTokenId.Should().BeNull();
    }

    [TestMethod]
    public async Task RefreshCleanup_AfterGrace_RemovesResponseButKeepsReplayLineage()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("cleanup");
        await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        var original = await harness.Context.Set<SqlOSRefreshToken>()
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(initialTokens.RefreshToken));
        original.ConsumedAt = DateTime.UtcNow.AddMinutes(-1);
        await harness.Context.SaveChangesAsync();

        await harness.Admin.CleanupExpiredRefreshTokensAsync();

        var retained = await harness.Context.Set<SqlOSRefreshToken>()
            .SingleAsync(x => x.Id == original.Id);
        retained.ReplacementTokenResponse.Should().BeNull();
        retained.ReplacedByTokenId.Should().NotBeNull();
        retained.TokenHash.Should().NotBeNullOrWhiteSpace(
            "the consumed parent must remain available for replay-family revocation");
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
    public async Task Refresh_GraceWindow_CachedTokenPairIsTimeLimitedAndEncryptedAtRest()
    {
        // The historical ReplacementAccessToken column now contains the
        // complete response under purpose-bound, time-limited protection.
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("encrypt");

        var firstRefresh = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));

        // Read the persisted row directly and verify the cached value is
        // NOT the raw access token JWT.
        var consumed = await harness.Context.Set<SqlOSRefreshToken>()
            .FirstAsync(x => x.TokenHash == harness.Crypto.HashToken(initialTokens.RefreshToken));

        consumed.ReplacementTokenResponse.Should().StartWith("dpt:");
        consumed.ReplacementTokenResponse.Should().NotContain(firstRefresh.AccessToken);
        consumed.ReplacementTokenResponse.Should().NotContain(firstRefresh.RefreshToken);

        // And the grace window path must still recover the original JWT.
        var graceHit = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
        graceHit.AccessToken.Should().Be(firstRefresh.AccessToken,
            "decryption must round-trip back to the original JWT");
        graceHit.RefreshToken.Should().Be(firstRefresh.RefreshToken,
            "decryption must return the same forward refresh credential");
    }

    [TestMethod]
    public async Task Refresh_DbContentsAlone_CannotRecoverCachedTokenPair()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var initialTokens = await harness.SignUpAsync("db-only");
        var firstRefresh = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(initialTokens.RefreshToken, initialTokens.OrganizationId));
        var consumed = await harness.Context.Set<SqlOSRefreshToken>()
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(initialTokens.RefreshToken));

        var dbOnlyCrypto = new SqlOSCryptoService(
            harness.Context,
            Options.Create(harness.Options),
            dataProtectionProvider: null);
        var act = () => dbOnlyCrypto.UnprotectRefreshTokenResponse(consumed.ReplacementTokenResponse!);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no Data Protection provider is available*");
        consumed.ReplacementTokenResponse.Should().NotContain(firstRefresh.AccessToken);
        consumed.ReplacementTokenResponse.Should().NotContain(firstRefresh.RefreshToken);
    }

    [TestMethod]
    public async Task RefreshTokenResponseProtection_AfterCryptographicExpiry_CannotBeUnprotected()
    {
        using var context = CreateContext();
        var crypto = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            new EphemeralDataProtectionProvider());
        var protectedResponse = crypto.ProtectRefreshTokenResponse(
            "{\"accessToken\":\"access\",\"refreshToken\":\"refresh\"}",
            TimeSpan.FromMilliseconds(20));

        await Task.Delay(100);

        var act = () => crypto.UnprotectRefreshTokenResponse(protectedResponse);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*invalid or its retry window has expired*");
    }

    [TestMethod]
    public async Task Refresh_CrossFamilyProtectedResponseSwap_RevokesOnlyTargetFamily()
    {
        var harness = await TestHarness.CreateAsync(graceWindowSeconds: 30);
        var sourceInitial = await harness.SignUpAsync("swap-source");
        var targetInitial = await harness.SignUpAsync("swap-target");
        var sourceWinner = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(sourceInitial.RefreshToken, sourceInitial.OrganizationId));
        await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(targetInitial.RefreshToken, targetInitial.OrganizationId));

        var sourceParent = await harness.Context.Set<SqlOSRefreshToken>()
            .Include(x => x.Session)
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(sourceInitial.RefreshToken));
        var targetParent = await harness.Context.Set<SqlOSRefreshToken>()
            .Include(x => x.Session)
            .SingleAsync(x => x.TokenHash == harness.Crypto.HashToken(targetInitial.RefreshToken));
        targetParent.ReplacementTokenResponse = sourceParent.ReplacementTokenResponse;
        await harness.Context.SaveChangesAsync();

        var act = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(targetInitial.RefreshToken, targetInitial.OrganizationId));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Refresh token has already been used.");

        var targetSession = await harness.Context.Set<SqlOSSession>()
            .SingleAsync(x => x.Id == targetParent.SessionId);
        targetSession.RevocationReason.Should().Be("refresh_token_response_invalid");
        (await harness.Context.Set<SqlOSRefreshToken>()
            .Where(x => x.FamilyId == targetParent.FamilyId)
            .ToListAsync()).Should().OnlyContain(x => x.RevokedAt != null);

        var sourceSession = await harness.Context.Set<SqlOSSession>()
            .SingleAsync(x => x.Id == sourceParent.SessionId);
        sourceSession.RevokedAt.Should().BeNull();
        var sourceAdvanced = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(sourceWinner.RefreshToken, sourceWinner.OrganizationId));
        sourceAdvanced.AccessToken.Should().NotBeNullOrWhiteSpace();
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

    private static DefaultHttpContext CreatePasswordHttpContext(string ipAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        context.Request.Headers.UserAgent = "SqlOSTest";
        return context;
    }

    private static DefaultHttpContext CreateInvitationHttpContext()
    {
        var context = CreatePasswordHttpContext("203.0.113.214");
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("auth.example.test");
        return context;
    }

    private static void ConfigureHeadlessMfa(SqlOSAuthServerOptions options)
    {
        options.Mfa.Enabled = true;
        options.Mfa.AllowUserSelfEnrollmentByDefault = true;
        options.Mfa.RecoveryCodesEnabledByDefault = true;
        options.UseHeadlessAuthPage(headless =>
        {
            headless.BuildUiUrl = ctx =>
                $"https://app.example.test/authorize?request={Uri.EscapeDataString(ctx.RequestId ?? string.Empty)}&view={Uri.EscapeDataString(ctx.View)}";
        });
    }

    private static void ConfigureRequiredMfa(SqlOSAuthServerOptions options)
    {
        options.Mfa.Enabled = true;
        options.Mfa.RequireForAllUsersByDefault = true;
        options.Mfa.AllowUserSelfEnrollmentByDefault = true;
        options.Mfa.RecoveryCodesEnabledByDefault = true;
    }

    private static async Task<(SqlOSUser User, string Secret)> CreateEnrolledMfaUserAsync(
        TestHarness harness,
        string displayName)
    {
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            displayName,
            $"mfa-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var enrollment = await harness.Auth.StartTotpEnrollmentAsync(
            user.Id,
            new SqlOSTotpEnrollmentStartRequest($"{displayName} authenticator"));
        await harness.Auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
            enrollment.EnrollmentToken,
            harness.Totp.GenerateCodeForTesting(enrollment.Secret)));
        return (user, enrollment.Secret);
    }

    private static async Task<SqlOSLoginResult> LoginForRequiredMfaAsync(
        TestHarness harness,
        SqlOSUser user,
        string clientId)
    {
        var login = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", clientId, null),
            CreatePasswordHttpContext("203.0.113.224"));
        login.RequiresMfa.Should().BeTrue();
        login.RequiresMfaEnrollment.Should().BeTrue();
        login.Tokens.Should().BeNull();
        return login;
    }

    private static async Task RequireMfaForAllUsersAsync(TestHarness harness, string organizationId)
    {
        await harness.Settings.UpdateOrganizationMfaPolicyAsync(
            organizationId,
            new SqlOSUpdateOrganizationMfaPolicyRequest(
                IsEnabled: true,
                RequireMfaForAllUsers: true,
                RequireMfaForOwnersAndAdmins: false,
                UserSelfEnrollmentEnabled: true,
                RecoveryCodesEnabled: true,
                RequiredRoles: ["owner", "admin"],
                AvailableFactors: [SqlOSMfaFactorTypes.Totp, SqlOSMfaFactorTypes.RecoveryCode]));
    }

    private static async Task<SqlOSAuthorizationRequest> CreateHeadlessAuthorizationRequestAsync(
        TestHarness harness,
        string state,
        string? loginHint)
        => await harness.Authorization.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                "test-client",
                "https://client.example.test/callback",
                state,
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                loginHint,
                null,
                null,
                "headless",
                null));

    private static string GetLatestCode(TestAuthEmailSender sender, string email)
    {
        var message = sender.Messages.Last(x => string.Equals(x.To, email, StringComparison.OrdinalIgnoreCase));
        return Regex.Match(message.TextBody ?? string.Empty, @"\b\d{4,8}\b").Value;
    }

    private static string ExtractInvitationToken(string inviteUrl)
    {
        var query = new Uri(inviteUrl).Query.TrimStart('?');
        var tokenPart = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .First(x => x.StartsWith("token=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(tokenPart["token=".Length..]);
    }

    private static string ExtractResetToken(string? textBody)
    {
        var match = Regex.Match(textBody ?? string.Empty, @"token=([A-Za-z0-9_-]+)");
        match.Success.Should().BeTrue();
        return match.Groups[1].Value;
    }

    private static string ExtractMagicLinkToken(string? textBody)
    {
        var match = Regex.Match(textBody ?? string.Empty, @"token=([A-Za-z0-9_-]+)");
        match.Success.Should().BeTrue();
        return match.Groups[1].Value;
    }

    private static SqlOSTransactionalEmailService CreateTransactionalEmailService(
        TestSqlOSInMemoryDbContext context,
        SqlOSCryptoService crypto,
        TestAuthEmailSender sender)
        => new(
            context,
            crypto,
            sender,
            new SqlOSEmailTemplateRenderer(),
            Options.Create(new SqlOSEmailOptions()));

    private static SqlOSEmailAdminService CreateEmailAdmin(
        TestSqlOSInMemoryDbContext context,
        SqlOSCryptoService crypto)
        => new(context, crypto, new SqlOSEmailTemplateRenderer());

    private sealed class PasswordResetHarness : IDisposable
    {
        public required TestSqlOSInMemoryDbContext Context { get; init; }
        public required SqlOSAuthService Auth { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSCryptoService Crypto { get; init; }
        public required SqlOSAuthServerOptions Options { get; init; }
        public required TestAuthEmailSender EmailSender { get; init; }

        public static async Task<PasswordResetHarness> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);

            var authOptions = new SqlOSAuthServerOptions();
            authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            authOptions.SeedAuthPage(page =>
            {
                page.EnabledCredentialTypes = ["password"];
                page.EnablePasswordSignup = true;
            });
            configure?.Invoke(authOptions);

            var options = Microsoft.Extensions.Options.Options.Create(authOptions);
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var crypto = TestCryptoService.Create(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var transactionalEmailService = CreateTransactionalEmailService(context, crypto, emailSender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options, transactionalEmailService);
            var auth = new SqlOSAuthService(
                context,
                options,
                admin,
                crypto,
                settings,
                emailOtp,
                transactionalEmailService: transactionalEmailService,
                authEmailSender: emailSender);

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.UpsertSeededAuthPageSettingsAsync();
            await settings.UpsertSeededAuthEmailSettingsAsync();
            await CreateEmailAdmin(context, crypto).EnsureBuiltInTemplatesAsync();

            return new PasswordResetHarness
            {
                Context = context,
                Auth = auth,
                Admin = admin,
                Crypto = crypto,
                Options = authOptions,
                EmailSender = emailSender
            };
        }

        public void Dispose()
            => Context.Dispose();
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
            var crypto = TestCryptoService.Create(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var transactionalEmailService = CreateTransactionalEmailService(context, crypto, emailSender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options, transactionalEmailService);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp, transactionalEmailService: transactionalEmailService);

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.UpsertSeededAuthPageSettingsAsync();
            await settings.UpsertSeededAuthEmailSettingsAsync();
            await CreateEmailAdmin(context, crypto).EnsureBuiltInTemplatesAsync();

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

    private sealed class MagicLinkHarness : IDisposable
    {
        public required TestSqlOSInMemoryDbContext Context { get; init; }
        public required SqlOSAuthService Auth { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSCryptoService Crypto { get; init; }
        public required TestAuthEmailSender EmailSender { get; init; }

        public static async Task<MagicLinkHarness> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
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
                page.EnabledCredentialTypes = ["magic_link"];
                page.EnablePasswordSignup = false;
            });
            configure?.Invoke(authOptions);

            var options = Options.Create(authOptions);
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var crypto = new SqlOSCryptoService(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var transactionalEmailService = CreateTransactionalEmailService(context, crypto, emailSender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options, transactionalEmailService);
            var magicLink = new SqlOSMagicLinkService(context, admin, crypto, settings, emailSender, options, transactionalEmailService);
            var auth = new SqlOSAuthService(
                context,
                options,
                admin,
                crypto,
                settings,
                emailOtp,
                transactionalEmailService: transactionalEmailService,
                magicLinkService: magicLink);

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.UpsertSeededAuthPageSettingsAsync();
            await settings.UpsertSeededAuthEmailSettingsAsync();
            await CreateEmailAdmin(context, crypto).EnsureBuiltInTemplatesAsync();

            return new MagicLinkHarness
            {
                Context = context,
                Auth = auth,
                Admin = admin,
                Crypto = crypto,
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
        public required SqlOSAuthorizationServerService Authorization { get; init; }
        public required SqlOSHeadlessAuthService Headless { get; init; }
        public required SqlOSInvitationService Invitation { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSCryptoService Crypto { get; init; }
        public required SqlOSSettingsService Settings { get; init; }
        public required SqlOSMfaPolicyService MfaPolicy { get; init; }
        public required SqlOSTotpMfaService Totp { get; init; }
        public required SqlOSAuthServerOptions Options { get; init; }

        public static async Task<TestHarness> CreateAsync(
            int graceWindowSeconds = 30,
            bool includeDataProtection = true,
            Action<SqlOSAuthServerOptions>? configure = null)
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
            configure?.Invoke(authOptions);
            var options = Microsoft.Extensions.Options.Options.Create(authOptions);

            // Inject a real ephemeral data protection provider so the
            // ReplacementAccessToken cache is encrypted at rest as in production.
            var dataProtectionProvider = includeDataProtection
                ? new EphemeralDataProtectionProvider()
                : null;
            var crypto = includeDataProtection
                ? TestCryptoService.Create(context, options, dataProtectionProvider)
                : new SqlOSCryptoService(
                    context,
                    options,
                    new SqlOSDataProtectionSigningKeyCustody(new EphemeralDataProtectionProvider()),
                    dataProtectionProvider: null);
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var transactionalEmailService = CreateTransactionalEmailService(context, crypto, emailSender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options, transactionalEmailService);
            var invitation = new SqlOSInvitationService(context, admin, crypto, emailSender, settings, options, transactionalEmailService);
            var passwordAbuse = new SqlOSPasswordLoginAbuseService(context, admin, crypto, options);
            var mfaPolicy = new SqlOSMfaPolicyService(context, settings, options);
            var totp = new SqlOSTotpMfaService(context, crypto, mfaPolicy, options);
            var auth = new SqlOSAuthService(
                context,
                options,
                admin,
                crypto,
                settings,
                emailOtp,
                invitationService: invitation,
                passwordLoginAbuseService: passwordAbuse,
                transactionalEmailService: transactionalEmailService,
                mfaPolicyService: mfaPolicy,
                totpMfaService: totp);
            var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
            var authorization = new SqlOSAuthorizationServerService(
                context,
                admin,
                auth,
                crypto,
                settings,
                authPageSession,
                options,
                invitationService: invitation,
                passwordLoginAbuseService: passwordAbuse,
                mfaPolicyService: mfaPolicy,
                totpMfaService: totp);
            var discovery = new SqlOSHomeRealmDiscoveryService(context);
            var oidcAuth = new SqlOSOidcAuthService(
                context,
                admin,
                crypto,
                new FakeOidcProviderHttpClientFactory(),
                NullLogger<SqlOSOidcAuthService>.Instance);
            var saml = new SqlOSSamlService(context, options, admin, crypto);
            var oidcBrowserAuth = new SqlOSOidcBrowserAuthService(
                context,
                admin,
                auth,
                authorization,
                crypto,
                oidcAuth,
                options);
            var headless = new SqlOSHeadlessAuthService(
                context,
                admin,
                authorization,
                discovery,
                oidcBrowserAuth,
                saml,
                settings,
                emailOtp,
                options,
                invitationService: invitation,
                authService: auth);

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.UpsertSeededAuthPageSettingsAsync();
            await settings.UpsertSeededAuthEmailSettingsAsync();
            await settings.UpsertSeededMfaSettingsAsync();

            return new TestHarness
            {
                Context = context,
                Auth = auth,
                Authorization = authorization,
                Headless = headless,
                Invitation = invitation,
                Admin = admin,
                Crypto = crypto,
                Settings = settings,
                MfaPolicy = mfaPolicy,
                Totp = totp,
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
