using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class OAuthArtifactConcurrencyIntegrationTests
{
    [TestMethod]
    public async Task AuthorizationCodeExchange_WithTwentyParallelRequests_AllowsExactlyOneTokenResponse()
    {
        TestSqlOSDbContext? setupContext = null;
        string? connectionString = null;

        try
        {
            setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSCodeRace");
            connectionString = setupContext.Database.GetConnectionString();
            connectionString.Should().NotBeNullOrWhiteSpace();

            var clientId = $"code-race-{Guid.NewGuid():N}"[..30];
            var redirectUri = $"https://client.example.test/{clientId}/callback";
            var options = CreateOptions(clientId, redirectUri);
            var setupStack = BuildStack(setupContext, options);
            await setupStack.Admin.UpsertSeededClientsAsync();
            await setupStack.Crypto.EnsureActiveSigningKeyAsync();
            var (user, organization) = await SeedUserWithOrganizationAsync(setupStack, "Code Race");

            var codeVerifier = setupStack.Crypto.GenerateOpaqueToken();
            var authorizationRequest = await setupStack.AuthorizationServer.CreateAuthorizationRequestAsync(
                new SqlOSAuthorizeRequestInput(
                    "code",
                    clientId,
                    redirectUri,
                    "state-code-race",
                    "openid profile email offline_access",
                    setupStack.Crypto.CreatePkceCodeChallenge(codeVerifier),
                    "S256",
                    null,
                    user.DefaultEmail,
                    null,
                    null,
                    "hosted",
                    null));

            var redirect = await setupStack.AuthorizationServer.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                user,
                organization.Id,
                "password",
                CreateHttpContext("code-race-complete"));
            var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();
            code.Should().NotBeNullOrWhiteSpace();

            var stacks = Enumerable.Range(0, 20)
                .Select(_ => BuildStack(CreateContext(connectionString!), options))
                .ToList();
            try
            {
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var tasks = stacks
                    .Select(stack => Task.Run(async () =>
                    {
                        await ready.Task;
                        return await TryExchangeAuthorizationCodeAsync(stack, clientId, redirectUri, codeVerifier, code);
                    }))
                    .ToArray();
                ready.SetResult(true);

                var outcomes = await Task.WhenAll(tasks);

                outcomes.Count(x => x.Succeeded).Should().Be(1);
                outcomes.Where(x => !x.Succeeded)
                    .Select(x => x.Error)
                    .Should()
                    .AllBeOfType<InvalidOperationException>();
            }
            finally
            {
                foreach (var stack in stacks)
                {
                    await stack.DisposeAsync();
                }
            }

            await using var verifyContext = CreateContext(connectionString!);
            var storedCodes = await verifyContext.Set<SqlOSAuthorizationCode>()
                .Where(x => x.AuthorizationRequestId == authorizationRequest.Id)
                .ToListAsync();
            storedCodes.Should().ContainSingle();
            storedCodes.Single().ConsumedAt.Should().NotBeNull();

            var client = await verifyContext.Set<SqlOSClientApplication>()
                .SingleAsync(x => x.ClientId == clientId);
            var sessions = await verifyContext.Set<SqlOSSession>()
                .Where(x => x.UserId == user.Id && x.ClientApplicationId == client.Id)
                .ToListAsync();
            sessions.Should().ContainSingle();

            var refreshTokens = await verifyContext.Set<SqlOSRefreshToken>()
                .Where(x => x.SessionId == sessions.Single().Id)
                .ToListAsync();
            refreshTokens.Should().ContainSingle();
            refreshTokens.Select(x => x.FamilyId).Distinct().Should().ContainSingle();
        }
        finally
        {
            if (setupContext != null)
            {
                await setupContext.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                await using var cleanupContext = CreateContext(connectionString);
                await cleanupContext.Database.EnsureDeletedAsync();
            }
        }
    }

    [TestMethod]
    public async Task ApprovedDeviceCodePoll_WithTwentyParallelRequests_AllowsExactlyOneTokenResponse()
    {
        TestSqlOSDbContext? setupContext = null;
        string? connectionString = null;

        try
        {
            setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSDeviceRace");
            connectionString = setupContext.Database.GetConnectionString();
            connectionString.Should().NotBeNullOrWhiteSpace();

            var clientId = $"device-race-{Guid.NewGuid():N}"[..30];
            var audience = "https://api.example.test/device-race";
            var options = CreateBaseOptions();
            options.DefaultAudience = audience;
            options.ResourceIndicators.Enabled = true;
            options.SeedCliClient(clientId, "Device Race CLI", audience, "openid", "offline_access", "device.read");

            var setupStack = BuildStack(setupContext, options);
            await setupStack.Admin.UpsertSeededClientsAsync();
            await setupStack.Crypto.EnsureActiveSigningKeyAsync();
            var (user, organization) = await SeedUserWithOrganizationAsync(setupStack, "Device Race");

            var start = await setupStack.Device.StartAsync(
                new SqlOSDeviceAuthorizationStartRequest(clientId, "openid offline_access device.read", audience),
                CreateHttpContext("device-race-start"));
            var approval = await setupStack.Device.ApproveAsync(
                new SqlOSDeviceAuthorizationApprovalRequest(start.UserCode, organization.Id),
                user,
                "password",
                CreateHttpContext("device-race-approve"));
            approval.RequiresOrganizationSelection.Should().BeFalse();

            var stacks = Enumerable.Range(0, 20)
                .Select(_ => BuildStack(CreateContext(connectionString!), options))
                .ToList();
            try
            {
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var tasks = stacks
                    .Select(stack => Task.Run(async () =>
                    {
                        await ready.Task;
                        return await TryPollDeviceCodeAsync(stack, clientId, start.DeviceCode, audience);
                    }))
                    .ToArray();
                ready.SetResult(true);

                var outcomes = await Task.WhenAll(tasks);

                outcomes.Count(x => x.Succeeded).Should().Be(1);
                outcomes.Where(x => !x.Succeeded)
                    .Select(x => x.Error)
                    .Should()
                    .AllSatisfy(error =>
                    {
                        var deviceError = error.Should().BeOfType<SqlOSDeviceAuthorizationException>().Subject;
                        deviceError.Error.Should().Be("invalid_grant");
                    });
            }
            finally
            {
                foreach (var stack in stacks)
                {
                    await stack.DisposeAsync();
                }
            }

            await using var verifyContext = CreateContext(connectionString!);
            var deviceAuthorization = await verifyContext.Set<SqlOSDeviceAuthorization>()
                .SingleAsync(x => x.UserCode == start.UserCode);
            deviceAuthorization.ConsumedAt.Should().NotBeNull();

            var sessions = await verifyContext.Set<SqlOSSession>()
                .Where(x => x.UserId == user.Id && x.OrganizationId == organization.Id)
                .ToListAsync();
            sessions.Should().ContainSingle();
        }
        finally
        {
            if (setupContext != null)
            {
                await setupContext.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                await using var cleanupContext = CreateContext(connectionString);
                await cleanupContext.Database.EnsureDeletedAsync();
            }
        }
    }

    [TestMethod]
    public async Task AuthorizationRequestCompletion_WithTwentyParallelRequests_CreatesExactlyOneAuthorizationCode()
    {
        TestSqlOSDbContext? setupContext = null;
        string? connectionString = null;

        try
        {
            setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSCompleteRace");
            connectionString = setupContext.Database.GetConnectionString();
            connectionString.Should().NotBeNullOrWhiteSpace();

            var clientId = $"complete-race-{Guid.NewGuid():N}"[..30];
            var redirectUri = $"https://client.example.test/{clientId}/callback";
            var options = CreateOptions(clientId, redirectUri);
            var setupStack = BuildStack(setupContext, options);
            await setupStack.Admin.UpsertSeededClientsAsync();
            await setupStack.Crypto.EnsureActiveSigningKeyAsync();
            var (user, organization) = await SeedUserWithOrganizationAsync(setupStack, "Complete Race");
            var codeVerifier = setupStack.Crypto.GenerateOpaqueToken();

            var authorizationRequest = await setupStack.AuthorizationServer.CreateAuthorizationRequestAsync(
                new SqlOSAuthorizeRequestInput(
                    "code",
                    clientId,
                    redirectUri,
                    "state-complete-race",
                    "openid profile email",
                    setupStack.Crypto.CreatePkceCodeChallenge(codeVerifier),
                    "S256",
                    null,
                    user.DefaultEmail,
                    null,
                    null,
                    "hosted",
                    null));

            var stacks = Enumerable.Range(0, 20)
                .Select(_ => BuildStack(CreateContext(connectionString!), options))
                .ToList();
            try
            {
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var tasks = stacks
                    .Select(stack => Task.Run(async () =>
                    {
                        await ready.Task;
                        return await TryCompleteAuthorizationRequestAsync(stack, authorizationRequest.Id, user.Id, organization.Id);
                    }))
                    .ToArray();
                ready.SetResult(true);

                var outcomes = await Task.WhenAll(tasks);

                outcomes.Count(x => x.Succeeded).Should().Be(1);
                outcomes.Where(x => !x.Succeeded)
                    .Select(x => x.Error)
                    .Should()
                    .AllBeOfType<InvalidOperationException>();
            }
            finally
            {
                foreach (var stack in stacks)
                {
                    await stack.DisposeAsync();
                }
            }

            await using var verifyContext = CreateContext(connectionString!);
            var storedCodes = await verifyContext.Set<SqlOSAuthorizationCode>()
                .Where(x => x.AuthorizationRequestId == authorizationRequest.Id)
                .ToListAsync();
            storedCodes.Should().ContainSingle();

            var completedRequest = await verifyContext.Set<SqlOSAuthorizationRequest>()
                .SingleAsync(x => x.Id == authorizationRequest.Id);
            completedRequest.CompletedAt.Should().NotBeNull();
        }
        finally
        {
            if (setupContext != null)
            {
                await setupContext.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                await using var cleanupContext = CreateContext(connectionString);
                await cleanupContext.Database.EnsureDeletedAsync();
            }
        }
    }

    private static async Task<RaceOutcome<SqlOSTokenEndpointResult>> TryExchangeAuthorizationCodeAsync(
        ServiceStack stack,
        string clientId,
        string redirectUri,
        string codeVerifier,
        string code)
    {
        try
        {
            var result = await stack.AuthorizationServer.ExchangeAuthorizationCodeAsync(
                new SqlOSTokenRequest(
                    "authorization_code",
                    code,
                    redirectUri,
                    clientId,
                    codeVerifier,
                    null,
                    null),
                CreateHttpContext("code-race-token"));
            return RaceOutcome<SqlOSTokenEndpointResult>.Success(result);
        }
        catch (Exception ex)
        {
            return RaceOutcome<SqlOSTokenEndpointResult>.Failure(ex);
        }
    }

    private static async Task<RaceOutcome<SqlOSDeviceTokenPollResult>> TryPollDeviceCodeAsync(
        ServiceStack stack,
        string clientId,
        string deviceCode,
        string resource)
    {
        try
        {
            var result = await stack.Device.PollAsync(
                new SqlOSDeviceTokenPollRequest(clientId, deviceCode, resource),
                CreateHttpContext("device-race-poll"));
            return RaceOutcome<SqlOSDeviceTokenPollResult>.Success(result);
        }
        catch (Exception ex)
        {
            return RaceOutcome<SqlOSDeviceTokenPollResult>.Failure(ex);
        }
    }

    private static async Task<RaceOutcome<string>> TryCompleteAuthorizationRequestAsync(
        ServiceStack stack,
        string authorizationRequestId,
        string userId,
        string organizationId)
    {
        try
        {
            var authorizationRequest = await stack.Context.Set<SqlOSAuthorizationRequest>()
                .SingleAsync(x => x.Id == authorizationRequestId);
            var user = await stack.Context.Set<SqlOSUser>()
                .SingleAsync(x => x.Id == userId);

            var redirect = await stack.AuthorizationServer.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                user,
                organizationId,
                "password",
                CreateHttpContext("complete-race"));
            return RaceOutcome<string>.Success(redirect);
        }
        catch (Exception ex)
        {
            return RaceOutcome<string>.Failure(ex);
        }
    }

    private static async Task<(SqlOSUser User, SqlOSOrganization Organization)> SeedUserWithOrganizationAsync(
        ServiceStack stack,
        string namePrefix)
    {
        var user = await stack.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            $"{namePrefix} User",
            $"{namePrefix.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var organization = await stack.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"{namePrefix} Org {Guid.NewGuid():N}", null));
        await stack.Admin.CreateMembershipAsync(
            organization.Id,
            new SqlOSCreateMembershipRequest(user.Id, "owner"));
        return (user, organization);
    }

    private static SqlOSAuthServerOptions CreateOptions(string clientId, string redirectUri)
    {
        var options = CreateBaseOptions();
        options.SeedBrowserClient(clientId, "Race Test Client", redirectUri);
        return options;
    }

    private static SqlOSAuthServerOptions CreateBaseOptions()
        => new()
        {
            PublicOrigin = "https://auth.example.test",
            Issuer = "https://auth.example.test/sqlos/auth",
            BasePath = "/sqlos/auth"
        };

    private static ServiceStack BuildStack(TestSqlOSDbContext context, SqlOSAuthServerOptions optionsValue)
    {
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender { IsConfigured = true };
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
        var authorizationServer = new SqlOSAuthorizationServerService(
            context,
            admin,
            auth,
            crypto,
            settings,
            authPageSession,
            options);
        var device = new SqlOSDeviceAuthorizationService(context, admin, auth, crypto, options);
        return new ServiceStack(context, crypto, admin, authorizationServer, device);
    }

    private static TestSqlOSDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new TestSqlOSDbContext(options);
    }

    private static DefaultHttpContext CreateHttpContext(string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("auth.example.test");
        context.Request.Headers.UserAgent = userAgent;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
        return context;
    }

    private sealed record RaceOutcome<T>(bool Succeeded, T? Result, Exception? Error)
    {
        public static RaceOutcome<T> Success(T result) => new(true, result, null);

        public static RaceOutcome<T> Failure(Exception error) => new(false, default, error);
    }

    private sealed class ServiceStack : IAsyncDisposable
    {
        public ServiceStack(
            TestSqlOSDbContext context,
            SqlOSCryptoService crypto,
            SqlOSAdminService admin,
            SqlOSAuthorizationServerService authorizationServer,
            SqlOSDeviceAuthorizationService device)
        {
            Context = context;
            Crypto = crypto;
            Admin = admin;
            AuthorizationServer = authorizationServer;
            Device = device;
        }

        public TestSqlOSDbContext Context { get; }
        public SqlOSCryptoService Crypto { get; }
        public SqlOSAdminService Admin { get; }
        public SqlOSAuthorizationServerService AuthorizationServer { get; }
        public SqlOSDeviceAuthorizationService Device { get; }

        public ValueTask DisposeAsync()
            => Context.DisposeAsync();
    }
}
