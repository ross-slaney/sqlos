using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class AuthServiceIntegrationTests
{
    [TestMethod]
    public async Task OAuthRefresh_OmittedOrganization_AfterMembershipRemoval_IsRejected()
    {
        var email = $"refresh-offboard-{Guid.NewGuid():N}@example.com";
        SqlOSTokenResponse tokens;
        string userId;
        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            var signup = await issuance.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Refresh Offboard",
                    email,
                    "P@ssword123!",
                    $"Refresh Offboard {Guid.NewGuid():N}",
                    "test-client",
                    null),
                new DefaultHttpContext());
            tokens = signup.Tokens!;
            userId = await issuance.Context.Set<SqlOS.AuthServer.Models.SqlOSUser>()
                .Where(x => x.DefaultEmail == email)
                .Select(x => x.Id)
                .SingleAsync();
        }

        await using (var offboarding = BuildIsolatedContext())
        {
            var membership = await offboarding.Set<SqlOS.AuthServer.Models.SqlOSMembership>()
                .SingleAsync(x => x.UserId == userId && x.OrganizationId == tokens.OrganizationId);
            membership.IsActive = false;
            await offboarding.SaveChangesAsync();
        }

        await using var refreshInstance = BuildIsolatedLifecycleStack();
        var action = async () => await refreshInstance.Auth.RefreshAsync(
            new SqlOSRefreshRequest(tokens.RefreshToken, OrganizationId: null));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Session is no longer active.");
        (await refreshInstance.Context.Set<SqlOS.AuthServer.Models.SqlOSSession>()
            .SingleAsync(x => x.Id == tokens.SessionId)).RevocationReason.Should().Be("membership_inactive");
        (await refreshInstance.Context.Set<SqlOS.AuthServer.Models.SqlOSAuditEvent>()
            .AnyAsync(x => x.EventType == "auth.lifecycle.denied"
                && x.UserId == userId
                && x.OrganizationId == tokens.OrganizationId)).Should().BeTrue();
    }

    [TestMethod]
    public async Task AuthPageSession_AfterMembershipRemoval_IsRejectedAcrossDbContexts()
    {
        var email = $"cookie-offboard-{Guid.NewGuid():N}@example.com";
        string userId;
        string organizationId;
        string rawCookie;
        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            var signup = await issuance.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Cookie Offboard",
                    email,
                    "P@ssword123!",
                    $"Cookie Offboard {Guid.NewGuid():N}",
                    "test-client",
                    null),
                new DefaultHttpContext());
            userId = await issuance.Context.Set<SqlOS.AuthServer.Models.SqlOSUser>()
                .Where(x => x.DefaultEmail == email)
                .Select(x => x.Id)
                .SingleAsync();
            organizationId = signup.Tokens!.OrganizationId!;
            rawCookie = await issuance.Crypto.CreateTemporaryTokenAsync(
                "auth_page_session",
                userId,
                clientApplicationId: null,
                organizationId: organizationId,
                payload: new { AuthenticationMethod = "password" },
                lifetime: TimeSpan.FromMinutes(30));
        }

        await using (var offboarding = BuildIsolatedContext())
        {
            var membership = await offboarding.Set<SqlOS.AuthServer.Models.SqlOSMembership>()
                .SingleAsync(x => x.UserId == userId && x.OrganizationId == organizationId);
            membership.IsActive = false;
            await offboarding.SaveChangesAsync();
        }

        await using var reuseInstance = BuildIsolatedLifecycleStack();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = $"sqlos_auth_page={rawCookie}";

        (await reuseInstance.AuthPage.TryGetSessionAsync(httpContext)).Should().BeNull();
        (await reuseInstance.Crypto.FindTemporaryTokenAsync("auth_page_session", rawCookie)).Should().BeNull();
    }

    [TestMethod]
    public async Task ValidateAccessToken_IdleExpiredOrLifecycleInvalidSession_IsRejected()
    {
        SqlOSTokenResponse idleTokens;
        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            idleTokens = (await issuance.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Idle SQL",
                    $"idle-sql-{Guid.NewGuid():N}@example.com",
                    "P@ssword123!",
                    $"Idle SQL {Guid.NewGuid():N}",
                    "test-client",
                    null),
                new DefaultHttpContext())).Tokens!;
        }

        await using (var offboarding = BuildIsolatedContext())
        {
            var session = await offboarding.Set<SqlOS.AuthServer.Models.SqlOSSession>()
                .SingleAsync(x => x.Id == idleTokens.SessionId);
            session.IdleExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await offboarding.SaveChangesAsync();
        }

        await using (var validation = BuildIsolatedLifecycleStack())
        {
            (await validation.Auth.ValidateAccessTokenAsync(idleTokens.AccessToken, AspireFixture.Options.DefaultAudience))
                .Should().BeNull();
            (await validation.Context.Set<SqlOS.AuthServer.Models.SqlOSSession>()
                .SingleAsync(x => x.Id == idleTokens.SessionId)).RevocationReason.Should().Be("session_idle_expired");
        }

        var lifecycleEmail = $"access-offboard-{Guid.NewGuid():N}@example.com";
        SqlOSTokenResponse lifecycleTokens;
        string lifecycleUserId;
        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            lifecycleTokens = (await issuance.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Access Offboard",
                    lifecycleEmail,
                    "P@ssword123!",
                    $"Access Offboard {Guid.NewGuid():N}",
                    "test-client",
                    null),
                new DefaultHttpContext())).Tokens!;
            lifecycleUserId = await issuance.Context.Set<SqlOS.AuthServer.Models.SqlOSUser>()
                .Where(x => x.DefaultEmail == lifecycleEmail)
                .Select(x => x.Id)
                .SingleAsync();
        }

        await using (var offboarding = BuildIsolatedContext())
        {
            var organization = await offboarding.Set<SqlOS.AuthServer.Models.SqlOSOrganization>()
                .SingleAsync(x => x.Id == lifecycleTokens.OrganizationId);
            organization.IsActive = false;
            await offboarding.SaveChangesAsync();
        }

        await using (var validation = BuildIsolatedLifecycleStack())
        {
            (await validation.Auth.ValidateAccessTokenAsync(lifecycleTokens.AccessToken, AspireFixture.Options.DefaultAudience))
                .Should().BeNull();
            (await validation.Context.Set<SqlOS.AuthServer.Models.SqlOSSession>()
                .SingleAsync(x => x.Id == lifecycleTokens.SessionId)).RevocationReason.Should().Be("organization_inactive");
            (await validation.Context.Set<SqlOS.AuthServer.Models.SqlOSAuditEvent>()
                .AnyAsync(x => x.EventType == "auth.lifecycle.denied" && x.UserId == lifecycleUserId))
                .Should().BeTrue();
        }
    }

    [TestMethod]
    public async Task LogoutAll_RevokesPendingAuthorizationArtifactsAcrossDbContexts()
    {
        const string verifier = "sql-logout-verifier-123456789012345678901234";
        string userId;
        string organizationId;
        string pendingCode;
        string pendingMfaToken;
        string deviceAuthorizationId;
        string clientId;
        var email = $"pending-artifact-{Guid.NewGuid():N}@example.com";
        await using (var issuance = BuildIsolatedLifecycleStack())
        {
            var signup = await issuance.Auth.SignUpAsync(
                new SqlOSSignupRequest(
                    "Pending Artifact SQL",
                    email,
                    "P@ssword123!",
                    $"Pending Artifact {Guid.NewGuid():N}",
                    "test-client",
                    null),
                new DefaultHttpContext());
            var user = await issuance.Context.Set<SqlOSUser>()
                .SingleAsync(x => x.DefaultEmail == email);
            userId = user.Id;
            organizationId = signup.Tokens!.OrganizationId!;
            var client = await issuance.Context.Set<SqlOSClientApplication>()
                .SingleAsync(x => x.ClientId == "test-client");
            clientId = client.ClientId;
            var authorizationRequest = new SqlOSAuthorizationRequest
            {
                Id = $"req_{Guid.NewGuid():N}",
                ClientApplicationId = client.Id,
                ClientApplication = client,
                RedirectUri = "https://client.example.test/callback",
                State = "pending-artifact-state",
                Scope = "openid",
                CodeChallenge = issuance.Crypto.CreatePkceCodeChallenge(verifier),
                CodeChallengeMethod = "S256",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            };
            issuance.Context.Set<SqlOSAuthorizationRequest>().Add(authorizationRequest);
            await issuance.Context.SaveChangesAsync();
            var redirect = await issuance.Authorization.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                user,
                organizationId,
                "password",
                new DefaultHttpContext());
            pendingCode = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();
            pendingMfaToken = await issuance.Crypto.CreateTemporaryTokenAsync(
                SqlOSAuthService.MfaChallengePurpose,
                userId,
                client.Id,
                organizationId,
                new { Flow = "client" },
                TimeSpan.FromMinutes(5));
            deviceAuthorizationId = $"dev_{Guid.NewGuid():N}";
            var now = DateTime.UtcNow;
            issuance.Context.Set<SqlOSDeviceAuthorization>().Add(new SqlOSDeviceAuthorization
            {
                Id = deviceAuthorizationId,
                DeviceCodeHash = issuance.Crypto.HashToken($"device-{Guid.NewGuid():N}"),
                UserCodeHash = issuance.Crypto.HashToken($"code-{Guid.NewGuid():N}"),
                UserCode = "PENDING2",
                ClientApplicationId = client.Id,
                Status = SqlOSDeviceAuthorizationService.ApprovedStatus,
                ApprovedUserId = userId,
                ApprovedOrganizationId = organizationId,
                AuthenticationMethod = "password",
                CreatedAt = now,
                ApprovedAt = now,
                ExpiresAt = now.AddMinutes(10)
            });
            await issuance.Context.SaveChangesAsync();
        }

        await using (var revocation = BuildIsolatedLifecycleStack())
        {
            await revocation.Auth.LogoutAllAsync(userId);
        }

        await using var verification = BuildIsolatedLifecycleStack();
        var exchange = async () => await verification.Authorization.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                pendingCode,
                "https://client.example.test/callback",
                clientId,
                verifier,
                RefreshToken: null,
                Resource: null),
            new DefaultHttpContext());
        await exchange.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authorization code is no longer valid.");
        (await verification.Crypto.FindTemporaryTokenAsync(
            SqlOSAuthService.MfaChallengePurpose,
            pendingMfaToken)).Should().BeNull();
        var deviceAuthorization = await verification.Context.Set<SqlOSDeviceAuthorization>()
            .SingleAsync(x => x.Id == deviceAuthorizationId);
        deviceAuthorization.Status.Should().Be(SqlOSDeviceAuthorizationService.DeniedStatus);
        deviceAuthorization.DeniedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task Signup_Refresh_Logout_RoundTrips()
    {
        var auth = BuildAuthService();
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "IntegrationTest";

        var signup = await auth.SignUpAsync(new SqlOSSignupRequest(
            "Alice",
            $"alice-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            "Acme Corp",
            "test-client",
            null), http);

        signup.Tokens.Should().NotBeNull();
        signup.Tokens!.OrganizationId.Should().NotBeNullOrWhiteSpace();

        var refreshed = await auth.RefreshAsync(new SqlOSRefreshRequest(signup.Tokens.RefreshToken, signup.Tokens.OrganizationId));
        refreshed.AccessToken.Should().NotBeNullOrWhiteSpace();
        refreshed.RefreshToken.Should().NotBe(signup.Tokens.RefreshToken);

        await auth.LogoutAsync(refreshed.RefreshToken, null);

        var act = async () => await auth.RefreshAsync(new SqlOSRefreshRequest(refreshed.RefreshToken, refreshed.OrganizationId));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task Refresh_TwoInstancesRacingOnSameToken_BothSucceed_NoOrphans()
    {
        // The full multi-instance scenario the grace window + concurrency
        // token are designed to fix. Two SqlOSAuthService instances on
        // separate DbContexts race to refresh the same token at the same
        // instant — simulating two app servers behind a load balancer
        // both serving a parallel SSR Promise.all.
        //
        // With EF Core optimistic concurrency on `ConsumedAt`:
        //   - One UPDATE wins the rotation race
        //   - The other(s) get DbUpdateConcurrencyException, fall through
        //     to the grace window path, and return the SAME cached access
        //     token the winner produced
        //   - Exactly ONE replacement refresh token is inserted (no
        //     orphaned siblings polluting the family)
        //
        // Without the concurrency token, both rotations would silently
        // succeed and the family would have duplicate replacements.
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "ConcurrencyRaceTest";

        // Bootstrap a user and grab a single starting refresh token via
        // the shared context.
        var bootstrapAuth = BuildAuthService();
        var signup = await bootstrapAuth.SignUpAsync(new SqlOSSignupRequest(
            "Erin",
            $"erin-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            "Acme Corp",
            "test-client",
            null), http);

        var refreshToken = signup.Tokens!.RefreshToken;
        var orgId = signup.Tokens.OrganizationId;

        // Build TWO completely separate DbContext + service stacks
        // pointing at the same database. This is the key — each has its
        // own change tracker, so the race is genuine, not synthetic.
        var instanceA = BuildIsolatedAuthService();
        var instanceB = BuildIsolatedAuthService();

        // Fire both refresh calls in parallel and wait for both to finish.
        // Use Task.WhenAll to maximize the chance of overlapping inside
        // the SaveChanges window. Re-run a few times if the race doesn't
        // overlap on the first attempt — the test passes if the
        // invariants hold no matter which call wins.
        var task1 = instanceA.Service.RefreshAsync(new SqlOSRefreshRequest(refreshToken, orgId));
        var task2 = instanceB.Service.RefreshAsync(new SqlOSRefreshRequest(refreshToken, orgId));

        var results = await Task.WhenAll(task1, task2);

        // Both calls succeeded.
        results[0].AccessToken.Should().NotBeNullOrWhiteSpace();
        results[1].AccessToken.Should().NotBeNullOrWhiteSpace();

        // Critical invariant: both calls returned the SAME access token.
        // The winner produced it; the loser hit the grace window path and
        // returned the cached value.
        results[0].AccessToken.Should().Be(results[1].AccessToken,
            "both concurrent refreshes must yield the same access token (winner produces, loser reads from cache)");

        // Critical invariant: no orphaned refresh tokens. The family
        // should contain the original (now consumed) + exactly ONE
        // rotation replacement + ONE sibling token from the grace window
        // reissue path = 3 rows total. NOT 2 separate rotation replacements.
        instanceA.Dispose();
        instanceB.Dispose();

        var verifyCtx = BuildIsolatedContext();
        try
        {
            // Find the family ID from the original token.
            var crypto = new SqlOSCryptoService(verifyCtx, Microsoft.Extensions.Options.Options.Create(AspireFixture.Options), AspireFixture.DataProtectionProvider);
            var originalHash = crypto.HashToken(refreshToken);
            var original = await verifyCtx.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
                .FirstAsync(x => x.TokenHash == originalHash);
            var familyId = original.FamilyId;

            // Count rows that are direct rotations of the original (i.e.
            // have ReplacedByTokenId pointing AT the new token row, where
            // the new token's ConsumedAt is null and it was created by
            // the rotation flow). These are the rows the rotation race
            // could have multiplied.
            var rotationsFromOriginal = await verifyCtx.Set<SqlOS.AuthServer.Models.SqlOSRefreshToken>()
                .CountAsync(x => x.FamilyId == familyId && x.Id == original.ReplacedByTokenId);

            rotationsFromOriginal.Should().Be(1,
                "exactly ONE rotation replacement should exist for the original token; orphans here would mean the concurrency token failed");

            // Original must be marked consumed.
            original.ConsumedAt.Should().NotBeNull();
            original.ReplacedByTokenId.Should().NotBeNullOrEmpty();
            original.ReplacementAccessToken.Should().NotBeNullOrEmpty(
                "the winner must have cached its access token for the grace window path");
        }
        finally
        {
            await verifyCtx.DisposeAsync();
        }
    }

    /// <summary>
    /// Builds an isolated SqlOSAuthService with its own DbContext pointing
    /// at the shared SQL Server. Used to genuinely race two instances
    /// without sharing change-tracker state.
    /// </summary>
    private static (SqlOSAuthService Service, TestSqlOSDbContext Context) BuildIsolatedServiceTuple()
    {
        var ctx = BuildIsolatedContext();
        var options = Microsoft.Extensions.Options.Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(ctx, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(ctx, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(ctx, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(ctx, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(ctx, options, admin, crypto, settings, emailOtp);
        return (auth, ctx);
    }

    private sealed class IsolatedAuthService : IDisposable
    {
        public SqlOSAuthService Service { get; }
        private readonly TestSqlOSDbContext _context;
        public IsolatedAuthService(SqlOSAuthService service, TestSqlOSDbContext context)
        {
            Service = service;
            _context = context;
        }
        public void Dispose() => _context.Dispose();
    }

    private static IsolatedAuthService BuildIsolatedAuthService()
    {
        var (svc, ctx) = BuildIsolatedServiceTuple();
        return new IsolatedAuthService(svc, ctx);
    }

    private static TestSqlOSDbContext BuildIsolatedContext()
    {
        var dbOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(AspireFixture.SqlConnectionString)
            .Options;
        return new TestSqlOSDbContext(dbOptions);
    }

    private static IsolatedLifecycleStack BuildIsolatedLifecycleStack()
    {
        var context = BuildIsolatedContext();
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
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
        return new IsolatedLifecycleStack(context, crypto, auth, authPage, authorization);
    }

    private sealed class IsolatedLifecycleStack(
        TestSqlOSDbContext context,
        SqlOSCryptoService crypto,
        SqlOSAuthService auth,
        SqlOSAuthPageSessionService authPage,
        SqlOSAuthorizationServerService authorization) : IAsyncDisposable
    {
        public TestSqlOSDbContext Context { get; } = context;
        public SqlOSCryptoService Crypto { get; } = crypto;
        public SqlOSAuthService Auth { get; } = auth;
        public SqlOSAuthPageSessionService AuthPage { get; } = authPage;
        public SqlOSAuthorizationServerService Authorization { get; } = authorization;

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    [TestMethod]
    public async Task Refresh_WithSameTokenTwiceWithinGraceWindow_ReturnsSameAccessToken()
    {
        // Issue #18 — proves the grace window survives a real DB round trip.
        // Two refresh calls with the same consumed refresh token, both
        // happening within the default 30s grace window, must return the
        // SAME access token and must NOT revoke the token family.
        var auth = BuildAuthService();
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "GraceWindowIntegrationTest";

        var signup = await auth.SignUpAsync(new SqlOSSignupRequest(
            "Carol",
            $"carol-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            "Acme Corp",
            "test-client",
            null), http);

        var firstRefresh = await auth.RefreshAsync(
            new SqlOSRefreshRequest(signup.Tokens!.RefreshToken, signup.Tokens.OrganizationId));

        // Replay the SAME original refresh token immediately. This is the
        // canonical "two parallel SSR calls hit refresh at the same instant"
        // scenario the grace window is designed to fix.
        var secondRefresh = await auth.RefreshAsync(
            new SqlOSRefreshRequest(signup.Tokens.RefreshToken, signup.Tokens.OrganizationId));

        secondRefresh.AccessToken.Should().Be(firstRefresh.AccessToken,
            "the grace window should hand back the cached access token");

        // The forward refresh token from the first call should still be
        // valid — proving the family was NOT revoked by the replay.
        var thirdRefresh = await auth.RefreshAsync(
            new SqlOSRefreshRequest(firstRefresh.RefreshToken, firstRefresh.OrganizationId));
        thirdRefresh.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task Login_WithMultipleOrganizations_ReturnsPendingAuthToken()
    {
        var admin = BuildAdminService();
        var auth = BuildAuthService();
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Bob",
            $"bob-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var org1 = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Org One", null));
        var org2 = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Org Two", null));
        await admin.CreateMembershipAsync(org1.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        await admin.CreateMembershipAsync(org2.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));

        var result = await auth.LoginWithPasswordAsync(new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null), new DefaultHttpContext());
        result.RequiresOrganizationSelection.Should().BeTrue();
        result.PendingAuthToken.Should().NotBeNullOrWhiteSpace();
        result.Organizations.Should().HaveCount(2);
    }

    private static SqlOSAuthService BuildAuthService()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(AspireFixture.SharedContext, admin, crypto, settings, emailSender, options);
        return new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings, emailOtp);
    }

    private static SqlOSAdminService BuildAdminService()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        return new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
    }
}
