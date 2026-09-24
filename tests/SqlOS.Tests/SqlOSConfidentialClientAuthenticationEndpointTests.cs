using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using SqlOS.AuthServer.Contracts;
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
    private const string ConfidentialPost = "confidential-post";
    private const string PublicClient = "public-client";
    private const string SecretA = "confidential-a-secret-with-at-least-256-bits-of-entropy-123";
    private const string SecretB = "confidential-b-secret-with-at-least-256-bits-of-entropy-456";
    private const string SecretPost = "confidential-post-secret-with-at-least-256-bits-entropy-789";
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
    public async Task PublicRefresh_WithoutClientId_SucceedsBecauseRefreshTokenIdentifiesTheClient()
    {
        await using var harness = await Harness.StartAsync();
        var refreshToken = await harness.CreateRefreshTokenAsync(PublicClient);

        var refreshed = await harness.PostTokenAsync(RefreshForm(refreshToken));
        var body = await refreshed.Content.ReadAsStringAsync();
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        json.RootElement.GetProperty("refresh_token").GetString().Should().NotBeNullOrWhiteSpace();
        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeTrue();
    }

    [TestMethod]
    public async Task PublicRefresh_WrongClientId_IsInvalidGrantAndDoesNotConsumeToken()
    {
        await using var harness = await Harness.StartAsync();
        var refreshToken = await harness.CreateRefreshTokenAsync(PublicClient);

        var wrongClient = await harness.PostTokenAsync(RefreshForm(refreshToken, ConfidentialA));
        await AssertGenericInvalidGrantAsync(wrongClient);
        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeFalse();
    }

    [TestMethod]
    public async Task PublicRefresh_DisabledClientWithoutClientId_IsInvalidClientAndDoesNotConsumeToken()
    {
        await using var harness = await Harness.StartAsync();
        var refreshToken = await harness.CreateRefreshTokenAsync(PublicClient);
        await harness.DisableClientAsync(PublicClient);

        var disabled = await harness.PostTokenAsync(RefreshForm(refreshToken));
        await AssertGenericInvalidClientAsync(disabled);
        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeFalse();
    }

    [TestMethod]
    public async Task ConfidentialRefresh_WithoutClientIdOrSecret_IsInvalidClientAndDoesNotConsumeToken()
    {
        await using var harness = await Harness.StartAsync();
        var refreshToken = await harness.CreateRefreshTokenAsync(ConfidentialA);

        var missing = await harness.PostTokenAsync(RefreshForm(refreshToken));
        await AssertGenericInvalidClientAsync(missing);
        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeFalse();
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

    [TestMethod]
    public async Task ClientSecretPost_AuthenticatesWithBodySecret()
    {
        await using var harness = await Harness.StartAsync();
        var code = await harness.CreateAuthorizationCodeAsync(ConfidentialPost);
        var form = AuthorizationCodeForm(code, ConfidentialPost).ToList();
        form.Add(new KeyValuePair<string, string>("client_secret", SecretPost));

        var accepted = await harness.PostTokenAsync(form);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        (await harness.IsAuthorizationCodeConsumedAsync(code)).Should().BeTrue();
        var auditJson = await harness.GetClientAuthenticationAuditJsonAsync();
        auditJson.Should().NotContain(SecretPost);
    }

    [TestMethod]
    public async Task ClientSecretPost_RejectsBasicAuthentication()
    {
        await using var harness = await Harness.StartAsync();
        var code = await harness.CreateAuthorizationCodeAsync(ConfidentialPost);

        // Correct secret over the wrong transport: the registered method is
        // client_secret_post, so HTTP Basic must be rejected.
        var rejected = await harness.PostTokenAsync(
            AuthorizationCodeForm(code, ConfidentialPost),
            ConfidentialPost,
            SecretPost);

        await AssertGenericInvalidClientAsync(rejected);
        (await harness.IsAuthorizationCodeConsumedAsync(code)).Should().BeFalse();
    }

    [TestMethod]
    public async Task ClientSecretPost_WrongBodySecret_IsRejected()
    {
        await using var harness = await Harness.StartAsync();
        var code = await harness.CreateAuthorizationCodeAsync(ConfidentialPost);
        var form = AuthorizationCodeForm(code, ConfidentialPost).ToList();
        form.Add(new KeyValuePair<string, string>(
            "client_secret",
            "wrong-secret-with-at-least-forty-three-characters-123456"));

        var rejected = await harness.PostTokenAsync(form);

        await AssertGenericInvalidClientAsync(rejected);
        (await harness.IsAuthorizationCodeConsumedAsync(code)).Should().BeFalse();
    }

    [TestMethod]
    public async Task ClientSecretBasicClient_StillRejectsBodySecret()
    {
        await using var harness = await Harness.StartAsync();
        var code = await harness.CreateAuthorizationCodeAsync(ConfidentialA);
        var form = AuthorizationCodeForm(code, ConfidentialA).ToList();
        form.Add(new KeyValuePair<string, string>("client_secret", SecretA));

        // A basic-registered client presenting its (correct) secret in the body
        // keeps today's rejection: the transport must match the registration.
        var rejected = await harness.PostTokenAsync(form);

        await AssertGenericInvalidClientAsync(rejected);
        (await harness.IsAuthorizationCodeConsumedAsync(code)).Should().BeFalse();
    }

    [TestMethod]
    public async Task OAuthRefresh_RejectsWrongExpiredAndRevokedCredentialsWithoutIssuingArtifacts()
    {
        await using var harness = await Harness.StartAsync();
        const string ExpiredSecretA = "confidential-a-expired-secret-with-at-least-256-bits-321";
        await harness.AddClientCredentialAsync(ConfidentialA, RotatedSecretA);
        await harness.AddClientCredentialAsync(ConfidentialA, ExpiredSecretA, DateTime.UtcNow.AddMinutes(-1));
        var refreshToken = await harness.CreateRefreshTokenAsync(ConfidentialA);
        var before = await harness.CountIssuedArtifactsAsync();

        var wrong = await harness.PostTokenAsync(
            RefreshForm(refreshToken, ConfidentialA),
            ConfidentialA,
            "wrong-secret-with-at-least-forty-three-characters-123456");
        await AssertGenericInvalidClientAsync(wrong);

        var expired = await harness.PostTokenAsync(
            RefreshForm(refreshToken, ConfidentialA),
            ConfidentialA,
            ExpiredSecretA);
        await AssertGenericInvalidClientAsync(expired);

        await harness.RevokePrimaryCredentialAsync(ConfidentialA);
        var revoked = await harness.PostTokenAsync(
            RefreshForm(refreshToken, ConfidentialA),
            ConfidentialA,
            SecretA);
        await AssertGenericInvalidClientAsync(revoked);

        var crossClientCredentials = await harness.PostTokenAsync(
            RefreshForm(refreshToken),
            ConfidentialB,
            SecretB);
        await AssertGenericInvalidGrantAsync(crossClientCredentials);

        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeFalse();
        (await harness.CountIssuedArtifactsAsync()).Should().Be(before);

        var retained = await harness.PostTokenAsync(
            RefreshForm(refreshToken, ConfidentialA),
            ConfidentialA,
            RotatedSecretA);
        retained.StatusCode.Should().Be(HttpStatusCode.OK, await retained.Content.ReadAsStringAsync());
        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeTrue();
    }

    [TestMethod]
    public async Task OAuthRefresh_ClientSecretPostUsesOnlyItsRegisteredMethod()
    {
        await using var harness = await Harness.StartAsync();
        var refreshToken = await harness.CreateRefreshTokenAsync(ConfidentialPost);

        var missing = await harness.PostTokenAsync(RefreshForm(refreshToken, ConfidentialPost));
        await AssertGenericInvalidClientAsync(missing);

        var basic = await harness.PostTokenAsync(
            RefreshForm(refreshToken, ConfidentialPost),
            ConfidentialPost,
            SecretPost);
        await AssertGenericInvalidClientAsync(basic);

        var wrong = await harness.PostTokenAsync(RefreshForm(
            refreshToken,
            ConfidentialPost,
            "wrong-secret-with-at-least-forty-three-characters-123456"));
        await AssertGenericInvalidClientAsync(wrong);
        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeFalse();

        var accepted = await harness.PostTokenAsync(RefreshForm(refreshToken, ConfidentialPost, SecretPost));
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeTrue();
    }

    [TestMethod]
    [DataRow(ConfidentialA)]
    [DataRow(ConfidentialPost)]
    public async Task JsonRefresh_ConfidentialClientToken_IsInvalidClientBeforeRotation(string clientId)
    {
        await using var harness = await Harness.StartAsync();
        var refreshToken = await harness.CreateRefreshTokenAsync(clientId);
        var before = await harness.CountIssuedArtifactsAsync();

        var missing = await harness.PostJsonRefreshAsync(new { refreshToken });
        await AssertJsonInvalidClientAsync(missing);

        var claimedClient = await harness.PostJsonRefreshAsync(new { refreshToken, clientId });
        await AssertJsonInvalidClientAsync(claimedClient);

        // The compatibility route has no credential channel. Valid credentials in
        // either transport are ignored rather than half-honored.
        var basicHeader = await harness.PostJsonRefreshAsync(
            new { refreshToken, clientId },
            ConfidentialA,
            SecretA);
        await AssertJsonInvalidClientAsync(basicHeader);
        var bodySecret = await harness.PostJsonRefreshAsync(
            new { refreshToken, clientId, clientSecret = SecretPost, client_secret = SecretPost });
        await AssertJsonInvalidClientAsync(bodySecret);

        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeFalse();
        (await harness.CountIssuedArtifactsAsync()).Should().Be(before);
        var auditJson = await harness.GetClientAuthenticationAuditJsonAsync();
        auditJson.Should().Contain(clientId);
        auditJson.Should().NotContain(SecretA);
        auditJson.Should().NotContain(SecretPost);
    }

    [TestMethod]
    public async Task JsonRefresh_CrossClientIdOnConfidentialToken_FailsWithoutRotation()
    {
        await using var harness = await Harness.StartAsync();
        var refreshToken = await harness.CreateRefreshTokenAsync(ConfidentialA);

        // Client-id substitution keeps its existing consistency failure, which
        // this route lets flow to the host's exception pipeline.
        var substituted = async () => await harness.PostJsonRefreshAsync(
            new { refreshToken, clientId = ConfidentialB },
            ConfidentialB,
            SecretB);
        await substituted.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Refresh token was not issued for this client.");

        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeFalse();
    }

    [TestMethod]
    public async Task JsonRefresh_ConsumedConfidentialTokenInsideGraceWindow_DoesNotReleaseCachedTokens()
    {
        await using var harness = await Harness.StartAsync();
        var refreshToken = await harness.CreateRefreshTokenAsync(ConfidentialA);
        var rotated = await harness.PostTokenAsync(
            RefreshForm(refreshToken, ConfidentialA),
            ConfidentialA,
            SecretA);
        var rotatedBody = await rotated.Content.ReadAsStringAsync();
        rotated.StatusCode.Should().Be(HttpStatusCode.OK, rotatedBody);
        using var rotatedJson = JsonDocument.Parse(rotatedBody);
        var successor = rotatedJson.RootElement.GetProperty("refresh_token").GetString()!;
        var before = await harness.CountIssuedArtifactsAsync();

        var replay = await harness.PostJsonRefreshAsync(new { refreshToken });
        await AssertJsonInvalidClientAsync(replay);
        var replayBody = await replay.Content.ReadAsStringAsync();
        replayBody.Should().NotContain(successor);
        replayBody.Should().NotContain(rotatedJson.RootElement.GetProperty("access_token").GetString()!);
        (await harness.CountIssuedArtifactsAsync()).Should().Be(before);

        // A rejected attempt is not an authenticated replay, so the healthy
        // successor is not revoked.
        var next = await harness.PostTokenAsync(
            RefreshForm(successor, ConfidentialA),
            ConfidentialA,
            SecretA);
        next.StatusCode.Should().Be(HttpStatusCode.OK, await next.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task JsonRefresh_PublicClientTokenStillRotatesWithoutSecret()
    {
        await using var harness = await Harness.StartAsync();
        var refreshToken = await harness.CreateRefreshTokenAsync(PublicClient);

        var refreshed = await harness.PostJsonRefreshAsync(new { refreshToken });
        var body = await refreshed.Content.ReadAsStringAsync();
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var successor = json.RootElement.GetProperty("refreshToken").GetString()!;
        json.RootElement.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeTrue();

        var withClientId = await harness.PostJsonRefreshAsync(new { refreshToken = successor, clientId = PublicClient });
        withClientId.StatusCode.Should().Be(HttpStatusCode.OK, await withClientId.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task InProcessRefreshServices_RejectConfidentialClientTokenWithoutAdmission()
    {
        await using var harness = await Harness.StartAsync();
        var refreshToken = await harness.CreateRefreshTokenAsync(ConfidentialA);
        var before = await harness.CountIssuedArtifactsAsync();

        await harness.Invoking(x => x.RefreshInProcessAsync(refreshToken, ConfidentialA))
            .Should().ThrowAsync<SqlOSClientAuthenticationException>();
        await harness.Invoking(x => x.ExchangeRefreshWithoutAdmissionAsync(refreshToken, ConfidentialA))
            .Should().ThrowAsync<SqlOSClientAuthenticationException>();

        (await harness.IsRefreshTokenConsumedAsync(refreshToken)).Should().BeFalse();
        (await harness.CountIssuedArtifactsAsync()).Should().Be(before);

        var publicToken = await harness.CreateRefreshTokenAsync(PublicClient);
        (await harness.RefreshInProcessAsync(publicToken, null)).RefreshToken.Should().NotBeNullOrWhiteSpace();
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

    private static IEnumerable<KeyValuePair<string, string>> RefreshForm(
        string refreshToken,
        string? clientId = null,
        string? clientSecret = null)
    {
        yield return new("grant_type", "refresh_token");
        yield return new("refresh_token", refreshToken);
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            yield return new("client_id", clientId);
        }
        if (clientSecret != null)
        {
            yield return new("client_secret", clientSecret);
        }
    }

    private static async Task AssertJsonInvalidClientAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetString().Should().Be("invalid_client");
        json.RootElement.GetProperty("error_description").GetString().Should().Be("Client authentication failed.");
        json.RootElement.TryGetProperty("accessToken", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("refreshToken", out _).Should().BeFalse();
        body.Should().NotContain(SecretA);
        body.Should().NotContain(SecretPost);
    }

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
        body.Should().NotContain(ConfidentialPost);
        body.Should().NotContain(SecretA);
        body.Should().NotContain(SecretB);
        body.Should().NotContain(SecretPost);
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
                            AddClientSeed(sqlos.AuthServer.ClientSeeds, ConfidentialPost, "Confidential Post", SecretPost, "confidential", "client_secret_post");
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

        public async Task AddClientCredentialAsync(string clientId, string secret, DateTime? expiresAt = null)
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
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt
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

        public async Task<SqlOSTokenResponse> RefreshInProcessAsync(string refreshToken, string? clientId)
        {
            using var scope = _host.Services.CreateScope();
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            return await auth.RefreshAsync(new SqlOSRefreshRequest(refreshToken, null, ClientId: clientId));
        }

        public async Task<SqlOSTokenEndpointResult> ExchangeRefreshWithoutAdmissionAsync(string refreshToken, string clientId)
        {
            using var scope = _host.Services.CreateScope();
            var authorizationServer = scope.ServiceProvider.GetRequiredService<SqlOSAuthorizationServerService>();
            return await authorizationServer.ExchangeAuthorizationCodeAsync(
                new SqlOSTokenRequest("refresh_token", null, null, clientId, null, refreshToken, null),
                new DefaultHttpContext());
        }

        /// <summary>
        /// Counts every artifact a refresh could issue, so a rejected attempt can
        /// prove it minted nothing.
        /// </summary>
        public async Task<(int RefreshTokens, int Sessions, int AuthorizationCodes)> CountIssuedArtifactsAsync()
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            return (
                await context.Set<SqlOSRefreshToken>().CountAsync(),
                await context.Set<SqlOSSession>().CountAsync(),
                await context.Set<SqlOSAuthorizationCode>().CountAsync());
        }

        public async Task<HttpResponseMessage> PostJsonRefreshAsync(
            object body,
            string? basicClientId = null,
            string? basicSecret = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/token/refresh")
            {
                Content = JsonContent.Create(body)
            };
            if (basicClientId != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basicClientId}:{basicSecret}")));
            }
            return await _client.SendAsync(request);
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
            string clientType,
            string? tokenEndpointAuthMethod = null)
        {
            seeds.Add(new()
            {
                ClientId = clientId,
                Name = name,
                Audience = "sqlos",
                ClientType = clientType,
                TokenEndpointAuthMethod = tokenEndpointAuthMethod,
                RequirePkce = true,
                RedirectUris = [RedirectUri],
                AllowedScopes = ["openid", "offline_access"],
                ClientSecretResolver = secret == null ? null : () => secret
            });
        }
    }
}
