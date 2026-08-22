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
            await setupStack.Settings.EnsureDefaultMfaSettingsAsync();
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

    [TestMethod]
    public async Task ConsentGrantUpsert_WithParallelApprovals_KeepsOneActiveRowWithTheScopeUnion()
    {
        TestSqlOSDbContext? setupContext = null;
        string? connectionString = null;

        try
        {
            setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSConsentRace");
            connectionString = setupContext.Database.GetConnectionString();
            connectionString.Should().NotBeNullOrWhiteSpace();

            var clientId = $"consent-race-{Guid.NewGuid():N}"[..30];
            var redirectUri = $"https://client.example.test/{clientId}/callback";
            var options = CreateBaseOptions();
            options.SeedClient(client =>
            {
                client.ClientId = clientId;
                client.Name = "Consent Race Client";
                client.RedirectUris = [redirectUri];
                client.ClientType = "public_pkce";
                client.RequirePkce = true;
                client.IsFirstParty = false;
                client.AllowedScopes = ["openid", "todo:read", "todo:write"];
            });
            var setupStack = BuildStack(setupContext, options);
            await setupStack.Admin.UpsertSeededClientsAsync();
            var (user, _) = await SeedUserWithOrganizationAsync(setupStack, "Consent Race");
            var clientApplicationId = (await setupContext.Set<SqlOSClientApplication>()
                .SingleAsync(x => x.ClientId == clientId)).Id;

            // Two concurrent first approvals with different scope sets: the filtered unique
            // active index rejects one insert, whose retry must merge into the winning row.
            var scopeSets = new[]
            {
                new[] { "openid", "todo:read" },
                new[] { "openid", "todo:write" }
            };
            var stacks = scopeSets
                .Select(_ => BuildStack(CreateContext(connectionString!), options))
                .ToList();
            try
            {
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var tasks = stacks
                    .Select((stack, index) => Task.Run(async () =>
                    {
                        await ready.Task;
                        var consentService = new SqlOSConsentService(stack.Context, stack.Crypto);
                        return await consentService.UpsertGrantAsync(user.Id, clientApplicationId, scopeSets[index]);
                    }))
                    .ToArray();
                ready.SetResult(true);

                await Task.WhenAll(tasks);
            }
            finally
            {
                foreach (var stack in stacks)
                {
                    await stack.DisposeAsync();
                }
            }

            await using var verifyContext = CreateContext(connectionString!);
            var grants = await verifyContext.Set<SqlOSConsentGrant>()
                .Where(x => x.UserId == user.Id && x.ClientApplicationId == clientApplicationId)
                .ToListAsync();
            grants.Should().ContainSingle("both approvals must converge on one active grant row");
            var grant = grants.Single();
            grant.RevokedAt.Should().BeNull();
            SqlOSScopePolicy.Split(grant.Scope).Should().BeEquivalentTo(
                new[] { "openid", "todo:read", "todo:write" },
                "the losing approval must merge its scopes into the winning row instead of losing them");
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
    public async Task ConsentApproveAndDeny_RacingOnOneRequest_AllowExactlyOneTerminalOutcome()
    {
        TestSqlOSDbContext? setupContext = null;
        string? connectionString = null;

        try
        {
            setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSConsentDenyRace");
            connectionString = setupContext.Database.GetConnectionString();
            connectionString.Should().NotBeNullOrWhiteSpace();

            var clientId = $"consent-deny-{Guid.NewGuid():N}"[..30];
            var redirectUri = $"https://client.example.test/{clientId}/callback";
            var options = CreateBaseOptions();
            options.SeedClient(client =>
            {
                client.ClientId = clientId;
                client.Name = "Consent Deny Race Client";
                client.RedirectUris = [redirectUri];
                client.ClientType = "public_pkce";
                client.RequirePkce = true;
                client.IsFirstParty = false;
                client.AllowedScopes = ["openid", "todo:read"];
            });
            var setupStack = BuildStack(setupContext, options);
            await setupStack.Admin.UpsertSeededClientsAsync();
            await setupStack.Crypto.EnsureActiveSigningKeyAsync();
            await setupStack.Settings.EnsureDefaultMfaSettingsAsync();
            var (user, _) = await SeedUserWithOrganizationAsync(setupStack, "Consent Deny Race");

            var codeVerifier = setupStack.Crypto.GenerateOpaqueToken();
            var authorizationRequest = await setupStack.AuthorizationServer.CreateAuthorizationRequestAsync(
                new SqlOSAuthorizeRequestInput(
                    "code",
                    clientId,
                    redirectUri,
                    "state-consent-deny-race",
                    "openid todo:read",
                    setupStack.Crypto.CreatePkceCodeChallenge(codeVerifier),
                    "S256",
                    null,
                    user.DefaultEmail,
                    null,
                    null,
                    "hosted",
                    null));

            // Reload re-minting means several live consent tokens can exist for one
            // request; running the consent gate twice models that legitimately.
            var firstConsent = await setupStack.AuthorizationServer.CompleteAuthorizationRequestLoginAsync(
                authorizationRequest,
                user,
                "password",
                CreateHttpContext("consent-deny-race-login"));
            firstConsent.RequiresConsent.Should().BeTrue();
            var secondConsent = await setupStack.AuthorizationServer.CompleteAuthorizationRequestLoginAsync(
                authorizationRequest,
                user,
                "password",
                CreateHttpContext("consent-deny-race-login"));
            secondConsent.RequiresConsent.Should().BeTrue();

            var approveStack = BuildStack(CreateContext(connectionString!), options);
            var denyStack = BuildStack(CreateContext(connectionString!), options);
            try
            {
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var approveTask = Task.Run(async () =>
                {
                    await ready.Task;
                    try
                    {
                        var result = await approveStack.AuthorizationServer.ApproveConsentAsync(
                            firstConsent.ConsentToken!,
                            authorizationRequest.Id,
                            CreateHttpContext("consent-deny-race-approve"));
                        return RaceOutcome<SqlOSAuthorizationRequestLoginResult>.Success(result);
                    }
                    catch (Exception ex)
                    {
                        return RaceOutcome<SqlOSAuthorizationRequestLoginResult>.Failure(ex);
                    }
                });
                var denyTask = Task.Run(async () =>
                {
                    await ready.Task;
                    try
                    {
                        var redirect = await denyStack.AuthorizationServer.DenyConsentAsync(
                            secondConsent.ConsentToken!,
                            authorizationRequest.Id,
                            CreateHttpContext("consent-deny-race-deny"));
                        return RaceOutcome<string>.Success(redirect);
                    }
                    catch (Exception ex)
                    {
                        return RaceOutcome<string>.Failure(ex);
                    }
                });
                ready.SetResult(true);

                var approveOutcome = await approveTask;
                var denyOutcome = await denyTask;

                (approveOutcome.Succeeded ^ denyOutcome.Succeeded).Should().BeTrue(
                    "the CompletedAt/CancelledAt concurrency tokens must let exactly one terminal outcome commit "
                    + $"(approve: {approveOutcome.Error?.Message ?? "ok"}; deny: {denyOutcome.Error?.Message ?? "ok"})");

                await using var verifyContext = CreateContext(connectionString!);
                var storedRequest = await verifyContext.Set<SqlOSAuthorizationRequest>()
                    .SingleAsync(x => x.Id == authorizationRequest.Id);
                var storedCodes = await verifyContext.Set<SqlOSAuthorizationCode>()
                    .Where(x => x.AuthorizationRequestId == authorizationRequest.Id)
                    .ToListAsync();

                var storedClient = await verifyContext.Set<SqlOSClientApplication>()
                    .SingleAsync(x => x.ClientId == clientId);
                var storedGrants = await verifyContext.Set<SqlOSConsentGrant>()
                    .Where(x => x.UserId == user.Id && x.ClientApplicationId == storedClient.Id)
                    .ToListAsync();

                if (denyOutcome.Succeeded)
                {
                    storedRequest.CancelledAt.Should().NotBeNull();
                    storedRequest.CompletedAt.Should().BeNull();
                    storedCodes.Should().BeEmpty(
                        "a request the user denied must never leak an authorization code");
                    approveOutcome.Error.Should().BeOfType<InvalidOperationException>();
                    // The winning denial's effect must include the grant: an approval that
                    // committed its grant before losing the terminal-write race compensates
                    // by revoking it, so no active remembered grant survives the denial.
                    storedGrants.Should().OnlyContain(
                        x => x.RevokedAt != null,
                        "a denial that wins the CancelledAt race must not leave an active grant that would skip consent next time");
                    storedGrants
                        .Where(x => x.RevocationReason != null)
                        .Should().OnlyContain(x => x.RevocationReason == "authorization_request_cancelled");
                }
                else
                {
                    storedRequest.CompletedAt.Should().NotBeNull();
                    storedRequest.CancelledAt.Should().BeNull();
                    storedCodes.Should().ContainSingle();
                    var denyError = denyOutcome.Error.Should().BeOfType<InvalidOperationException>().Subject;
                    denyError.Message.Should().MatchRegex(
                        "no longer active|invalid or expired",
                        "a deny losing to a committed approval must surface the safe already-inactive failure, not a provider error");
                    // Real-SQL pin for the approve/CIMD-refresh TOCTOU closure: the winning
                    // approval stamps the metadata fingerprint it was granted against, so a
                    // refresh committed between the approval's staleness check and its grant
                    // write would leave a mismatched fingerprint and force re-consent.
                    var activeGrant = storedGrants.Single(x => x.RevokedAt == null);
                    activeGrant.ClientMetadataFingerprint.Should().Be(
                        SqlOSCimdClientService.ComputeSensitiveMetadataFingerprint(storedClient));
                }
            }
            finally
            {
                await approveStack.DisposeAsync();
                await denyStack.DisposeAsync();
            }
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
        var mfaPolicy = new SqlOSMfaPolicyService(context, settings, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp, mfaPolicyService: mfaPolicy);
        var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
        var authorizationServer = new SqlOSAuthorizationServerService(
            context,
            admin,
            auth,
            crypto,
            settings,
            authPageSession,
            options,
            mfaPolicyService: mfaPolicy);
        var device = new SqlOSDeviceAuthorizationService(context, admin, auth, crypto, options, mfaPolicy);
        return new ServiceStack(context, crypto, admin, settings, authorizationServer, device);
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
            SqlOSSettingsService settings,
            SqlOSAuthorizationServerService authorizationServer,
            SqlOSDeviceAuthorizationService device)
        {
            Context = context;
            Crypto = crypto;
            Admin = admin;
            Settings = settings;
            AuthorizationServer = authorizationServer;
            Device = device;
        }

        public TestSqlOSDbContext Context { get; }
        public SqlOSCryptoService Crypto { get; }
        public SqlOSAdminService Admin { get; }
        public SqlOSSettingsService Settings { get; }
        public SqlOSAuthorizationServerService AuthorizationServer { get; }
        public SqlOSDeviceAuthorizationService Device { get; }

        public ValueTask DisposeAsync()
            => Context.DisposeAsync();
    }
}
