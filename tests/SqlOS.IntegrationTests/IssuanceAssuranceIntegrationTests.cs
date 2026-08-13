using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Security;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class IssuanceAssuranceIntegrationTests
{
    private const string PkceChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task SqlServer_OidcAndSignupPrimaryFactors_CannotIssueWhenOrganizationRequiresMfa()
    {
        await using var fixture = await Fixture.CreateAsync("IssuanceOidcSignup");
        var organization = await fixture.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Issuance MFA Org", null));
        await fixture.RequireOrganizationMfaAsync(organization.Id);

        var oidcUser = await fixture.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "OIDC Bypass",
            $"oidc-bypass-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        await fixture.Admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(oidcUser.Id, "member"));
        var oidcRequest = await fixture.CreateAuthorizationRequestAsync(oidcUser.DefaultEmail);
        oidcRequest.OrganizationId = organization.Id;
        await fixture.Context.SaveChangesAsync();

        var oidcCompletion = await fixture.Authorization.CompleteAuthorizationRequestLoginAsync(
            oidcRequest,
            oidcUser,
            "oidc",
            fixture.Http);

        oidcCompletion.RequiresMfa.Should().BeTrue();
        oidcCompletion.RedirectUrl.Should().BeNull();
        (await fixture.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == oidcRequest.Id)).Should().Be(0);

        var signup = await fixture.Authorization.SignUpAsync(
            "Signup Bypass",
            $"signup-bypass-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            null,
            null);
        await fixture.Admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(signup.User.Id, "member"));
        var signupRequest = await fixture.CreateAuthorizationRequestAsync(signup.User.DefaultEmail, "signup-bypass");
        signupRequest.OrganizationId = organization.Id;
        await fixture.Context.SaveChangesAsync();

        var signupCompletion = await fixture.Authorization.CompleteAuthorizationRequestLoginAsync(
            signupRequest,
            signup.User,
            signup.AuthenticationMethod,
            fixture.Http);

        signupCompletion.RequiresMfa.Should().BeTrue();
        (await fixture.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == signupRequest.Id)).Should().Be(0);
        (await fixture.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task SqlServer_TrustedUpstreamMfa_IssuesAuthorizationCode()
    {
        await using var fixture = await Fixture.CreateAsync("IssuanceUpstream");
        var organization = await fixture.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Upstream MFA Org", null));
        await fixture.RequireOrganizationMfaAsync(organization.Id);
        var user = await fixture.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Upstream User",
            $"upstream-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        await fixture.Admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        var request = await fixture.CreateAuthorizationRequestAsync(user.DefaultEmail);
        request.OrganizationId = organization.Id;
        await fixture.Context.SaveChangesAsync();

        var completion = await fixture.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            SqlOSMfaPolicyService.AddAuthenticationMethod("saml", SqlOSUpstreamMfaTrust.AuthenticationMethod),
            fixture.Http);

        completion.RequiresMfa.Should().BeFalse();
        completion.RedirectUrl.Should().Contain("code=");
        (await fixture.Context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == request.Id)).Should().Be(1);
    }

    [TestMethod]
    public async Task SqlServer_DevicePollAfterPolicyChange_DoesNotIssueTokens()
    {
        await using var fixture = await Fixture.CreateAsync("IssuanceDevicePolicy");
        var organization = await fixture.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Device Policy Org", null));
        var user = await fixture.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Device Policy User",
            $"device-policy-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        await fixture.Admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        var start = await fixture.Device.StartAsync(
            new SqlOSDeviceAuthorizationStartRequest("todo-cli", "openid", "https://api.example.com/todos"),
            fixture.Http);
        await fixture.Device.ApproveAsync(
            new SqlOSDeviceAuthorizationApprovalRequest(start.UserCode, organization.Id),
            user,
            "password",
            fixture.Http);

        await fixture.RequireOrganizationMfaAsync(organization.Id);

        var poll = await Assert.ThrowsExceptionAsync<SqlOSDeviceAuthorizationException>(() =>
            fixture.Device.PollAsync(
                new SqlOSDeviceTokenPollRequest("todo-cli", start.DeviceCode, "https://api.example.com/todos"),
                fixture.Http));
        poll.Error.Should().Be("authorization_pending");
        (await fixture.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await fixture.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
        (await fixture.Context.Set<SqlOSDeviceAuthorization>().SingleAsync()).ApprovedAt.Should().BeNull();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestSqlOSDbContext Context { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSAuthorizationServerService Authorization { get; init; }
        public required SqlOSDeviceAuthorizationService Device { get; init; }
        public required SqlOSSettingsService Settings { get; init; }
        public required DefaultHttpContext Http { get; init; }

        public static async Task<Fixture> CreateAsync(string databasePrefix)
        {
            var context = await AspireFixture.CreateIsolatedAuthContextAsync(databasePrefix);
            var optionsValue = new SqlOSAuthServerOptions
            {
                Issuer = AspireFixture.Options.Issuer,
                BasePath = AspireFixture.Options.BasePath,
                PublicOrigin = "https://auth.example.test",
                DefaultAudience = "https://api.example.com/todos"
            };
            optionsValue.ResourceIndicators.Enabled = true;
            optionsValue.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            optionsValue.SeedCliClient("todo-cli", "Todo CLI", "https://api.example.com/todos", "openid");
            optionsValue.Mfa.Enabled = true;
            optionsValue.Mfa.AllowUserSelfEnrollmentByDefault = true;
            optionsValue.Mfa.RecoveryCodesEnabledByDefault = true;
            var options = Microsoft.Extensions.Options.Options.Create(optionsValue);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);
            var sender = new TestAuthEmailSender { IsConfigured = true };
            var settings = new SqlOSSettingsService(context, options, sender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, sender, options);
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

            await admin.UpsertSeededClientsAsync();
            await settings.EnsureDefaultSettingsAsync();
            await crypto.EnsureActiveSigningKeyAsync();

            return new Fixture
            {
                Context = context,
                Admin = admin,
                Authorization = authorization,
                Device = device,
                Settings = settings,
                Http = http
            };
        }

        public async Task RequireOrganizationMfaAsync(string organizationId)
            => await Settings.UpdateOrganizationMfaPolicyAsync(
                organizationId,
                new SqlOSUpdateOrganizationMfaPolicyRequest(
                    IsEnabled: true,
                    RequireMfaForAllUsers: true,
                    RequireMfaForOwnersAndAdmins: false,
                    UserSelfEnrollmentEnabled: true,
                    RecoveryCodesEnabled: true,
                    RequiredRoles: ["owner", "admin"],
                    AvailableFactors: [SqlOSMfaFactorTypes.Totp, SqlOSMfaFactorTypes.RecoveryCode]));

        public async Task<SqlOSAuthorizationRequest> CreateAuthorizationRequestAsync(string? loginHint, string? state = null)
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
                null));

        public async ValueTask DisposeAsync()
        {
            await Context.Database.EnsureDeletedAsync();
            await Context.DisposeAsync();
        }
    }
}
