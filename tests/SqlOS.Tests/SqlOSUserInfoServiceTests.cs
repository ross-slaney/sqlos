using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSUserInfoServiceTests
{
    [TestMethod]
    public async Task GetUserInfoAsync_MissingToken_Returns401WithoutErrorCode()
    {
        using var context = CreateContext();
        var harness = CreateHarness(context);

        var result = await harness.UserInfo.GetUserInfoAsync(null);

        result.StatusCode.Should().Be(401);
        result.Error.Should().BeNull("RFC 6750 §3.1: a request without any authentication information gets a bare challenge");
        result.Claims.Should().BeNull();
    }

    [TestMethod]
    public async Task GetUserInfoAsync_GarbageToken_Returns401InvalidToken()
    {
        using var context = CreateContext();
        var harness = CreateHarness(context);

        var result = await harness.UserInfo.GetUserInfoAsync("not-a-real-jwt");

        result.StatusCode.Should().Be(401);
        result.Error.Should().Be("invalid_token");
        result.Claims.Should().BeNull();
    }

    [TestMethod]
    public async Task GetUserInfoAsync_SessionScopeWithoutOpenId_Returns403InsufficientScope()
    {
        using var context = CreateContext();
        var harness = CreateHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context);

        var tokens = await harness.Auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1",
            null,
            scope: "profile email",
            nonce: null,
            authenticatedAt: DateTime.UtcNow);

        var result = await harness.UserInfo.GetUserInfoAsync(tokens.AccessToken);

        result.StatusCode.Should().Be(403);
        result.Error.Should().Be("insufficient_scope");
        result.Claims.Should().BeNull();
    }

    [TestMethod]
    public async Task GetUserInfoAsync_OpenIdProfileEmailGrant_ReleasesScopedClaims()
    {
        using var context = CreateContext();
        var harness = CreateHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context);

        var tokens = await harness.Auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1",
            null,
            scope: "openid profile email",
            nonce: null,
            authenticatedAt: DateTime.UtcNow);

        var result = await harness.UserInfo.GetUserInfoAsync(tokens.AccessToken);

        result.StatusCode.Should().Be(200);
        result.Error.Should().BeNull();
        var claims = result.Claims!;
        claims["sub"].Should().Be(user.Id);
        claims["name"].Should().Be(user.DisplayName);
        claims["preferred_username"].Should().Be(user.DefaultEmail);
        claims["updated_at"].Should().Be(EpochTime.GetIntDate(user.UpdatedAt));
        claims["email"].Should().Be(user.DefaultEmail);
        claims["email_verified"].Should().Be(true);
        claims["amr"].Should().BeEquivalentTo(new[] { "password" });
    }

    [TestMethod]
    public async Task GetUserInfoAsync_OpenIdOnlyGrant_ReleasesOnlySubjectAndAmr()
    {
        using var context = CreateContext();
        var harness = CreateHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context);

        var tokens = await harness.Auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1",
            null,
            scope: "openid",
            nonce: null,
            authenticatedAt: DateTime.UtcNow);

        var result = await harness.UserInfo.GetUserInfoAsync(tokens.AccessToken);

        result.StatusCode.Should().Be(200);
        var claims = result.Claims!;
        claims["sub"].Should().Be(user.Id);
        claims.Keys.Should().BeEquivalentTo("sub", "amr");
    }

    [TestMethod]
    public async Task GetUserInfoAsync_AfterOrganizationSwitchRefresh_ReportsTheTokenOrganization()
    {
        using var context = CreateContext();
        var harness = CreateHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context);
        var source = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Source Org", "source-org"));
        var target = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Target Org", "target-org"));
        await harness.Admin.CreateMembershipAsync(source.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        await harness.Admin.CreateMembershipAsync(target.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));

        var issued = await harness.Auth.CreateSessionTokensForUserAsync(
            user,
            client,
            source.Id,
            "password",
            "test-agent",
            "127.0.0.1",
            null,
            scope: "openid",
            nonce: null,
            authenticatedAt: DateTime.UtcNow);
        var switched = await harness.Auth.RefreshAsync(new SqlOSRefreshRequest(issued.RefreshToken, target.Id));

        switched.OrganizationId.Should().Be(target.Id);
        (await context.Set<SqlOSSession>().AsNoTracking().SingleAsync(x => x.Id == switched.SessionId))
            .OrganizationId.Should().Be(source.Id, "the organization-switch refresh leaves the session on the source organization");

        var result = await harness.UserInfo.GetUserInfoAsync(switched.AccessToken);

        result.StatusCode.Should().Be(200);
        result.Claims!["org_id"].Should().Be(
            target.Id,
            "UserInfo must report the organization the presented access token was minted for, not the session's source organization");
    }

    private sealed record Harness(SqlOSAuthService Auth, SqlOSAdminService Admin, SqlOSUserInfoService UserInfo);

    private static Harness CreateHarness(TestSqlOSInMemoryDbContext context)
    {
        var options = Options.Create(new SqlOSAuthServerOptions
        {
            Issuer = "https://app.example.com/sqlos/auth",
            PublicOrigin = "https://app.example.com"
        });
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        return new Harness(auth, admin, new SqlOSUserInfoService(context, crypto));
    }

    private static async Task<SqlOSUser> SeedUserAsync(TestSqlOSInMemoryDbContext context)
    {
        var user = new SqlOSUser
        {
            Id = "usr_userinfo",
            DisplayName = "Alice Example",
            DefaultEmail = "alice@example.test",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Set<SqlOSUser>().Add(user);
        context.Set<SqlOSUserEmail>().Add(new SqlOSUserEmail
        {
            Id = "eml_userinfo",
            UserId = user.Id,
            Email = user.DefaultEmail!,
            NormalizedEmail = user.DefaultEmail!,
            IsPrimary = true,
            IsVerified = true,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<SqlOSClientApplication> SeedClientAsync(TestSqlOSInMemoryDbContext context)
    {
        var client = new SqlOSClientApplication
        {
            Id = "cli_userinfo",
            ClientId = "userinfo-client",
            Name = "UserInfo Client",
            Audience = "sqlos",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]",
            TokenEndpointAuthMethod = "none",
            RegistrationSource = "manual",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Set<SqlOSClientApplication>().Add(client);
        await context.SaveChangesAsync();
        return client;
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}
