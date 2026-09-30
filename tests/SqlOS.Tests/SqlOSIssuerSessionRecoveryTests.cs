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
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// A revoked or cleaned-up issuer session cookie counts as signed out (#434). A sign-in that
/// rests on a credential replaces it with a new family; a sign-in that rests on the presented
/// session (silent reuse, device approval, their interstitials) still fails closed (#359).
/// </summary>
[TestClass]
public sealed class SqlOSIssuerSessionRecoveryTests
{
    private const string PkceChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string FirstPartyClientId = "recovery-web";
    private const string FirstPartyRedirect = "https://client.example.test/callback";
    private const string ThirdPartyClientId = "recovery-third-party";
    private const string ThirdPartyRedirect = "https://third.example.test/callback";
    private const string CliClientId = "recovery-cli";
    private const string CookiePrefix = "sqlos_auth_page=";

    [DataTestMethod]
    [DataRow(false, DisplayName = "revoked family, row still present")]
    [DataRow(true, DisplayName = "row deleted by cleanup")]
    public async Task CredentialSignIn_WithDeadCookie_IssuesCodeInNewFamily(bool cleanedUp)
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("fresh");
        var dead = await harness.CreateDeadCookieAsync(user, cleanedUp);
        var request = await harness.CreateRequestAsync(FirstPartyClientId, FirstPartyRedirect);
        var http = harness.CreateHttpContext(dead.Cookie);

        var completion = await harness.Authorization.CompleteCredentialSignInAsync(request, user, "password", http);

