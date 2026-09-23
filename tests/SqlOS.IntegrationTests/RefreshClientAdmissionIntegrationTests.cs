using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class RefreshClientAdmissionIntegrationTests
{
    private const string Secret = "refresh-admission-secret-with-at-least-256-bits-of-entropy-1";

    [TestMethod]
    public async Task ConfidentialRefresh_RealSqlRaceBetweenAuthenticatedAndUnauthenticatedAttempts_IssuesOnlyTheAuthorizedSuccessor()
    {
        var clientId = $"refresh-admission-{Guid.NewGuid():N}";
        var (client, user) = await SeedConfidentialClientAsync(clientId);

        for (var round = 0; round < 3; round++)
        {
            var refreshToken = await IssueRefreshTokenAsync(client.Id, user.Id);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Every attempt runs on its own DbContext against the same database,
            // so the race reaches the provider rather than a shared change tracker.
            var unauthenticated = Enumerable.Range(0, 4)
                .Select(index => CaptureAsync(async () =>
                {
                    await using var stack = Stack.Create();
                    await gate.Task;
                    return index % 2 == 0
                        // The JSON compatibility route calls exactly this.
                        ? await stack.Auth.RefreshAsync(new SqlOSRefreshRequest(refreshToken, null, ClientId: clientId))
                        // A host calling the public exchange without admission.
                        : (await stack.AuthorizationServer.ExchangeAuthorizationCodeAsync(
                            new SqlOSTokenRequest("refresh_token", null, null, clientId, null, refreshToken, null),
                            new DefaultHttpContext())).Tokens;
                }))
                .ToList();
            var authenticated = CaptureAsync(async () =>
            {
                await using var stack = Stack.Create();
                var form = new FormCollection(new Dictionary<string, StringValues>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken
                });
                var httpContext = new DefaultHttpContext();
                httpContext.Request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{Secret}"))).ToString();
                await gate.Task;
                var admission = await stack.ClientAuthentication.AdmitRefreshGrantClientAsync(form, httpContext);
                return (await stack.AuthorizationServer.ExchangeAuthorizationCodeAsync(
                    new SqlOSTokenRequest("refresh_token", null, null, admission.ClientId, null, refreshToken, null),
                    admission,
                    httpContext)).Tokens;
            });

            gate.SetResult();
            var rejected = await Task.WhenAll(unauthenticated).WaitAsync(TimeSpan.FromSeconds(60));
            var accepted = await authenticated.WaitAsync(TimeSpan.FromSeconds(60));

            accepted.Error.Should().BeNull($"round {round}: the authenticated exchange must succeed");
            rejected.Should().OnlyContain(
                x => x.Tokens == null && x.Error is SqlOSClientAuthenticationException,
                $"round {round}: unauthenticated attempts must fail client authentication, never share the winner's cached response");

            await using var verify = Stack.Create();
            var original = await verify.Context.Set<SqlOSRefreshToken>()
                .AsNoTracking()
                .SingleAsync(x => x.TokenHash == verify.Crypto.HashToken(refreshToken));
            var family = await verify.Context.Set<SqlOSRefreshToken>()
                .AsNoTracking()
                .Where(x => x.FamilyId == original.FamilyId)
                .ToListAsync();
            family.Should().HaveCount(2, $"round {round}: only the authorized exchange may add a successor");
            var successor = family.Single(x => x.Id != original.Id);
            original.ConsumedAt.Should().NotBeNull();
            original.ReplacedByTokenId.Should().Be(successor.Id);
            successor.TokenHash.Should().Be(verify.Crypto.HashToken(accepted.Tokens!.RefreshToken));
            successor.RevokedAt.Should().BeNull("a rejected attempt is not an authenticated replay");
            (await verify.Context.Set<SqlOSSession>().AsNoTracking()
                    .CountAsync(x => x.ClientApplicationId == client.Id && x.RevokedAt == null))
                .Should().Be(round + 1, "rejected attempts must not create or revoke sessions");
        }
    }

    private static async Task<(SqlOSClientApplication Client, SqlOSUser User)> SeedConfidentialClientAsync(string clientId)
    {
        await using var stack = Stack.Create();
        var client = new SqlOSClientApplication
        {
            Id = $"cli_{Guid.NewGuid():N}",
            ClientId = clientId,
            Name = "Refresh admission client",
            Audience = "sqlos",
            ClientType = "confidential",
            TokenEndpointAuthMethod = "client_secret_basic",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        stack.Context.Set<SqlOSClientApplication>().Add(client);
        stack.Context.Set<SqlOSClientCredential>().Add(new SqlOSClientCredential
        {
            Id = $"clcred_{Guid.NewGuid():N}",
            ClientApplicationId = client.Id,
            SecretHash = stack.Crypto.HashPassword(Secret),
            CreatedAt = DateTime.UtcNow
        });
        await stack.Context.SaveChangesAsync();
        var user = await stack.Admin.CreateUserAsync(new(
            "Refresh Admission User",
            $"refresh-admission-{Guid.NewGuid():N}@example.test",
            "P@ssword123!"));
        return (client, user);
    }

    private static async Task<string> IssueRefreshTokenAsync(string clientApplicationId, string userId)
    {
        await using var stack = Stack.Create();
        var client = await stack.Context.Set<SqlOSClientApplication>().SingleAsync(x => x.Id == clientApplicationId);
        var user = await stack.Context.Set<SqlOSUser>().SingleAsync(x => x.Id == userId);
        var tokens = await stack.Auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "RefreshClientAdmissionIntegrationTests",
            "203.0.113.20");
        return tokens.RefreshToken;
    }

    private static async Task<(SqlOSTokenResponse? Tokens, Exception? Error)> CaptureAsync(
        Func<Task<SqlOSTokenResponse>> attempt)
    {
        try
        {
            return (await attempt(), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private sealed class Stack : IAsyncDisposable
    {
        private Stack(
            TestSqlOSDbContext context,
            SqlOSCryptoService crypto,
            SqlOSAdminService admin,
            SqlOSAuthService auth,
            SqlOSAuthorizationServerService authorizationServer,
            SqlOSClientAuthenticationService clientAuthentication)
        {
            Context = context;
            Crypto = crypto;
            Admin = admin;
            Auth = auth;
            AuthorizationServer = authorizationServer;
            ClientAuthentication = clientAuthentication;
        }

        public TestSqlOSDbContext Context { get; }
        public SqlOSCryptoService Crypto { get; }
        public SqlOSAdminService Admin { get; }
        public SqlOSAuthService Auth { get; }
        public SqlOSAuthorizationServerService AuthorizationServer { get; }
        public SqlOSClientAuthenticationService ClientAuthentication { get; }

        public static Stack Create()
        {
            var context = new TestSqlOSDbContext(
                new DbContextOptionsBuilder<TestSqlOSDbContext>()
                    .UseTestProvider(AspireFixture.SqlConnectionString)
                    .Options);
            var options = Options.Create(AspireFixture.Options);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender();
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
            var authorizationServer = new SqlOSAuthorizationServerService(
                context,
                admin,
                auth,
                crypto,
                settings,
                new SqlOSIssuerSessionService(context, crypto, settings),
                options);
            return new Stack(
                context,
                crypto,
                admin,
                auth,
                authorizationServer,
                new SqlOSClientAuthenticationService(context, crypto, admin));
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}
