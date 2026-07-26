using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSConfidentialClientAuthenticationEndpointTests
{
    private const string ConfidentialA = "confidential-a";
    private const string ConfidentialB = "confidential-b";
    private const string PublicClient = "public-client";
    private const string SecretA = "confidential-a-secret-with-at-least-256-bits-of-entropy-123";
    private const string SecretB = "confidential-b-secret-with-at-least-256-bits-of-entropy-456";
    private const string RotatedSecretA = "confidential-a-rotated-secret-with-at-least-256-bits-789";
    private const string RedirectUri = "https://client.example.test/callback";
    private const string Verifier = "pkce-verifier-with-at-least-forty-three-characters-123456";

    [TestMethod]
    public async Task AuthorizationCode_RequiresValidConfidentialClientAuthenticationBeforeConsumption()
    {
        await using var harness = await Harness.StartAsync();
        var code = await harness.CreateAuthorizationCodeAsync(ConfidentialA);

        var missing = await harness.PostTokenAsync(AuthorizationCodeForm(code, ConfidentialA));
        await AssertGenericInvalidClientAsync(missing);
        (await harness.IsAuthorizationCodeConsumedAsync(code)).Should().BeFalse();

        var malformed = await harness.PostTokenAsync(
            AuthorizationCodeForm(code, ConfidentialA),
            basicParameter: "not-base64");
        await AssertGenericInvalidClientAsync(malformed);
        (await harness.IsAuthorizationCodeConsumedAsync(code)).Should().BeFalse();

        var unknown = await harness.PostTokenAsync(AuthorizationCodeForm(code, "unknown-client"));
        await AssertGenericInvalidClientAsync(unknown);
        (await harness.IsAuthorizationCodeConsumedAsync(code)).Should().BeFalse();

        var wrongSecret = await harness.PostTokenAsync(
            AuthorizationCodeForm(code, ConfidentialA),
            ConfidentialA,
            "wrong-secret-with-at-least-forty-three-characters-123456");
        await AssertGenericInvalidClientAsync(wrongSecret);
        (await harness.IsAuthorizationCodeConsumedAsync(code)).Should().BeFalse();

        var wrongClientCode = await harness.CreateAuthorizationCodeAsync(ConfidentialA);
        var wrongClient = await harness.PostTokenAsync(
            AuthorizationCodeForm(wrongClientCode, ConfidentialB),
            ConfidentialB,
            SecretB);
        await AssertGenericInvalidGrantAsync(wrongClient);
        (await harness.IsAuthorizationCodeConsumedAsync(wrongClientCode)).Should().BeFalse();

        var accepted = await harness.PostTokenAsync(
            AuthorizationCodeForm(code, ConfidentialA),
            ConfidentialA,
            SecretA);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        (await harness.IsAuthorizationCodeConsumedAsync(code)).Should().BeTrue();

        var auditJson = await harness.GetClientAuthenticationAuditJsonAsync();
        auditJson.Should().NotContain(SecretA);
        auditJson.Should().NotContain("wrong-secret");
    }

    [TestMethod]
    public async Task RefreshToken_RequiresArtifactClientAuthenticationAndFailureDoesNotConsumeToken()
    {
        await using var harness = await Harness.StartAsync();
        var refreshToken = await harness.CreateRefreshTokenAsync(ConfidentialA);

        var missing = await harness.PostTokenAsync(RefreshForm(refreshToken, ConfidentialA));
        await AssertGenericInvalidClientAsync(missing);
        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeFalse();

        var wrongClient = await harness.PostTokenAsync(
            RefreshForm(refreshToken, ConfidentialB),
            ConfidentialB,
            SecretB);
        await AssertGenericInvalidGrantAsync(wrongClient);
        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeFalse();

        var accepted = await harness.PostTokenAsync(
            RefreshForm(refreshToken, ConfidentialA),
            ConfidentialA,
            SecretA);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeTrue();
    }

    [TestMethod]
    public async Task PublicPkceAuthorizationCodeAndRefreshRemainUnauthenticated()
    {
        await using var harness = await Harness.StartAsync();
        var code = await harness.CreateAuthorizationCodeAsync(PublicClient);

        var exchanged = await harness.PostTokenAsync(AuthorizationCodeForm(code, PublicClient));
        var exchangeBody = await exchanged.Content.ReadAsStringAsync();
        exchanged.StatusCode.Should().Be(HttpStatusCode.OK, exchangeBody);
        using var exchangeJson = JsonDocument.Parse(exchangeBody);
        var refreshToken = exchangeJson.RootElement.GetProperty("refresh_token").GetString();
        refreshToken.Should().NotBeNullOrWhiteSpace();

        var refreshed = await harness.PostTokenAsync(RefreshForm(refreshToken!, PublicClient));
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task ConfidentialClientAuthenticationRejectsAmbiguityAndSupportsRotationOverlap()
    {
        await using var harness = await Harness.StartAsync();
        await harness.AddClientCredentialAsync(ConfidentialA, RotatedSecretA);

        var originalCredentialCode = await harness.CreateAuthorizationCodeAsync(ConfidentialA);
        var originalAccepted = await harness.PostTokenAsync(
            AuthorizationCodeForm(originalCredentialCode, ConfidentialA),
            ConfidentialA,
            SecretA);
        originalAccepted.StatusCode.Should().Be(HttpStatusCode.OK);

        var rotatedCredentialCode = await harness.CreateAuthorizationCodeAsync(ConfidentialA);
        var rotatedAccepted = await harness.PostTokenAsync(
            AuthorizationCodeForm(rotatedCredentialCode, ConfidentialA),
            ConfidentialA,
            RotatedSecretA);
        rotatedAccepted.StatusCode.Should().Be(HttpStatusCode.OK);

        await harness.RevokePrimaryCredentialAsync(ConfidentialA);
        var revokedCredentialCode = await harness.CreateAuthorizationCodeAsync(ConfidentialA);
        var revokedCredential = await harness.PostTokenAsync(
            AuthorizationCodeForm(revokedCredentialCode, ConfidentialA),
            ConfidentialA,
            SecretA);
        await AssertGenericInvalidClientAsync(revokedCredential);
        (await harness.IsAuthorizationCodeConsumedAsync(revokedCredentialCode)).Should().BeFalse();
        var retainedCredentialCode = await harness.CreateAuthorizationCodeAsync(ConfidentialA);
        var retainedCredential = await harness.PostTokenAsync(
            AuthorizationCodeForm(retainedCredentialCode, ConfidentialA),
            ConfidentialA,
            RotatedSecretA);
        retainedCredential.StatusCode.Should().Be(HttpStatusCode.OK);

        var ambiguousCode = await harness.CreateAuthorizationCodeAsync(ConfidentialA);
        var duplicateClientIds = AuthorizationCodeForm(ambiguousCode, ConfidentialA).ToList();
        duplicateClientIds.Add(new KeyValuePair<string, string>("client_id", ConfidentialA));
        var ambiguous = await harness.PostTokenAsync(
            duplicateClientIds,
            ConfidentialA,
            SecretA);
        await AssertGenericInvalidClientAsync(ambiguous);
        (await harness.IsAuthorizationCodeConsumedAsync(ambiguousCode)).Should().BeFalse();

        var mixedMethodsCode = await harness.CreateAuthorizationCodeAsync(ConfidentialA);
        var mixedMethodsForm = AuthorizationCodeForm(mixedMethodsCode, ConfidentialA).ToList();
        mixedMethodsForm.Add(new KeyValuePair<string, string>("client_secret", SecretA));
        var mixedMethods = await harness.PostTokenAsync(
            mixedMethodsForm,
            ConfidentialA,
            SecretA);
        await AssertGenericInvalidClientAsync(mixedMethods);
        (await harness.IsAuthorizationCodeConsumedAsync(mixedMethodsCode)).Should().BeFalse();

        var disabledCode = await harness.CreateAuthorizationCodeAsync(ConfidentialA);
        await harness.DisableClientAsync(ConfidentialA);
        var disabled = await harness.PostTokenAsync(
            AuthorizationCodeForm(disabledCode, ConfidentialA),
            ConfidentialA,
            SecretA);
        await AssertGenericInvalidClientAsync(disabled);
        (await harness.IsAuthorizationCodeConsumedAsync(disabledCode)).Should().BeFalse();
    }

    private static IEnumerable<KeyValuePair<string, string>> AuthorizationCodeForm(string code, string clientId)
        =>
        [
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", RedirectUri),
            new("client_id", clientId),
            new("code_verifier", Verifier)
        ];

    private static IEnumerable<KeyValuePair<string, string>> RefreshForm(string refreshToken, string clientId)
        =>
        [
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", clientId)
        ];

    private static async Task AssertGenericInvalidClientAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        response.Headers.WwwAuthenticate.Should().ContainSingle(x => x.Scheme == "Basic");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetString().Should().Be("invalid_client");
        json.RootElement.GetProperty("error_description").GetString().Should().Be("Client authentication failed.");
        body.Should().NotContain(ConfidentialA);
        body.Should().NotContain(ConfidentialB);
        body.Should().NotContain(SecretA);
        body.Should().NotContain(SecretB);
    }

    private static async Task AssertGenericInvalidGrantAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetString().Should().Be("invalid_grant");
        json.RootElement.GetProperty("error_description").GetString()
            .Should().Be("The authorization grant is invalid or expired.");
        body.Should().NotContain(ConfidentialA);
        body.Should().NotContain(ConfidentialB);
        body.Should().NotContain(SecretA);
        body.Should().NotContain(SecretB);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly HttpClient _client;
        private readonly string _userId;

        private Harness(IHost host, string userId)
        {
            _host = host;
            _client = host.GetTestClient();
            _userId = userId;
        }

        public static async Task<Harness> StartAsync()
        {
            var databaseName = Guid.NewGuid().ToString("N");
            var host = await new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddLogging();
                        services.AddDbContext<TestSqlOSInMemoryDbContext>(db =>
                            db.UseInMemoryDatabase(databaseName));
                        services.AddSqlOS<TestSqlOSInMemoryDbContext>(sqlos =>
                        {
                            sqlos.AuthServer.Issuer = "https://tests.example.local/sqlos/auth";
                            sqlos.AuthServer.BasePath = "/sqlos/auth";
                            AddClientSeed(sqlos.AuthServer.ClientSeeds, ConfidentialA, "Confidential A", SecretA, "confidential");
                            AddClientSeed(sqlos.AuthServer.ClientSeeds, ConfidentialB, "Confidential B", SecretB, "confidential");
                            AddClientSeed(sqlos.AuthServer.ClientSeeds, PublicClient, "Public client", null, "public_pkce");
                        });
                        foreach (var hostedService in services
                            .Where(x => x.ServiceType == typeof(IHostedService))
                            .ToList())
                        {
                            services.Remove(hostedService);
                        }
                        services.AddSingleton<ISqlOSAuthEmailSender>(
                            new TestAuthEmailSender { IsConfigured = true });
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapAuthServer("/sqlos/auth"));
                    }))
                .StartAsync();

            using var scope = host.Services.CreateScope();
            var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var settings = scope.ServiceProvider.GetRequiredService<SqlOSSettingsService>();
            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.EnsureDefaultSettingsAsync();
            var user = await admin.CreateUserAsync(new(
                "Confidential Client Test User",
                $"confidential-client-{Guid.NewGuid():N}@example.test",
                "P@ssword123!"));
            return new Harness(host, user.Id);
        }

        public async Task<string> CreateAuthorizationCodeAsync(string clientId)
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            var client = await context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == clientId);
            var code = crypto.GenerateOpaqueToken();
            context.Set<SqlOSAuthorizationCode>().Add(new()
            {
                Id = crypto.GenerateId("acd"),
                AuthorizationRequestId = crypto.GenerateId("areq"),
                UserId = _userId,
                ClientApplicationId = client.Id,
                RedirectUri = RedirectUri,
                State = "state",
                Scope = "openid offline_access",
                CodeHash = crypto.HashToken(code),
                CodeChallenge = crypto.CreatePkceCodeChallenge(Verifier),
                CodeChallengeMethod = "S256",
                AuthenticationMethod = "password",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });
            await context.SaveChangesAsync();
            return code;
        }

        public async Task<string> CreateRefreshTokenAsync(string clientId)
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            var user = await context.Set<SqlOSUser>().SingleAsync(x => x.Id == _userId);
            var client = await context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == clientId);
            var tokens = await auth.CreateSessionTokensForUserAsync(
                user,
                client,
                null,
                "password",
                "ConfidentialClientEndpointTests",
                "203.0.113.10");
            return tokens.RefreshToken;
        }

        public async Task AddClientCredentialAsync(string clientId, string secret)
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            var client = await context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == clientId);
            context.Set<SqlOSClientCredential>().Add(new()
            {
                Id = crypto.GenerateId("clcred"),
                ClientApplicationId = client.Id,
                SecretHash = crypto.HashPassword(secret),
                DisplayName = "Rotation overlap",
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        public async Task RevokePrimaryCredentialAsync(string clientId)
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var client = await context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == clientId);
            var credential = await context.Set<SqlOSClientCredential>().SingleAsync(x =>
                x.ClientApplicationId == client.Id
                && x.ConfigurationSourceKey == "primary");
            credential.RevokedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        public async Task DisableClientAsync(string clientId)
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var client = await context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == clientId);
            client.IsActive = false;
            client.DisabledAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        public async Task<bool> IsAuthorizationCodeConsumedAsync(string rawCode)
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            return await context.Set<SqlOSAuthorizationCode>()
                .Where(x => x.CodeHash == crypto.HashToken(rawCode))
                .Select(x => x.ConsumedAt != null)
                .SingleAsync();
        }

        public async Task<bool> IsRefreshTokenConsumedAsync(string rawToken)
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            return await context.Set<SqlOSRefreshToken>()
                .Where(x => x.TokenHash == crypto.HashToken(rawToken))
                .Select(x => x.ConsumedAt != null)
                .SingleAsync();
        }

        public async Task<string> GetClientAuthenticationAuditJsonAsync()
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var payloads = await context.Set<SqlOSAuditEvent>()
                .Where(x => x.EventType.StartsWith("oauth.client_authentication."))
                .Select(x => x.DataJson)
                .ToListAsync();
            return string.Join('\n', payloads);
        }

        public async Task<HttpResponseMessage> PostTokenAsync(
            IEnumerable<KeyValuePair<string, string>> form,
            string? basicClientId = null,
            string? basicSecret = null,
            string? basicParameter = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/token")
            {
                Content = new FormUrlEncodedContent(form)
            };
            if (basicParameter != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicParameter);
            }
            else if (basicClientId != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basicClientId}:{basicSecret}")));
            }
            return await _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            _host.Dispose();
            await Task.CompletedTask;
        }

        private static void AddClientSeed(
            ICollection<SqlOS.AuthServer.Configuration.SqlOSClientSeedOptions> seeds,
            string clientId,
            string name,
            string? secret,
            string clientType)
        {
            seeds.Add(new()
            {
                ClientId = clientId,
                Name = name,
                Audience = "sqlos",
                ClientType = clientType,
                RequirePkce = true,
                RedirectUris = [RedirectUri],
                AllowedScopes = ["openid", "offline_access"],
                ClientSecretResolver = secret == null ? null : () => secret
            });
        }
    }
}
