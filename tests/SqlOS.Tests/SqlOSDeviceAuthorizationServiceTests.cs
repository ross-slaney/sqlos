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
public sealed class SqlOSDeviceAuthorizationServiceTests
{
    [TestMethod]
    public async Task StartAsync_RequiresDeviceEnabledClient_AndStoresHashedCodes()
    {
        await using var harness = await Harness.CreateAsync();

        var result = await harness.Device.StartAsync(
            new SqlOSDeviceAuthorizationStartRequest("todo-cli", "openid todos.read", "https://api.example.com/todos"),
            harness.Http);

        result.DeviceCode.Should().NotBeNullOrWhiteSpace();
        result.UserCode.Should().Contain("-");
        result.VerificationUri.Should().Be("https://auth.example.com/sqlos/auth/device");
        result.VerificationUriComplete.Should().Contain("user_code=");

        var stored = await harness.Context.Set<SqlOSDeviceAuthorization>().SingleAsync();
        var client = await harness.Context.Set<SqlOSClientApplication>().SingleAsync(x => x.Id == stored.ClientApplicationId);
        stored.DeviceCodeHash.Should().NotBe(result.DeviceCode);
        stored.UserCodeHash.Should().NotBe(result.UserCode);
        stored.UserCode.Should().Be(result.UserCode);
        client.ClientId.Should().Be("todo-cli");
        stored.Scope.Should().Be("openid todos.read");
    }

    [TestMethod]
    public async Task PollAsync_ReturnsPending_ThenSlowDown_BeforeApproval()
    {
        await using var harness = await Harness.CreateAsync();
        var start = await harness.Device.StartAsync(
            new SqlOSDeviceAuthorizationStartRequest("todo-cli", "openid", "https://api.example.com/todos"),
            harness.Http);

        var pending = await Assert.ThrowsExceptionAsync<SqlOSDeviceAuthorizationException>(() =>
            harness.Device.PollAsync(new SqlOSDeviceTokenPollRequest("todo-cli", start.DeviceCode, "https://api.example.com/todos"), harness.Http));
        pending.Error.Should().Be("authorization_pending");

        var slowDown = await Assert.ThrowsExceptionAsync<SqlOSDeviceAuthorizationException>(() =>
            harness.Device.PollAsync(new SqlOSDeviceTokenPollRequest("todo-cli", start.DeviceCode, "https://api.example.com/todos"), harness.Http));
        slowDown.Error.Should().Be("slow_down");
        slowDown.Interval.Should().BeGreaterThan(start.Interval);
    }

    [TestMethod]
    public async Task StartAsync_RejectsUnsupportedScopesAndResources()
    {
        await using var harness = await Harness.CreateAsync();

        var invalidScope = await Assert.ThrowsExceptionAsync<SqlOSDeviceAuthorizationException>(() =>
            harness.Device.StartAsync(
                new SqlOSDeviceAuthorizationStartRequest("todo-cli", "openid todos.delete", "https://api.example.com/todos"),
                harness.Http));
        invalidScope.Error.Should().Be("invalid_scope");

        var invalidResource = await Assert.ThrowsExceptionAsync<SqlOSDeviceAuthorizationException>(() =>
            harness.Device.StartAsync(
                new SqlOSDeviceAuthorizationStartRequest("todo-cli", "openid", "https://api.example.com/other"),
                harness.Http));
        invalidResource.Error.Should().Be("invalid_target");
    }

    [TestMethod]
    public async Task ApproveAsync_AllowsExactlyOneSuccessfulTokenPoll()
    {
        await using var harness = await Harness.CreateAsync();
        var user = await harness.SeedUserAsync();
        var start = await harness.Device.StartAsync(
            new SqlOSDeviceAuthorizationStartRequest("todo-cli", "openid offline_access todos.read", "https://api.example.com/todos"),
            harness.Http);

        var resolved = await harness.Device.ApproveAsync(
            new SqlOSDeviceAuthorizationApprovalRequest(start.UserCode),
            user,
            "password",
            harness.Http);

        resolved.Status.Should().Be(SqlOSDeviceAuthorizationService.ApprovedStatus);
        resolved.RequiresOrganizationSelection.Should().BeFalse();

        var tokenResult = await harness.Device.PollAsync(
            new SqlOSDeviceTokenPollRequest("todo-cli", start.DeviceCode, "https://api.example.com/todos"),
            harness.Http);

        tokenResult.Tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
        tokenResult.Tokens.RefreshToken.Should().NotBeNullOrWhiteSpace();
        tokenResult.Tokens.ClientId.Should().Be("todo-cli");
        tokenResult.Tokens.OrganizationId.Should().Be("org_test");
        tokenResult.Scope.Should().Be("openid offline_access todos.read");

        var reused = await Assert.ThrowsExceptionAsync<SqlOSDeviceAuthorizationException>(() =>
            harness.Device.PollAsync(new SqlOSDeviceTokenPollRequest("todo-cli", start.DeviceCode, "https://api.example.com/todos"), harness.Http));
        reused.Error.Should().Be("invalid_grant");
    }

