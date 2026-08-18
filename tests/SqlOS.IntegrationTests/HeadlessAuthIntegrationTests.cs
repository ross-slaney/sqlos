using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Configuration;
using SqlOS.Email.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class HeadlessAuthIntegrationTests
{
    private const string UnauthorizedOrganizationJoinMessage =
        "Joining an existing organization requires an invitation or approved join policy.";
    private const string ValidPkceCodeChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task CreateAuthorizationRequestAsync_PersistsHeadlessPresentationAndUiContext()
    {
        await using var fixture = await CreateFixtureAsync();

        var request = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-123",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                "alice@example.com",
                null,
                null,
                "headless",
                """{"lng":"en","template":"starter-pack"}"""));

        request.PresentationMode.Should().Be("headless");
        request.UiContextJson.Should().Contain("\"lng\":\"en\"");
        request.UiContextJson.Should().Contain("\"template\":\"starter-pack\"");
    }

    [TestMethod]
    public async Task GetRequestAsync_ReturnsProviders_AndConfiguredHeadlessApiBasePath()
    {
        await using var fixture = await CreateFixtureAsync(headless =>
        {
            headless.HeadlessApiBasePath = "/sqlos/auth/custom-headless";
        });

        await fixture.AdminService.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Custom,
            $"Acme OIDC {Guid.NewGuid():N}",
            "custom-client",
            "custom-secret",
            ["https://app.example.local/callback/custom"],
            false,
            null,
            "https://oidc.example.local",
            "https://oidc.example.local/authorize",
            "https://oidc.example.local/token",
            "https://oidc.example.local/userinfo",
            "https://oidc.example.local/jwks",
            null,
            ["openid", "profile", "email"],
            new SqlOSOidcClaimMapping
            {
                SubjectClaim = "custom_sub",
                EmailClaim = "email_address",
                EmailVerifiedClaim = "email_verified_flag",
                DisplayNameClaim = "full_name"
            },
            SqlOSOidcClientAuthMethod.ClientSecretPost,
            true,
            null,
            null,
            null));

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-456",
                "openid",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                """{"lng":"en"}"""));

        var viewModel = await fixture.HeadlessAuthService.GetRequestAsync(
            authorizationRequest.Id,
            "signup",
            error: null,
            pendingToken: null,
            email: null,
            displayName: null);

        viewModel.View.Should().Be("signup");
        viewModel.HeadlessApiBasePath.Should().Be("/sqlos/auth/custom-headless");
        viewModel.ClientId.Should().Be(fixture.ClientId);
        viewModel.Scope.Should().BeEmpty();
        viewModel.Providers.Should().ContainSingle(x => x.ProviderType == "Custom");
        viewModel.UiContext?["lng"]?.GetValue<string>().Should().Be("en");
    }

    [TestMethod]
    public async Task GetRequestAsync_ExposesGrantedScope_AndOmittingScopeStillReturnsAView()
    {
        await using var fixture = await CreateFixtureAsync(configureOptions: options =>
        {
            options.ClientSeeds[0].AllowedScopes = ["openid", "profile", "email"];
        });

        var granted = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-granted-scope",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                null));
        var grantedView = await fixture.HeadlessAuthService.GetRequestAsync(
            granted.Id,
            "login",
            error: null,
            pendingToken: null,
            email: null,
            displayName: null);
        grantedView.Scope.Should().Be("openid profile email");

        var omitted = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-omitted-scope",
                null,
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                null));
        var omittedView = await fixture.HeadlessAuthService.GetRequestAsync(
            omitted.Id,
            "login",
            error: null,
            pendingToken: null,
            email: null,
            displayName: null);
        omittedView.RequestId.Should().Be(omitted.Id);
        omittedView.Scope.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SignUpAsync_InvokesHook_AndReturnsAuthorizationRedirect()
    {
        JsonObject? capturedFields = null;

        await using var fixture = await CreateFixtureAsync(headless =>
        {
            headless.OnHeadlessSignupAsync = (ctx, _) =>
            {
                capturedFields = JsonNode.Parse(ctx.CustomFields.ToJsonString())?.AsObject();
                return Task.CompletedTask;
            };
        });

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-signup",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                """{"lng":"en"}"""));

        var email = $"alice-{Guid.NewGuid():N}@example.com";
        var organizationName = $"Acme-{Guid.NewGuid():N}";
        var result = await fixture.HeadlessAuthService.SignUpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Alice Example",
                email,
                "P@ssword123!",
                organizationName,
                new JsonObject
                {
                    ["firstName"] = "Alice",
                    ["lastName"] = "Example",
                    ["companyName"] = organizationName
                }));

        result.Type.Should().Be("redirect");
        result.RedirectUrl.Should().StartWith($"{fixture.RedirectUri}?");
        result.RedirectUrl.Should().Contain("code=");
        result.RedirectUrl.Should().Contain("state=state-signup");
        capturedFields?["companyName"]?.GetValue<string>().Should().Be(organizationName);

        (await fixture.Context.Set<SqlOSUserEmail>().CountAsync(x => x.Email == email)).Should().Be(1);
        (await fixture.Context.Set<SqlOSOrganization>().CountAsync(x => x.Name == organizationName)).Should().Be(1);
    }

    [TestMethod]
    public async Task HeadlessSignup_WithAuthorizationRequestOrganizationId_WithoutPolicy_DoesNotCreateMembership()
    {
        await using var fixture = await CreateFixtureAsync();
        var existingOrganization = await fixture.AdminService.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"Headless Existing {Guid.NewGuid():N}", null));

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-org-probe",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                null));
        authorizationRequest.OrganizationId = existingOrganization.Id;
        authorizationRequest.ResolvedOrganizationId = existingOrganization.Id;
        await fixture.Context.SaveChangesAsync();

        var email = $"headless-probe-{Guid.NewGuid():N}@example.com";
        var result = await fixture.HeadlessAuthService.SignUpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Headless Probe",
                email,
                "P@ssword123!",
                OrganizationName: null,
                CustomFields: new JsonObject()));

        result.Type.Should().Be("view");
        result.ViewModel.Should().NotBeNull();
        result.ViewModel!.View.Should().Be("signup");
        result.ViewModel.Error.Should().Be(UnauthorizedOrganizationJoinMessage);

        (await fixture.Context.Set<SqlOSUserEmail>().CountAsync(x => x.Email == email)).Should().Be(0);
        (await fixture.Context.Set<SqlOSMembership>()
            .CountAsync(x => x.OrganizationId == existingOrganization.Id)).Should().Be(0);
        (await fixture.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == authorizationRequest.Id)).Should().Be(0);
    }

    [TestMethod]
    public async Task SignUpAsync_WhenHookThrowsValidation_DoesNotPersistPartialSignup()
    {
        await using var fixture = await CreateFixtureAsync(headless =>
        {
            headless.OnHeadlessSignupAsync = (_, _) =>
                throw new SqlOSHeadlessValidationException(
                    "Validation failed.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["companyName"] = "Company name is already in use."
                    },
                    ["Please review the highlighted fields."]);
        });

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-validation",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                """{"lng":"en"}"""));

        var email = $"alice-{Guid.NewGuid():N}@example.com";
        var organizationName = $"Acme-{Guid.NewGuid():N}";
        var result = await fixture.HeadlessAuthService.SignUpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Alice Example",
                email,
                "P@ssword123!",
                organizationName,
                new JsonObject
                {
                    ["companyName"] = organizationName
                }));

        result.Type.Should().Be("view");
        result.ViewModel.Should().NotBeNull();
        result.ViewModel!.View.Should().Be("signup");
        result.ViewModel.FieldErrors.Should().ContainKey("companyName");
        result.ViewModel.Error.Should().Be("Please review the highlighted fields.");
        (await fixture.Context.Set<SqlOSUserEmail>().CountAsync(x => x.Email == email)).Should().Be(0);
        (await fixture.Context.Set<SqlOSOrganization>().CountAsync(x => x.Name == organizationName)).Should().Be(0);
    }

    [TestMethod]
    public async Task EmailOtpSignUpAsync_WhenHookThrowsValidation_RollsBackUserOrganizationAndChallengeConsumption()
    {
        await using var fixture = await CreateFixtureAsync(headless =>
        {
            headless.OnHeadlessSignupAsync = (_, _) =>
                throw new SqlOSHeadlessValidationException(
                    "Validation failed.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["companyName"] = "Company name is already in use."
                    },
                    ["Please review the highlighted fields."]);
        });

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-otp-validation",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                """{"lng":"en"}"""));

        var email = $"otp-validation-{Guid.NewGuid():N}@example.com";
        var organizationName = $"Otp-Acme-{Guid.NewGuid():N}";
        var start = await fixture.HeadlessAuthService.RequestEmailOtpSignupAsync(
            CreateHttpContext(),
            new SqlOSHeadlessEmailOtpSignupStartRequest(
                authorizationRequest.Id,
                "OTP Alice",
                email,
                organizationName,
                new JsonObject
                {
                    ["companyName"] = organizationName
                }));

        start.Type.Should().Be("view");
        start.ViewModel.Should().NotBeNull();
        start.ViewModel!.View.Should().Be("email-otp-signup-verify");
        var challengeToken = start.ViewModel.ChallengeToken!;
        var signupToken = start.ViewModel.SignupToken!;

        var verify = await fixture.HeadlessAuthService.VerifyEmailOtpSignupAsync(
            CreateHttpContext(),
            new SqlOSHeadlessEmailOtpSignupVerifyRequest(
                authorizationRequest.Id,
                signupToken,
                challengeToken,
                GetLatestCode(fixture.EmailSender, email)));

        verify.Type.Should().Be("view");
        verify.ViewModel.Should().NotBeNull();
        verify.ViewModel!.View.Should().Be("email-otp-signup-verify");
        verify.ViewModel.FieldErrors.Should().ContainKey("companyName");
        verify.ViewModel.Error.Should().Be("Please review the highlighted fields.");
        verify.ViewModel.SignupToken.Should().Be(signupToken);
        verify.ViewModel.ChallengeToken.Should().Be(challengeToken);

        (await fixture.Context.Set<SqlOSUserEmail>().CountAsync(x => x.Email == email)).Should().Be(0);
        (await fixture.Context.Set<SqlOSOrganization>().CountAsync(x => x.Name == organizationName)).Should().Be(0);
        (await fixture.Context.Set<SqlOSAuthorizationCode>().CountAsync(x => x.AuthorizationRequestId == authorizationRequest.Id)).Should().Be(0);
        (await fixture.Context.Set<SqlOSEmailOtpChallenge>().AsNoTracking().SingleAsync(x => x.Email == email)).ConsumedAt.Should().BeNull();
    }

    [TestMethod]
    public async Task RequestEmailOtpAsync_WithInvitationForUnknownEmail_ReturnsSignupViewWithoutPhantomChallenge()
    {
        await using var fixture = await CreateFixtureAsync();

        var organization = await fixture.AdminService.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"Invite OTP {Guid.NewGuid():N}", null));
        var invitedEmail = $"invited-{Guid.NewGuid():N}@example.com";
        var invitation = await fixture.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(
                organization.Id,
                invitedEmail,
                "member",
                SendEmail: false),
            CreateHttpContext());
        var invitationToken = ExtractInvitationToken(invitation.InviteUrl!);

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-invite-login-otp",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                null));
        await fixture.InvitationService.BindInvitationToAuthorizationRequestAsync(invitationToken, authorizationRequest);

        var result = await fixture.HeadlessAuthService.RequestEmailOtpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessEmailOtpStartRequest(
                authorizationRequest.Id,
                invitedEmail));

        result.Type.Should().Be("view");
        result.ViewModel.Should().NotBeNull();
        result.ViewModel!.View.Should().Be("signup");
        result.ViewModel.Error.Should().Be("Create an account to accept this invitation.");
        result.ViewModel.ChallengeToken.Should().BeNull();
        fixture.EmailSender.Messages.Should().BeEmpty();
        (await fixture.Context.Set<SqlOSEmailOtpChallenge>().CountAsync(x => x.Email == invitedEmail)).Should().Be(0);
    }

    [TestMethod]
    public async Task SignUpWithInvitationAsync_AcceptsInviteWithoutOtpChallenge()
    {
        JsonObject? capturedFields = null;
        await using var fixture = await CreateFixtureAsync(headless =>
        {
            headless.OnHeadlessSignupAsync = (ctx, _) =>
            {
                capturedFields = JsonNode.Parse(ctx.CustomFields.ToJsonString())?.AsObject();
                return Task.CompletedTask;
            };
        });

        var organization = await fixture.AdminService.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"Invite Direct {Guid.NewGuid():N}", null));
        var invitedEmail = $"direct-invite-{Guid.NewGuid():N}@example.com";
        var invitation = await fixture.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(
                organization.Id,
                invitedEmail,
                "member",
                SendEmail: false),
            CreateHttpContext());
        var invitationToken = ExtractInvitationToken(invitation.InviteUrl!);

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-invite-direct-signup",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                null));

        var result = await fixture.HeadlessAuthService.SignUpWithInvitationAsync(
            CreateHttpContext(),
            new SqlOSHeadlessInvitationSignupRequest(
                authorizationRequest.Id,
                "Direct Invite",
                invitedEmail,
                new JsonObject
                {
                    ["firstName"] = "Direct",
                    ["lastName"] = "Invite"
                },
                invitationToken));

        result.Type.Should().Be("redirect");
        result.RedirectUrl.Should().StartWith($"{fixture.RedirectUri}?");
        result.RedirectUrl.Should().Contain("code=");
        result.RedirectUrl.Should().Contain("state=state-invite-direct-signup");
        capturedFields?["firstName"]?.GetValue<string>().Should().Be("Direct");
        fixture.EmailSender.Messages.Should().BeEmpty();
        (await fixture.Context.Set<SqlOSEmailOtpChallenge>().CountAsync(x => x.Email == invitedEmail)).Should().Be(0);

        var email = await fixture.Context.Set<SqlOSUserEmail>().SingleAsync(x => x.Email == invitedEmail);
        email.IsVerified.Should().BeTrue();
        var membership = await fixture.Context.Set<SqlOSMembership>()
            .SingleAsync(x => x.UserId == email.UserId && x.OrganizationId == organization.Id);
        membership.Role.Should().Be("member");
        var storedInvitation = await fixture.Context.Set<SqlOSInvitation>().SingleAsync(x => x.Id == invitation.Id);
        storedInvitation.AcceptedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task SignUpWithInvitationAsync_WhenEmailMatchesSsoDomain_RedirectsBeforeCreatingUser()
    {
        await using var fixture = await CreateFixtureAsync();

        var domain = $"invite-sso-{Guid.NewGuid():N}"[..30].ToLowerInvariant() + ".test";
        var organization = await CreateSamlOrganizationAsync(fixture, domain);
        var invitedEmail = $"direct@{domain}";
        var invitation = await fixture.InvitationService.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(
                organization.Id,
                invitedEmail,
                "member",
                SendEmail: false),
            CreateHttpContext());
        var invitationToken = ExtractInvitationToken(invitation.InviteUrl!);

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-invite-sso-signup",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                null));

        var result = await fixture.HeadlessAuthService.SignUpWithInvitationAsync(
            CreateHttpContext(),
            new SqlOSHeadlessInvitationSignupRequest(
                authorizationRequest.Id,
                "SSO Invite",
                invitedEmail,
                new JsonObject(),
                invitationToken));

        result.Type.Should().Be("redirect");
        result.RedirectUrl.Should().StartWith("https://idp.example.test/sso?");
        fixture.EmailSender.Messages.Should().BeEmpty();
        (await fixture.Context.Set<SqlOSUserEmail>().CountAsync(x => x.Email == invitedEmail)).Should().Be(0);
        (await fixture.Context.Set<SqlOSEmailOtpChallenge>().CountAsync(x => x.Email == invitedEmail)).Should().Be(0);
        var storedInvitation = await fixture.Context.Set<SqlOSInvitation>().SingleAsync(x => x.Id == invitation.Id);
        storedInvitation.AcceptedAt.Should().BeNull();
    }

    [TestMethod]
    public async Task RequestEmailOtpAsync_WhenEmailMatchesSsoDomain_RedirectsBeforeCreatingOtpChallenge()
    {
        await using var fixture = await CreateFixtureAsync();

        var domain = $"headless-sso-{Guid.NewGuid():N}"[..30].ToLowerInvariant() + ".test";
        await CreateSamlOrganizationAsync(fixture, domain);

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-sso-otp",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                null));
        var email = $"alex@{domain}";

        var result = await fixture.HeadlessAuthService.RequestEmailOtpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessEmailOtpStartRequest(
                authorizationRequest.Id,
                email));

        result.Type.Should().Be("redirect");
        result.RedirectUrl.Should().StartWith("https://idp.example.test/sso?");
        fixture.EmailSender.Messages.Should().BeEmpty();
        (await fixture.Context.Set<SqlOSEmailOtpChallenge>().CountAsync(x => x.Email == email)).Should().Be(0);
    }

    [TestMethod]
    public async Task RequestEmailOtpSignupAsync_WhenEmailMatchesSsoDomain_RedirectsBeforeCreatingSignupChallenge()
    {
        await using var fixture = await CreateFixtureAsync();

        var domain = $"signup-sso-{Guid.NewGuid():N}"[..30].ToLowerInvariant() + ".test";
        await CreateSamlOrganizationAsync(fixture, domain);

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-sso-signup",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                null));
        var email = $"casey@{domain}";

        var result = await fixture.HeadlessAuthService.RequestEmailOtpSignupAsync(
            CreateHttpContext(),
            new SqlOSHeadlessEmailOtpSignupStartRequest(
                authorizationRequest.Id,
                "Casey SSO",
                email,
                "Casey Workspace",
                new JsonObject()));

        result.Type.Should().Be("redirect");
        result.RedirectUrl.Should().StartWith("https://idp.example.test/sso?");
        fixture.EmailSender.Messages.Should().BeEmpty();
        (await fixture.Context.Set<SqlOSEmailOtpChallenge>().CountAsync(x => x.Email == email)).Should().Be(0);
    }

    [TestMethod]
    public async Task SignUpAsync_EstablishesReusableAuthPageSession()
    {
        await using var fixture = await CreateFixtureAsync();

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-session",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                """{"lng":"en"}"""));

        var email = $"session-{Guid.NewGuid():N}@example.com";
        var httpContext = CreateHttpContext();
        var result = await fixture.HeadlessAuthService.SignUpAsync(
            httpContext,
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Session User",
                email,
                "P@ssword123!",
                "Session Org",
                new JsonObject()));

        result.Type.Should().Be("redirect");
        var authPageCookie = ExtractCookieValue(httpContext.Response.Headers.SetCookie.ToString(), "sqlos_auth_page");
        authPageCookie.Should().NotBeNullOrWhiteSpace();

        var followOnContext = CreateHttpContext();
        followOnContext.Request.Headers.Cookie = $"sqlos_auth_page={authPageCookie}";

        var session = await fixture.AuthPageSessionService.TryGetSessionAsync(followOnContext);
        session.Should().NotBeNull();
        session!.User.Id.Should().NotBeNullOrWhiteSpace();
        session.AuthenticationMethod.Should().Be("password");
    }

    [TestMethod]
    public async Task EnsureNativeHeadlessClientAllowedAsync_RejectsClientWithoutOptIn()
    {
        await using var fixture = await CreateFixtureAsync();

        var act = async () => await fixture.HeadlessAuthService.EnsureNativeHeadlessClientAllowedAsync(
            fixture.ClientId,
            fixture.RedirectUri);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("This client is not allowed to start native headless auth.");
    }

    [TestMethod]
    public async Task EnsureNativeHeadlessClientAllowedAsync_AllowsFirstPartyOptedInClient()
    {
        await using var fixture = await CreateFixtureAsync(allowNativeHeadlessAuth: true);

        var act = async () => await fixture.HeadlessAuthService.EnsureNativeHeadlessClientAllowedAsync(
            fixture.ClientId,
            fixture.RedirectUri);

        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task HeadlessPasswordLogin_UsesSameThrottleStateAsApiLogin()
    {
        await using var fixture = await CreateFixtureAsync(configureOptions: options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 1;
            options.PasswordLogin.LockoutDuration = TimeSpan.FromMinutes(10);
        });
        var user = await fixture.AdminService.CreateUserAsync(new SqlOSCreateUserRequest(
            "Headless Lockout",
            $"headless-lockout-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));

        var apiFailure = async () => await fixture.AuthService.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "wrong-password", fixture.ClientId, null),
            CreateHttpContext("203.0.113.70"));
        await apiFailure.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "headless-lockout",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                user.DefaultEmail,
                null,
                null,
                "headless",
                null));

        var headlessResult = await fixture.HeadlessAuthService.PasswordLoginAsync(
            CreateHttpContext("203.0.113.70"),
            new SqlOSHeadlessPasswordLoginRequest(
                authorizationRequest.Id,
                user.DefaultEmail!,
                "P@ssword123!"));

        headlessResult.Type.Should().Be("view");
        headlessResult.ViewModel!.Error.Should().Be(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
    }

    private static async Task<HeadlessFixture> CreateFixtureAsync(
        Action<SqlOSHeadlessAuthOptions>? configureHeadless = null,
        bool allowNativeHeadlessAuth = false,
        Action<SqlOSAuthServerOptions>? configureOptions = null)
    {
        var context = CreateContext();
        var clientId = $"headless-{Guid.NewGuid():N}";
        var redirectUri = $"https://client.example.test/{clientId}/callback";

        var optionsValue = new SqlOSAuthServerOptions
        {
            Issuer = AspireFixture.Options.Issuer,
            BasePath = AspireFixture.Options.BasePath
        };
        optionsValue.SeedBrowserClient(clientId, $"Headless Test {clientId}", redirectUri);
        optionsValue.ClientSeeds[0].AllowNativeHeadlessAuth = allowNativeHeadlessAuth;
        optionsValue.SeedAuthPage(page =>
        {
            page.EnabledCredentialTypes = ["password", "email_otp"];
            page.EnablePasswordSignup = true;
        });
        optionsValue.UseHeadlessAuthPage(headless =>
        {
            headless.BuildUiUrl = ctx =>
                $"https://app.example.test/authorize?request={Uri.EscapeDataString(ctx.RequestId ?? string.Empty)}&view={Uri.EscapeDataString(ctx.View)}";
        });
        configureHeadless?.Invoke(optionsValue.Headless);
        configureOptions?.Invoke(optionsValue);

        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender { IsConfigured = true };
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);
        var transactionalEmailService = new SqlOSTransactionalEmailService(
            context,
            crypto,
            emailSender,
            new SqlOSEmailTemplateRenderer(),
            Options.Create(new SqlOSEmailOptions()));
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options, transactionalEmailService);
        var invitationService = new SqlOSInvitationService(context, admin, crypto, emailSender, settings, options, transactionalEmailService);
        var passwordAbuse = new SqlOSPasswordLoginAbuseService(context, admin, crypto, options);
        var authService = new SqlOSAuthService(
            context,
            options,
            admin,
            crypto,
            settings,
            emailOtp,
            invitationService,
            passwordAbuse,
            transactionalEmailService);
        var authorizationServerService = new SqlOSAuthorizationServerService(
            context,
            admin,
            authService,
            crypto,
            settings,
            authPageSessionService,
            options,
            invitationService,
            passwordAbuse);
        var discovery = new SqlOSHomeRealmDiscoveryService(context);
        var oidcAuthService = new SqlOSOidcAuthService(
            context,
            admin,
            crypto,
            new FakeOidcProviderHttpClientFactory(),
            NullLogger<SqlOSOidcAuthService>.Instance);
        var samlService = new SqlOSSamlService(context, options, admin, crypto, authorizationServerService);
        var oidcBrowserAuthService = new SqlOSOidcBrowserAuthService(
            context,
            admin,
            authService,
            authorizationServerService,
            crypto,
            oidcAuthService,
            options);
        var headlessAuthService = new SqlOSHeadlessAuthService(
            context,
            admin,
            authorizationServerService,
            discovery,
            oidcBrowserAuthService,
            samlService,
            settings,
            emailOtp,
            options,
            invitationService);

        await crypto.EnsureActiveSigningKeyAsync();
        await admin.UpsertSeededClientsAsync();
        await settings.UpsertSeededAuthPageSettingsAsync();
        await settings.UpsertSeededAuthEmailSettingsAsync();
        await new SqlOSEmailAdminService(context, crypto, new SqlOSEmailTemplateRenderer()).EnsureBuiltInTemplatesAsync();

        return new HeadlessFixture(context, clientId, redirectUri, admin, authService, authorizationServerService, headlessAuthService, authPageSessionService, emailSender, invitationService);
    }

    private static TestSqlOSDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(AspireFixture.SqlConnectionString)
            .Options;
        return new TestSqlOSDbContext(options);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("tests");
        return context;
    }

    private static DefaultHttpContext CreateHttpContext(string ipAddress)
    {
        var context = CreateHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        context.Request.Headers.UserAgent = "SqlOSTest";
        return context;
    }

    private static async Task<SqlOSOrganization> CreateSamlOrganizationAsync(HeadlessFixture fixture, string domain)
    {
        var organization = await fixture.AdminService.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"SSO {Guid.NewGuid():N}", null, domain));
        await fixture.AdminService.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            organization.Id,
            "Headless SSO",
            $"urn:headless:{Guid.NewGuid():N}",
            "https://idp.example.test/sso",
            CreateSamlCertificatePem(),
            true,
            true,
            "email",
            "first_name",
            "last_name"));
        return organization;
    }

    private static string CreateSamlCertificatePem()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=HeadlessAuthIntegrationIdP",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        return certificate.ExportCertificatePem();
    }

    private static string? ExtractCookieValue(string setCookieHeader, string cookieName)
    {
        var marker = $"{cookieName}=";
        var start = setCookieHeader.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = setCookieHeader.IndexOf(';', start);
        if (end < 0)
        {
            end = setCookieHeader.Length;
        }

        return setCookieHeader[start..end];
    }

    private static string ExtractInvitationToken(string inviteUrl)
    {
        var query = new Uri(inviteUrl).Query.TrimStart('?');
        var tokenPart = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .First(x => x.StartsWith("token=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(tokenPart["token=".Length..]);
    }

    private static string GetLatestCode(TestAuthEmailSender sender, string email)
    {
        var message = sender.Messages.Last(x => string.Equals(x.To, email, StringComparison.OrdinalIgnoreCase));
        return Regex.Match(message.TextBody ?? message.HtmlBody, @"\b\d{4,8}\b").Value;
    }

    private sealed record HeadlessFixture(
        TestSqlOSDbContext Context,
        string ClientId,
        string RedirectUri,
        SqlOSAdminService AdminService,
        SqlOSAuthService AuthService,
        SqlOSAuthorizationServerService AuthorizationServerService,
        SqlOSHeadlessAuthService HeadlessAuthService,
        SqlOSAuthPageSessionService AuthPageSessionService,
        TestAuthEmailSender EmailSender,
        SqlOSInvitationService InvitationService) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
        }
    }
}
