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
        var settings = new SqlOSSettingsService(context, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings);

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
        var settingsService = new SqlOSSettingsService(context, options);

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
        var settingsService = new SqlOSSettingsService(context, options);

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
        var settingsService = new SqlOSSettingsService(context, options);

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
            var settings = new SqlOSSettingsService(context, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings);

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