        completion.RedirectUrl.Should().Contain("code=");
        var issued = ReadIssuedCookie(http);
        issued.Should().NotBeNullOrWhiteSpace();
        issued.Should().NotBe(dead.Cookie);
        var family = await harness.FamilyOfAsync(issued!);
        family.Id.Should().NotBe(dead.FamilyId);
        family.UserId.Should().Be(user.Id);
        family.RevokedAt.Should().BeNull();
        await harness.AssertFamilyRevokedAsync(dead.FamilyId);
        (await harness.IssuerSession.TryGetSessionAsync(harness.CreateHttpContext(dead.Cookie))).Should().BeNull();
    }

    [TestMethod]
    public async Task SilentReuse_WithRevokedFamilyCookie_StillFailsClosed()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("silent");
        var dead = await harness.CreateDeadCookieAsync(user, cleanedUp: false);
        var request = await harness.CreateRequestAsync(FirstPartyClientId, FirstPartyRedirect);
        var http = harness.CreateHttpContext(dead.Cookie);

        var reuse = async () => await harness.Authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            http,
            knownAuthenticatedAt: DateTime.UtcNow.AddMinutes(-5));

        await reuse.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSIssuerSessionService.SessionNoLongerActiveMessage);
        ReadIssuedCookie(http).Should().BeNull();
        (await harness.Context.Set<SqlOSAuthorizationCode>().AnyAsync(x => x.AuthorizationRequestId == request.Id))
            .Should().BeFalse();
        (await harness.Context.Set<SqlOSIssuerSessionFamily>().CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task PresentedSession_WithCleanedUpCookie_BehavesLikeNoCookie()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("cleaned");
        var dead = await harness.CreateDeadCookieAsync(user, cleanedUp: true);
        var request = await harness.CreateRequestAsync(FirstPartyClientId, FirstPartyRedirect);
        var http = harness.CreateHttpContext(dead.Cookie);

        var completion = await harness.Authorization.CompleteAuthorizationRequestLoginAsync(request, user, "password", http);

        completion.RedirectUrl.Should().Contain("code=");
        var family = await harness.FamilyOfAsync(ReadIssuedCookie(http)!);
        family.Id.Should().NotBe(dead.FamilyId);
        await harness.AssertFamilyRevokedAsync(dead.FamilyId);
    }

    [TestMethod]
    public async Task Renewal_WithCleanedUpCookie_StartsNewFamilyAndNeverRevivesTheOldOne()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("renewal");
        var dead = await harness.CreateDeadCookieAsync(user, cleanedUp: true);
        var http = harness.CreateHttpContext(dead.Cookie);

        // No row means no presented session: a continueExistingSession renewal has nothing
        // to continue, so it starts a new family instead of failing or reviving the old one.
        await harness.IssuerSession.SignInAsync(
            http,
            user,
            organizationId: null,
            "password",
            authenticatedAt: null,
            continueExistingSession: true);

        var family = await harness.FamilyOfAsync(ReadIssuedCookie(http)!);
        family.Id.Should().NotBe(dead.FamilyId);
        family.RevokedAt.Should().BeNull();
        await harness.AssertFamilyRevokedAsync(dead.FamilyId);
    }

    [DataTestMethod]
    [DataRow(false, DisplayName = "revoked family, row still present")]
    [DataRow(true, DisplayName = "row deleted by cleanup")]
    public async Task CredentialSignIn_ByAnotherUser_WithDeadCookie_StartsThatUsersOwnFamily(bool cleanedUp)
    {
        await using var harness = await Harness.CreateAsync();
        var previous = await harness.CreateMemberAsync("previous");
        var next = await harness.CreateMemberAsync("next");
        var dead = await harness.CreateDeadCookieAsync(previous, cleanedUp);
        var request = await harness.CreateRequestAsync(FirstPartyClientId, FirstPartyRedirect);
        var http = harness.CreateHttpContext(dead.Cookie);

        var completion = await harness.Authorization.CompleteCredentialSignInAsync(request, next, "password", http);

        completion.RedirectUrl.Should().Contain("code=");
        var family = await harness.FamilyOfAsync(ReadIssuedCookie(http)!);
        family.Id.Should().NotBe(dead.FamilyId);
        family.UserId.Should().Be(next.Id);
        await harness.AssertFamilyRevokedAsync(dead.FamilyId);
    }

    [DataTestMethod]
    [DataRow(true, DisplayName = "credential sign-in replaces the dead cookie")]
    [DataRow(false, DisplayName = "silent reuse fails closed after revocation")]
    public async Task OrganizationSelection_KeepsTheEvidenceOfTheFlowThatReachedIt(bool credentialSignIn)
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("chooser");
        var secondOrganization = await harness.AddOrganizationAsync(user);
        var request = await harness.CreateRequestAsync(FirstPartyClientId, FirstPartyRedirect);
        var (cookie, completion) = await harness.StartInterstitialAsync(request, user, credentialSignIn);
        completion.RequiresOrganizationSelection.Should().BeTrue();

        var selection = async () => (await harness.Authorization.CompletePendingOrganizationSelectionForLoginAsync(
            completion.PendingToken!,
            secondOrganization,
            harness.CreateHttpContext(cookie))).RedirectUrl;

        await harness.AssertContinuationOutcomeAsync(selection, request, credentialSignIn);
    }

    [TestMethod]
    public async Task PublicPendingOrganizationSelection_KeepsThePresentedSessionCheck()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("public-pending");
        var secondOrganization = await harness.AddOrganizationAsync(user);
        var request = await harness.CreateRequestAsync(FirstPartyClientId, FirstPartyRedirect);
        var pendingToken = await harness.Authorization.CreatePendingOrganizationSelectionAsync(user, request, "password");
        var dead = await harness.CreateDeadCookieAsync(user, cleanedUp: false);

        var selection = async () => (await harness.Authorization.CompletePendingOrganizationSelectionForLoginAsync(
            pendingToken,
            secondOrganization,
            harness.CreateHttpContext(dead.Cookie))).RedirectUrl;

        await harness.AssertContinuationOutcomeAsync(selection, request, credentialSignIn: false);
    }

    [DataTestMethod]
    [DataRow(true, DisplayName = "credential sign-in replaces the dead cookie")]
    [DataRow(false, DisplayName = "silent reuse fails closed after revocation")]
    public async Task ConsentApproval_KeepsTheEvidenceOfTheFlowThatReachedIt(bool credentialSignIn)
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("consent");
        var request = await harness.CreateRequestAsync(ThirdPartyClientId, ThirdPartyRedirect);
        var (cookie, completion) = await harness.StartInterstitialAsync(request, user, credentialSignIn);
        completion.RequiresConsent.Should().BeTrue();

        var approval = async () => (await harness.Authorization.ApproveConsentAsync(
            completion.ConsentToken!,
            request.Id,
            harness.CreateHttpContext(cookie))).RedirectUrl;

        await harness.AssertContinuationOutcomeAsync(approval, request, credentialSignIn);
    }

    [DataTestMethod]
    [DataRow(true, DisplayName = "credential sign-in replaces the dead cookie")]
    [DataRow(false, DisplayName = "silent reuse fails closed after revocation")]
    public async Task MfaCompletion_KeepsTheEvidenceOfTheFlowThatReachedIt(bool credentialSignIn)
    {
        await using var harness = await Harness.CreateAsync(RequireAllUsersMfa);
        var user = await harness.CreateMemberAsync("mfa");
        var enrollment = await harness.Auth.StartTotpEnrollmentAsync(user.Id, new SqlOSTotpEnrollmentStartRequest());
        await harness.Auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
            enrollment.EnrollmentToken,
            harness.Totp.GenerateCodeForTesting(enrollment.Secret)));
        var request = await harness.CreateRequestAsync(FirstPartyClientId, FirstPartyRedirect);
        var (cookie, completion) = await harness.StartInterstitialAsync(request, user, credentialSignIn);
        completion.RequiresMfa.Should().BeTrue();

        var challengeCode = harness.Totp.GenerateCodeForTesting(
            enrollment.Secret,
            DateTimeOffset.UtcNow.AddSeconds(new SqlOSAuthServerOptions().Mfa.Totp.PeriodSeconds));
        var verification = async () => await harness.Authorization.CompleteMfaChallengeAsync(
            completion.MfaToken!,
            challengeCode,
            harness.CreateHttpContext(cookie));

        await harness.AssertContinuationOutcomeAsync(verification, request, credentialSignIn);
    }

    [DataTestMethod]
    [DataRow(false, DisplayName = "revoked family, row still present")]
    [DataRow(true, DisplayName = "row deleted by cleanup")]
    public async Task CredentialSignIn_ForDeviceRequest_WithDeadCookie_ResolvesApprovalInNewFamily(bool cleanedUp)
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("device");
        var dead = await harness.CreateDeadCookieAsync(user, cleanedUp);
        var deviceRequest = await harness.CreateDeviceRequestAsync();
        var http = harness.CreateHttpContext(dead.Cookie);

        var completion = await harness.Authorization.CompleteCredentialSignInAsync(deviceRequest, user, "password", http);

        completion.RedirectUrl.Should().Contain("/device/approve");
        deviceRequest.ResolvedAuthMethod.Should().Be("password");
        var issued = ReadIssuedCookie(http)!;
        (await harness.FamilyOfAsync(issued)).Id.Should().NotBe(dead.FamilyId);
        var approved = await harness.Device.ApproveAsync(deviceRequest, user, "password", harness.CreateHttpContext(issued));
        approved.Status.Should().Be(SqlOSDeviceAuthorizationService.ApprovedStatus);
    }

    [TestMethod]
    public async Task DeviceApproval_ResolvedFromRevokedPresentedSession_FailsClosed()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("device-reuse");
        var dead = await harness.CreateDeadCookieAsync(user, cleanedUp: false);
        var deviceRequest = await harness.CreateDeviceRequestAsync();
        var http = harness.CreateHttpContext(dead.Cookie);

        var reuse = async () => await harness.Authorization.CompleteAuthorizationRequestLoginAsync(deviceRequest, user, "password", http);

        await reuse.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSIssuerSessionService.SessionNoLongerActiveMessage);
        (await harness.Context.Set<SqlOSAuthorizationRequest>().AsNoTracking().SingleAsync(x => x.Id == deviceRequest.Id))
            .ResolvedAuthMethod.Should().BeNull();
        ReadIssuedCookie(http).Should().BeNull();
    }

    [TestMethod]
    public async Task ClearPresentedSessionCookie_DeletesOnlyAPresentedCookieAndOnlyOnce()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.CreateMemberAsync("clear");

        var anonymous = harness.CreateHttpContext(cookie: null);
        harness.IssuerSession.ClearPresentedSessionCookie(anonymous);
        anonymous.Response.Headers.SetCookie.Should().BeEmpty();

        var dead = await harness.CreateDeadCookieAsync(user, cleanedUp: true);
        var presented = harness.CreateHttpContext(dead.Cookie);
        harness.IssuerSession.ClearPresentedSessionCookie(presented);
        harness.IssuerSession.ClearPresentedSessionCookie(presented);
        await harness.IssuerSession.SignOutAsync(presented);
        presented.Response.Headers.SetCookie.Should().ContainSingle()
            .Which.Should().StartWith($"{CookiePrefix};").And.Contain("expires=Thu, 01 Jan 1970");
    }

    private static void RequireAllUsersMfa(SqlOSAuthServerOptions options)
    {
        options.Mfa.Enabled = true;
        options.Mfa.RequireForAllUsersByDefault = true;
        options.Mfa.AllowUserSelfEnrollmentByDefault = true;
        options.Mfa.RecoveryCodesEnabledByDefault = true;
    }

    private static string? ReadIssuedCookie(HttpContext httpContext)
        => httpContext.Response.Headers.SetCookie
            .Select(value => (value ?? string.Empty).Split(';', 2)[0])
            .Where(pair => pair.StartsWith(CookiePrefix, StringComparison.Ordinal))
            .Select(pair => pair[CookiePrefix.Length..])
            .LastOrDefault(value => value.Length > 0);

    private sealed record DeadCookie(string Cookie, string FamilyId);

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            TestSqlOSInMemoryDbContext context,
            SqlOSAdminService admin,
            SqlOSCryptoService crypto,
            SqlOSAuthService auth,
            SqlOSTotpMfaService totp,
            SqlOSIssuerSessionService issuerSession,
            SqlOSAuthorizationServerService authorization,
            SqlOSDeviceAuthorizationService device)
        {
            Context = context;
            Admin = admin;
            Crypto = crypto;
            Auth = auth;
            Totp = totp;
            IssuerSession = issuerSession;
            Authorization = authorization;
            Device = device;
        }

        public TestSqlOSInMemoryDbContext Context { get; }
        public SqlOSAdminService Admin { get; }
        public SqlOSCryptoService Crypto { get; }
        public SqlOSAuthService Auth { get; }
        public SqlOSTotpMfaService Totp { get; }
        public SqlOSIssuerSessionService IssuerSession { get; }
        public SqlOSAuthorizationServerService Authorization { get; }
        public SqlOSDeviceAuthorizationService Device { get; }
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
                Issuer = "https://auth.example.test/sqlos/auth"
            };
            authOptions.SeedBrowserClient(FirstPartyClientId, "Recovery Web", FirstPartyRedirect);
            authOptions.SeedClient(client =>
            {
                client.ClientId = ThirdPartyClientId;
                client.Name = "Recovery Third Party";
                client.RedirectUris = [ThirdPartyRedirect];
                client.ClientType = "public_pkce";
                client.RequirePkce = true;
                client.IsFirstParty = false;
                client.AllowedScopes = ["openid"];
            });
            authOptions.SeedCliClient(CliClientId, "Recovery CLI", "https://api.example.test", "openid");
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
            var issuerSession = new SqlOSIssuerSessionService(context, crypto, settings);
            var authorization = new SqlOSAuthorizationServerService(
                context,
                admin,
                auth,
                crypto,
                settings,
                issuerSession,
                options,
                mfaPolicyService: mfaPolicy,
                totpMfaService: totp,
                consentService: new SqlOSConsentService(context, crypto));
            var device = new SqlOSDeviceAuthorizationService(context, admin, auth, crypto, options, mfaPolicy, issuerSession);

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.EnsureDefaultSettingsAsync();
            await settings.EnsureDefaultAuthPageSettingsAsync();
            await settings.EnsureDefaultMfaSettingsAsync();
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Recovery Org", null));

            return new Harness(context, admin, crypto, auth, totp, issuerSession, authorization, device)
            {
                OrganizationId = organization.Id
            };
        }

        public DefaultHttpContext CreateHttpContext(string? cookie)
        {
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.test");
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");
            if (cookie != null)
            {
                http.Request.Headers.Cookie = $"{CookiePrefix}{cookie}";
            }

            return http;
        }

        public async Task<SqlOSUser> CreateMemberAsync(string prefix)
        {
            var user = await Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                $"{prefix} user",
                $"{prefix}-{Guid.NewGuid():N}@example.test",
                "P@ssword123!"));
            await Admin.CreateMembershipAsync(OrganizationId, new SqlOSCreateMembershipRequest(user.Id, "member"));
            return user;
        }

        public async Task<string> AddOrganizationAsync(SqlOSUser user)
        {
            var organization = await Admin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest($"Second {Guid.NewGuid():N}", null));
            await Admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
            return organization.Id;
        }

        public Task<SqlOSAuthorizationRequest> CreateRequestAsync(string clientId, string redirectUri)
            => Authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
                "code",
                clientId,
                redirectUri,
                Guid.NewGuid().ToString("N"),
                "openid",
                PkceChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "hosted",
                null));

        public async Task<SqlOSAuthorizationRequest> CreateDeviceRequestAsync()
        {
            var started = await Device.StartAsync(
                new SqlOSDeviceAuthorizationStartRequest(CliClientId, "openid"),
                CreateHttpContext(cookie: null));
            return await Device.CreateOrGetAuthorizationRequestAsync(started.UserCode, "hosted");
        }

        public async Task<string> SignInAsync(SqlOSUser user)
        {
            var http = CreateHttpContext(cookie: null);
            await IssuerSession.SignInAsync(http, user, OrganizationId, "password");
            return ReadIssuedCookie(http)!;
        }

        /// <summary>
        /// Signs the user in, then revokes the family the way logout does. With
        /// <paramref name="cleanedUp"/>, startup cleanup also deletes the consumed row.
        /// </summary>
        public async Task<DeadCookie> CreateDeadCookieAsync(SqlOSUser user, bool cleanedUp)
        {
            var cookie = await SignInAsync(user);
            var family = await FamilyOfAsync(cookie);
            await IssuerSession.SignOutAsync(CreateHttpContext(cookie));
            if (cleanedUp)
            {
                await Admin.CleanupExpiredTemporaryTokensAsync();
                (await Context.Set<SqlOSTemporaryToken>()
                        .AnyAsync(x => x.TokenHash == Crypto.HashToken(cookie)))
                    .Should().BeFalse();
            }

            return new DeadCookie(cookie, family.Id);
        }

        /// <summary>
        /// Reaches an interstitial either from a credential sign-in presenting a dead cookie, or
        /// from silent reuse of a live cookie whose family is then revoked (a logout elsewhere).
        /// Returns the cookie the browser presents when it continues.
        /// </summary>
        public async Task<(string Cookie, SqlOSAuthorizationRequestLoginResult Completion)> StartInterstitialAsync(
            SqlOSAuthorizationRequest request,
            SqlOSUser user,
            bool credentialSignIn)
        {
            if (credentialSignIn)
            {
                var dead = await CreateDeadCookieAsync(user, cleanedUp: false);
                var completion = await Authorization.CompleteCredentialSignInAsync(
                    request,
                    user,
                    "password",
                    CreateHttpContext(dead.Cookie));
                return (dead.Cookie, completion);
            }

            var live = await SignInAsync(user);
            var reused = await Authorization.CompleteAuthorizationRequestLoginAsync(
                request,
                user,
                "password",
                CreateHttpContext(live),
                knownAuthenticatedAt: DateTime.UtcNow.AddMinutes(-1));
            await IssuerSession.SignOutAsync(CreateHttpContext(live));
            return (live, reused);
        }

        public async Task AssertContinuationOutcomeAsync(
            Func<Task<string?>> continuation,
            SqlOSAuthorizationRequest request,
            bool credentialSignIn)
        {
            if (credentialSignIn)
            {
                var redirect = await continuation();
                redirect.Should().Contain("code=");
                (await Context.Set<SqlOSAuthorizationCode>().AnyAsync(x => x.AuthorizationRequestId == request.Id))
                    .Should().BeTrue();
                (await Context.Set<SqlOSIssuerSessionFamily>().CountAsync(x => x.RevokedAt == null))
                    .Should().Be(1);
                return;
            }

            await continuation.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(SqlOSIssuerSessionService.SessionNoLongerActiveMessage);
            (await Context.Set<SqlOSAuthorizationCode>()
                    .AnyAsync(x => x.AuthorizationRequestId == request.Id && x.ConsumedAt == null))
                .Should().BeFalse();
            (await Context.Set<SqlOSIssuerSessionFamily>().CountAsync(x => x.RevokedAt == null))
                .Should().Be(0);
        }

        public async Task<SqlOSIssuerSessionFamily> FamilyOfAsync(string cookie)
        {
            var hash = Crypto.HashToken(cookie);
            var token = await Context.Set<SqlOSTemporaryToken>()
                .AsNoTracking()
                .SingleAsync(x => x.Purpose == SqlOSAuthLifecyclePolicy.IssuerSessionPurpose && x.TokenHash == hash);
            return await Context.Set<SqlOSIssuerSessionFamily>()
                .AsNoTracking()
                .SingleAsync(x => x.Id == token.IssuerSessionFamilyId);
        }

        public async Task AssertFamilyRevokedAsync(string familyId)
            => (await Context.Set<SqlOSIssuerSessionFamily>().AsNoTracking().SingleAsync(x => x.Id == familyId))
                .RevokedAt.Should().NotBeNull();

        public async ValueTask DisposeAsync() => await Context.DisposeAsync();
    }
}
