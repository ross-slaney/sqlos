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
                "challenge-headless-mfa",
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
                "challenge-headless-mfa-challenge",
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
            new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!, ClientId: "test-client"),
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
    public async Task PasswordResetEmail_Request_UnknownEmail_ReturnsGenericSuccessAndDoesNotSend()
    {
        using var harness = await PasswordResetHarness.CreateAsync();

        var result = await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSSendPasswordResetEmailRequest("missing@example.com"),
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
            new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!),
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
            new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!),
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
            new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!),
            CreatePasswordHttpContext("203.0.113.94"));
        var second = await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!),
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
            new SqlOSSendPasswordResetEmailRequest(first.DefaultEmail!),
            CreatePasswordHttpContext("203.0.113.96"));
        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSSendPasswordResetEmailRequest(second.DefaultEmail!),
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
            new SqlOSSendPasswordResetEmailRequest(first.DefaultEmail!, ClientId: "test-client"),
            CreatePasswordHttpContext("203.0.113.97"));
        await harness.Auth.RequestPasswordResetEmailAsync(
            new SqlOSSendPasswordResetEmailRequest(second.DefaultEmail!, ClientId: "test-client"),
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
            new SqlOSSendPasswordResetEmailRequest(user.DefaultEmail!),
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
                $"challenge-{state}",
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
            var crypto = TestCryptoService.Create(context, options, new EphemeralDataProtectionProvider());
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
