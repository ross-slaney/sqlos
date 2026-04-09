using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