    [TestMethod]
    public async Task PollAsync_RejectsWrongClientAndResource()
    {
        await using var harness = await Harness.CreateAsync();
        var start = await harness.Device.StartAsync(
            new SqlOSDeviceAuthorizationStartRequest("todo-cli", "openid", "https://api.example.com/todos"),
            harness.Http);

        var wrongClient = await Assert.ThrowsExceptionAsync<SqlOSDeviceAuthorizationException>(() =>
            harness.Device.PollAsync(new SqlOSDeviceTokenPollRequest("other-cli", start.DeviceCode, "https://api.example.com/todos"), harness.Http));
        wrongClient.Error.Should().Be("invalid_grant");

        var wrongResource = await Assert.ThrowsExceptionAsync<SqlOSDeviceAuthorizationException>(() =>
            harness.Device.PollAsync(new SqlOSDeviceTokenPollRequest("todo-cli", start.DeviceCode, "https://api.example.com/other"), harness.Http));
        wrongResource.Error.Should().Be("invalid_grant");
    }

    [TestMethod]
    public async Task Metadata_IncludesDeviceAuthorizationEndpoint_WhenEnabled()
    {
        await using var harness = await Harness.CreateAsync();

        var metadata = await harness.Authorization.GetMetadataAsync(harness.Http);

        metadata.DeviceAuthorizationEndpoint.Should().Be("https://auth.example.com/sqlos/auth/device_authorization");
        metadata.GrantTypesSupported.Should().Contain(SqlOSOAuthGrantTypes.DeviceCode);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqlOSCryptoService _crypto;

        private Harness(
            TestSqlOSInMemoryDbContext context,
            SqlOSDeviceAuthorizationService device,
            SqlOSAuthorizationServerService authorization,
            SqlOSCryptoService crypto,
            DefaultHttpContext http)
        {
            Context = context;
            Device = device;
            Authorization = authorization;
            _crypto = crypto;
            Http = http;
        }

        public TestSqlOSInMemoryDbContext Context { get; }
        public SqlOSDeviceAuthorizationService Device { get; }
        public SqlOSAuthorizationServerService Authorization { get; }
        public DefaultHttpContext Http { get; }

        public static async Task<Harness> CreateAsync()
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var authOptions = new SqlOSAuthServerOptions
            {
                PublicOrigin = "https://auth.example.com",
                Issuer = "https://auth.example.com/sqlos/auth",
                DefaultAudience = "https://api.example.com/todos"
            };
            authOptions.ResourceIndicators.Enabled = true;
            authOptions.SeedCliClient("todo-cli", "Todo CLI", "https://api.example.com/todos", "openid", "offline_access", "todos.read");
            authOptions.SeedCliClient("other-cli", "Other CLI", "https://api.example.com/todos", "openid");

            var options = Options.Create(authOptions);
            var crypto = new SqlOSCryptoService(context, options);
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender();
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
            var device = new SqlOSDeviceAuthorizationService(context, admin, auth, crypto, options);
            var authorization = new SqlOSAuthorizationServerService(context, admin, auth, crypto, settings, authPageSession, options);
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.com");
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

            await settings.EnsureDefaultAuthPageSettingsAsync();
            await admin.UpsertSeededClientsAsync();

            return new Harness(context, device, authorization, crypto, http);
        }

        public async Task<SqlOSUser> SeedUserAsync()
        {
            var now = DateTime.UtcNow;
            var organization = new SqlOSOrganization
            {
                Id = "org_test",
                Slug = "test",
                Name = "Test Org",
                CreatedAt = now
            };
            var user = new SqlOSUser
            {
                Id = _crypto.GenerateId("usr"),
                DisplayName = "Ada Lovelace",
                DefaultEmail = "ada@example.com",
                CreatedAt = now,
                UpdatedAt = now
            };
            Context.Set<SqlOSOrganization>().Add(organization);
            Context.Set<SqlOSUser>().Add(user);
            Context.Set<SqlOSUserEmail>().Add(new SqlOSUserEmail
            {
                Id = _crypto.GenerateId("eml"),
                UserId = user.Id,
                Email = "ada@example.com",
                NormalizedEmail = SqlOSAdminService.NormalizeEmail("ada@example.com"),
                IsPrimary = true,
                IsVerified = true,
                VerifiedAt = now,
                CreatedAt = now
            });
            Context.Set<SqlOSMembership>().Add(new SqlOSMembership
            {
                OrganizationId = organization.Id,
                UserId = user.Id,
                Role = "admin",
                CreatedAt = now
            });
            await Context.SaveChangesAsync();
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
        }
    }
}
