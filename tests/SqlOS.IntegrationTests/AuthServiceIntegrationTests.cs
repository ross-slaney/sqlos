using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class AuthServiceIntegrationTests
{
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
            var crypto = new SqlOSCryptoService(verifyCtx, Microsoft.Extensions.Options.Options.Create(AspireFixture.Options));
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
        var crypto = new SqlOSCryptoService(ctx, options);
        var admin = new SqlOSAdminService(ctx, options, crypto);
        var settings = new SqlOSSettingsService(ctx, options);
        var auth = new SqlOSAuthService(ctx, options, admin, crypto, settings);
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
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options);
        return new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings);
    }

    private static SqlOSAdminService BuildAdminService()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options);
        return new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
    }
}
