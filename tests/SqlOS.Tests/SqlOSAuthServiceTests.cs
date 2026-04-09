using FluentAssertions;
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

            var crypto = new SqlOSCryptoService(context, options);
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
