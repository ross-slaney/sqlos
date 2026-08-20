using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Security;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSIssuanceAssuranceTests
{
    private const string PkceChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task IssueAuthorizationRedirectAsync_WhenMfaRequired_DoesNotPersistCodeOrSession()
    {
        await using var harness = await Harness.CreateAsync(RequireAllUsersMfa);
        var user = await harness.CreateMemberAsync("password-bypass");
        var request = await harness.CreateAuthorizationRequestAsync(user.DefaultEmail);

        var act = async () => await harness.Authorization.IssueAuthorizationRedirectAsync(
            request,
            user,
            harness.OrganizationId,
            "password",
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSMfaPolicyService.UnsatisfiedPolicyMessage);
        await AssertNoIssuanceAsync(harness, request.Id, user.Id);
    }

    [TestMethod]
    [DataRow("password")]
    [DataRow("invitation")]
    [DataRow("email_otp")]
    [DataRow("oidc")]
    [DataRow("saml")]
    public async Task CompleteAuthorizationRequestLoginAsync_PrimaryMethods_CannotIssueWhenMfaRequired(string authenticationMethod)
    {
        await using var harness = await Harness.CreateAsync(RequireAllUsersMfa);
        var user = await harness.CreateMemberAsync(authenticationMethod);
        var request = await harness.CreateAuthorizationRequestAsync(user.DefaultEmail);
        request.OrganizationId = harness.OrganizationId;
        await harness.Context.SaveChangesAsync();

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            authenticationMethod,
            harness.Http);

        completion.RequiresMfa.Should().BeTrue();
        completion.RedirectUrl.Should().BeNull();
        completion.MfaToken.Should().NotBeNullOrWhiteSpace();
        await AssertNoIssuanceAsync(harness, request.Id, user.Id);
    }

    [TestMethod]
    public async Task CompleteAuthorizationRequestLoginAsync_PhoneOtp_DoesNotSatisfyMfaUnlessConfigured()
    {
        await using var harness = await Harness.CreateAsync(RequireAllUsersMfa);
        var user = await harness.CreateMemberAsync("phone");
        var request = await harness.CreateAuthorizationRequestAsync(user.DefaultEmail);
        request.OrganizationId = harness.OrganizationId;
        await harness.Context.SaveChangesAsync();

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "phone_otp",
            harness.Http);

        completion.RequiresMfa.Should().BeTrue();
        await AssertNoIssuanceAsync(harness, request.Id, user.Id);
    }

    [TestMethod]
    public async Task CompleteAuthorizationRequestLoginAsync_PhoneOtp_SatisfiesMfaWhenConfigured()
    {
        await using var harness = await Harness.CreateAsync(options =>
        {
            RequireAllUsersMfa(options);
            options.PhoneOtp.SatisfiesMfa = true;
        });
        var user = await harness.CreateMemberAsync("phone-strong");
        var request = await harness.CreateAuthorizationRequestAsync(user.DefaultEmail);
        request.OrganizationId = harness.OrganizationId;
        await harness.Context.SaveChangesAsync();

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "phone_otp",
            harness.Http);

        completion.RequiresMfa.Should().BeFalse();
        completion.RedirectUrl.Should().Contain("code=");
        (await harness.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == request.Id)).Should().Be(1);
    }

    [TestMethod]
    public async Task CompleteAuthorizationRequestLoginAsync_TrustedUpstreamMfa_IssuesCode()
    {
        await using var harness = await Harness.CreateAsync(RequireAllUsersMfa);
        var user = await harness.CreateMemberAsync("upstream");
        var request = await harness.CreateAuthorizationRequestAsync(user.DefaultEmail);
        request.OrganizationId = harness.OrganizationId;
        await harness.Context.SaveChangesAsync();

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            SqlOSMfaPolicyService.AddAuthenticationMethod("oidc", SqlOSUpstreamMfaTrust.AuthenticationMethod),
            harness.Http);

        completion.RequiresMfa.Should().BeFalse();
        completion.RedirectUrl.Should().Contain("code=");
        var code = await harness.Context.Set<SqlOSAuthorizationCode>().SingleAsync(x => x.AuthorizationRequestId == request.Id);
        code.AuthenticationMethod.Should().Contain(SqlOSUpstreamMfaTrust.AuthenticationMethod);
    }

    [TestMethod]
    public async Task IssueAuthorizationRedirectAsync_ReevaluatesPolicyChangeAfterPrimaryAuthentication()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("policy-change");
        var request = await harness.CreateAuthorizationRequestAsync(user.DefaultEmail);
        var first = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);
        first.RequiresMfa.Should().BeFalse();
        first.RedirectUrl.Should().Contain("code=");

        var secondRequest = await harness.CreateAuthorizationRequestAsync(user.DefaultEmail, "policy-change-2");
        await harness.Settings.UpdateOrganizationMfaPolicyAsync(
            harness.OrganizationId,
            new SqlOSUpdateOrganizationMfaPolicyRequest(
                IsEnabled: true,
                RequireMfaForAllUsers: true,
                RequireMfaForOwnersAndAdmins: false,
                UserSelfEnrollmentEnabled: true,
                RecoveryCodesEnabled: true,
                RequiredRoles: ["owner", "admin"],
                AvailableFactors: [SqlOSMfaFactorTypes.Totp, SqlOSMfaFactorTypes.RecoveryCode]));

        var act = async () => await harness.Authorization.IssueAuthorizationRedirectAsync(
            secondRequest,
            user,
            harness.OrganizationId,
            "password",
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSMfaPolicyService.UnsatisfiedPolicyMessage);
        (await harness.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == secondRequest.Id)).Should().Be(0);
        (await harness.Context.Set<SqlOSTemporaryToken>()
            .CountAsync(x => x.Purpose == SqlOSAuthLifecyclePolicy.AuthPageSessionPurpose && x.UserId == user.Id))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task HostedSignupVariants_WhenOrganizationRequiresMfa_DoNotIssue()
    {
        await using var harness = await Harness.CreateAsync(RequireAllUsersMfa);

        foreach (var authenticationMethod in new[] { "password", "invitation", "email_otp", "phone_otp" })
        {
            var user = await harness.CreateMemberAsync(authenticationMethod);
            var request = await harness.CreateAuthorizationRequestAsync(user.DefaultEmail, authenticationMethod);
            request.OrganizationId = harness.OrganizationId;
            await harness.Context.SaveChangesAsync();

            var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
                request,
                user,
                authenticationMethod,
                harness.Http);

            completion.RequiresMfa.Should().BeTrue($"signup variant {authenticationMethod} must not skip MFA");
            completion.RedirectUrl.Should().BeNull();
            (await harness.Context.Set<SqlOSAuthorizationCode>()
                .CountAsync(x => x.AuthorizationRequestId == request.Id)).Should().Be(0);
            (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
        }
    }

    [TestMethod]
    public async Task SignUpAsync_WhenOrganizationRequiresMfa_CompleteLoginDoesNotIssue()
    {
        await using var harness = await Harness.CreateAsync(RequireAllUsersMfa);
        var signup = await harness.Authorization.SignUpAsync(
            "Signup user",
            $"signup-{Guid.NewGuid():N}@example.test",
            "P@ssword123!",
            null,
            null);
        await harness.Admin.CreateMembershipAsync(
            harness.OrganizationId,
            new SqlOSCreateMembershipRequest(signup.User.Id, "member"));
        var request = await harness.CreateAuthorizationRequestAsync(signup.User.DefaultEmail, "signup");
        request.OrganizationId = harness.OrganizationId;
        await harness.Context.SaveChangesAsync();

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            signup.User,
            signup.AuthenticationMethod,
            harness.Http);

        completion.RequiresMfa.Should().BeTrue();
        await AssertNoIssuanceAsync(harness, request.Id, signup.User.Id);
    }

    [TestMethod]
    public async Task CreateSessionTokensForUserAsync_WhenMfaRequired_DoesNotPersistSessionOrRefreshToken()
    {
        await using var harness = await Harness.CreateAsync(RequireAllUsersMfa);
        var user = await harness.CreateMemberAsync("token-bypass");
        var client = await harness.RequireClientAsync();

        var act = async () => await harness.Auth.CreateSessionTokensForUserAsync(
            user,
            client,
            harness.OrganizationId,
            "password",
            "agent",
            "127.0.0.1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSMfaPolicyService.UnsatisfiedPolicyMessage);
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
        (await harness.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task CompleteClientAuthenticationAsync_WhenMfaRequired_ReturnsChallengeWithoutTokens()
    {
        await using var harness = await Harness.CreateAsync(RequireAllUsersMfa);
        var user = await harness.CreateMemberAsync("client-bypass");
        var client = await harness.RequireClientAsync();

        var result = await harness.Auth.CompleteClientAuthenticationAsync(
            user,
            client,
            harness.OrganizationId,
            "demo_switch",
            harness.Http);

        result.RequiresMfa.Should().BeTrue();
        result.Tokens.Should().BeNull();
        result.MfaToken.Should().NotBeNullOrWhiteSpace();
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
        (await harness.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task DeviceApproveAndPoll_WhenMfaRequired_DoNotIssueTokensOrApproval()
    {
        await using var harness = await Harness.CreateAsync(RequireAllUsersMfa);
        var user = await harness.CreateMemberAsync("device");
        var start = await harness.Device.StartAsync(
            new SqlOSDeviceAuthorizationStartRequest("todo-cli", "openid", "https://api.example.com/todos"),
            harness.Http);

        var approve = async () => await harness.Device.ApproveAsync(
            new SqlOSDeviceAuthorizationApprovalRequest(start.UserCode),
            user,
            "password",
            harness.Http);

        await approve.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSMfaPolicyService.UnsatisfiedPolicyMessage);
        var stored = await harness.Context.Set<SqlOSDeviceAuthorization>().SingleAsync();
        stored.Status.Should().Be(SqlOSDeviceAuthorizationService.PendingStatus);
        stored.ApprovedAt.Should().BeNull();

        var poll = await Assert.ThrowsExceptionAsync<SqlOSDeviceAuthorizationException>(() =>
            harness.Device.PollAsync(
                new SqlOSDeviceTokenPollRequest("todo-cli", start.DeviceCode, "https://api.example.com/todos"),
                harness.Http));
        poll.Error.Should().Be("authorization_pending");
        (await harness.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task DevicePoll_WhenPolicyRequiresMfaAfterApproval_DoesNotIssueTokens()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("device-policy");
        var start = await harness.Device.StartAsync(
            new SqlOSDeviceAuthorizationStartRequest("todo-cli", "openid", "https://api.example.com/todos"),
            harness.Http);
        var bound = await harness.Device.CreateOrGetAuthorizationRequestAsync(start.UserCode, "hosted");
        bound.OrganizationId = harness.OrganizationId;
        await harness.Context.SaveChangesAsync();
        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            bound,
            user,
            "password",
            harness.Http);
        completion.RedirectUrl.Should().Contain("/device/approve");
        await harness.Device.ApproveAsync(bound, user, "password", harness.Http);

        await harness.Settings.UpdateOrganizationMfaPolicyAsync(
            harness.OrganizationId,
            new SqlOSUpdateOrganizationMfaPolicyRequest(
                IsEnabled: true,
                RequireMfaForAllUsers: true,
                RequireMfaForOwnersAndAdmins: false,
                UserSelfEnrollmentEnabled: true,
                RecoveryCodesEnabled: true,
                RequiredRoles: ["owner", "admin"],
                AvailableFactors: [SqlOSMfaFactorTypes.Totp, SqlOSMfaFactorTypes.RecoveryCode]));

        var poll = await Assert.ThrowsExceptionAsync<SqlOSDeviceAuthorizationException>(() =>
            harness.Device.PollAsync(
                new SqlOSDeviceTokenPollRequest("todo-cli", start.DeviceCode, "https://api.example.com/todos"),
                harness.Http));
        poll.Error.Should().Be("authorization_pending");
        var stored = await harness.Context.Set<SqlOSDeviceAuthorization>().SingleAsync();
        stored.Status.Should().Be(SqlOSDeviceAuthorizationService.PendingStatus);
        stored.ApprovedAt.Should().BeNull();
        (await harness.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);

        var rebound = await harness.Device.CreateOrGetAuthorizationRequestAsync(start.UserCode, "hosted");
        rebound.Id.Should().Be(bound.Id);
        rebound.CompletedAt.Should().BeNull();
    }

    [TestMethod]
    public async Task RefreshAsync_WhenOrganizationRequiresMfa_DoesNotRotateTokens()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("refresh-mfa");
        var client = await harness.RequireClientAsync();
        var tokens = await harness.Auth.CreateSessionTokensForUserAsync(
            user,
            client,
            harness.OrganizationId,
            "password",
            "agent",
            "127.0.0.1");

        await harness.Settings.UpdateOrganizationMfaPolicyAsync(
            harness.OrganizationId,
            new SqlOSUpdateOrganizationMfaPolicyRequest(
                IsEnabled: true,
                RequireMfaForAllUsers: true,
                RequireMfaForOwnersAndAdmins: false,
                UserSelfEnrollmentEnabled: true,
                RecoveryCodesEnabled: true,
                RequiredRoles: ["owner", "admin"],
                AvailableFactors: [SqlOSMfaFactorTypes.Totp, SqlOSMfaFactorTypes.RecoveryCode]));

        var act = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(tokens.RefreshToken, harness.OrganizationId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSMfaPolicyService.UnsatisfiedPolicyMessage);
        (await harness.Context.Set<SqlOSRefreshToken>().CountAsync(x => x.ConsumedAt != null)).Should().Be(0);
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(1);
    }

    [TestMethod]
    public async Task ContinuationRedirect_DoesNotPutMfaTokenInUrl()
    {
        await using var harness = await Harness.CreateAsync(RequireAllUsersMfa);
        var user = await harness.CreateMemberAsync("continue-url");
        var request = await harness.CreateAuthorizationRequestAsync(user.DefaultEmail);
        request.OrganizationId = harness.OrganizationId;
        await harness.Context.SaveChangesAsync();

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);
        completion.RequiresMfa.Should().BeTrue();

        var redirect = await harness.Authorization.CreateAuthorizationContinuationRedirectAsync(
            completion,
            harness.Http);
        redirect.Should().NotContain("mfa_token");
        redirect.Should().Contain("/continue");
        redirect.Should().Contain(request.Id);
        harness.Http.Response.Headers.SetCookie.ToString().Should().Contain("sqlos_auth_continue");
        harness.Http.Response.Headers.SetCookie.ToString().Should().NotContain(completion.MfaToken);
    }

    [TestMethod]
    public async Task IssueAuthorizationRedirectAsync_WhenAuthenticationOlderThanMaxAge_Throws()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("max-age");
        var request = await harness.CreateAuthorizationRequestAsync(user.DefaultEmail, maxAge: "0");
        request.MaxAgeSeconds.Should().Be(0);

        var act = async () => await harness.Authorization.IssueAuthorizationRedirectAsync(
            request,
            user,
            harness.OrganizationId,
            "password",
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authentication is older than the requested max_age.");
        (await harness.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == request.Id)).Should().Be(0);
    }

    [TestMethod]
    public async Task CompletePendingOrganizationSelection_UsesPendingCreationAuthTime_NotSelectionTime()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("pending-authtime");
        var secondOrganization = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Second Issuance Org", null));
        await harness.Admin.CreateMembershipAsync(
            secondOrganization.Id,
            new SqlOSCreateMembershipRequest(user.Id, "member"));
        var request = await harness.CreateAuthorizationRequestAsync(user.DefaultEmail);

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            harness.Http);
        completion.RequiresOrganizationSelection.Should().BeTrue();
        completion.PendingToken.Should().NotBeNullOrWhiteSpace();

        // Simulate the user parking on the organization chooser: rewrite the
        // pending payload's stamped authentication time to half an hour ago. The
        // minted code must carry that moment, not the selection-click time.
        var originalAuthenticatedAt = DateTime.UtcNow.AddMinutes(-30);
        var pending = await harness.Context.Set<SqlOSTemporaryToken>()
            .SingleAsync(x => x.Purpose == "auth_page_pending" && x.UserId == user.Id);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(pending.PayloadJson!)!.AsObject();
        payload["AuthenticatedAt"] = originalAuthenticatedAt.ToString("O");
        pending.PayloadJson = payload.ToJsonString();
        await harness.Context.SaveChangesAsync();

        var result = await harness.Authorization.CompletePendingOrganizationSelectionForLoginAsync(
            completion.PendingToken!,
            harness.OrganizationId,
            harness.Http);

        result.RedirectUrl.Should().Contain("code=");
        var code = await harness.Context.Set<SqlOSAuthorizationCode>()
            .SingleAsync(x => x.AuthorizationRequestId == request.Id);
        code.AuthTime.Should().NotBeNull();
        code.AuthTime!.Value.Should().BeCloseTo(originalAuthenticatedAt, TimeSpan.FromSeconds(1));
    }

    private static void RequireAllUsersMfa(SqlOSAuthServerOptions options)
    {
        options.Mfa.Enabled = true;
        options.Mfa.RequireForAllUsersByDefault = true;
        options.Mfa.AllowUserSelfEnrollmentByDefault = true;
        options.Mfa.RecoveryCodesEnabledByDefault = true;
    }

    private static async Task AssertNoIssuanceAsync(Harness harness, string authorizationRequestId, string userId)
    {
        (await harness.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == authorizationRequestId)).Should().Be(0);
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == userId)).Should().Be(0);
        (await harness.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
        (await harness.Context.Set<SqlOSDeviceAuthorization>().CountAsync(x => x.ApprovedAt != null)).Should().Be(0);
        (await harness.Context.Set<SqlOSTemporaryToken>()
            .CountAsync(x => x.Purpose == SqlOSAuthLifecyclePolicy.AuthPageSessionPurpose && x.UserId == userId))
            .Should().Be(0);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            TestSqlOSInMemoryDbContext context,
            SqlOSAuthService auth,
            SqlOSAuthorizationServerService authorization,
            SqlOSDeviceAuthorizationService device,
            SqlOSSettingsService settings,
            SqlOSAdminService admin,
            DefaultHttpContext http)
        {
            Context = context;
            Auth = auth;
            Authorization = authorization;
            Device = device;
            Settings = settings;
            Admin = admin;
            Http = http;
        }

        public TestSqlOSInMemoryDbContext Context { get; }
        public SqlOSAuthService Auth { get; }
        public SqlOSAuthorizationServerService Authorization { get; }
        public SqlOSDeviceAuthorizationService Device { get; }
        public SqlOSSettingsService Settings { get; }
        public SqlOSAdminService Admin { get; }
        public DefaultHttpContext Http { get; }
        public string OrganizationId { get; private set; } = null!;

        public static async Task<Harness> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var authOptions = new SqlOSAuthServerOptions
            {
                PublicOrigin = "https://auth.example.test",
                Issuer = "https://auth.example.test/sqlos/auth",
                DefaultAudience = "https://api.example.com/todos"
            };
            authOptions.ResourceIndicators.Enabled = true;
            authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            authOptions.SeedCliClient("todo-cli", "Todo CLI", "https://api.example.com/todos", "openid");
            configure?.Invoke(authOptions);
            var options = Options.Create(authOptions);
            var crypto = TestCryptoService.Create(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var mfaPolicy = new SqlOSMfaPolicyService(context, settings, options);
            var totp = new SqlOSTotpMfaService(context, crypto, mfaPolicy, options);
            var auth = new SqlOSAuthService(
                context,
                options,
                admin,
                crypto,
                settings,
                emailOtp,
                mfaPolicyService: mfaPolicy,
                totpMfaService: totp);
            var authPage = new SqlOSAuthPageSessionService(context, crypto, settings);
            var authorization = new SqlOSAuthorizationServerService(
                context,
                admin,
                auth,
                crypto,
                settings,
                authPage,
                options,
                mfaPolicyService: mfaPolicy,
                totpMfaService: totp);
            var device = new SqlOSDeviceAuthorizationService(context, admin, auth, crypto, options, mfaPolicy);
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.test");
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.EnsureDefaultSettingsAsync();
            await settings.EnsureDefaultAuthPageSettingsAsync();
            await settings.UpsertSeededAuthPageSettingsAsync();
            await settings.EnsureDefaultMfaSettingsAsync();
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Issuance Org", null));

            return new Harness(context, auth, authorization, device, settings, admin, http)
            {
                OrganizationId = organization.Id
            };
        }

        public Task<SqlOSClientApplication> RequireClientAsync()
            => Context.Set<SqlOSClientApplication>().FirstAsync(x => x.ClientId == "test-client");

        public async Task<SqlOSUser> CreateMemberAsync(string prefix)
        {
            var user = await Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                $"{prefix} user",
                $"{prefix}-{Guid.NewGuid():N}@example.test",
                "P@ssword123!"));
            await Admin.CreateMembershipAsync(OrganizationId, new SqlOSCreateMembershipRequest(user.Id, "member"));
            return user;
        }

        public async Task<SqlOSAuthorizationRequest> CreateAuthorizationRequestAsync(string? loginHint, string? state = null, string? maxAge = null)
            => await Authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
                "code",
                "test-client",
                "https://client.example.test/callback",
                state ?? Guid.NewGuid().ToString("N"),
                "openid",
                PkceChallenge,
                "S256",
                null,
                loginHint,
                null,
                null,
                "hosted",
                null,
                maxAge));

        public async ValueTask DisposeAsync() => await Context.DisposeAsync();
    }
}
