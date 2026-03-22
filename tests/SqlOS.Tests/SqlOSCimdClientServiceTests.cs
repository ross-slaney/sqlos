using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSCimdClientServiceTests
{
    [TestMethod]
    public async Task ResolveRequiredClientAsync_FetchesAndStoresDiscoveredClient()
    {
        using var context = CreateContext();
        const string clientId = "https://client.example.test/oauth/client.json";
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            """
            {
              "client_id": "https://client.example.test/oauth/client.json",
              "client_name": "Example MCP Client",
              "redirect_uris": ["https://client.example.test/callback"],
              "token_endpoint_auth_method": "none",
              "grant_types": ["authorization_code", "refresh_token"],
              "response_types": ["code"],
              "client_uri": "https://client.example.test",
              "logo_uri": "https://client.example.test/logo.png",
              "software_id": "example-client",
              "software_version": "1.0.0"
            }
            """,
            cacheSeconds: 300,
            etag: "\"v1\""));

        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var resolved = await resolver.ResolveRequiredClientAsync(clientId, "https://client.example.test/callback");

        resolved.ResolutionKind.Should().Be("cimd");
        resolved.Client.RegistrationSource.Should().Be("cimd");
        resolved.Client.MetadataDocumentUrl.Should().Be(clientId);
        resolved.Client.TokenEndpointAuthMethod.Should().Be("none");
        resolved.Client.ClientUri.Should().Be("https://client.example.test");
        resolved.Client.LogoUri.Should().Be("https://client.example.test/logo.png");
        resolved.Client.SoftwareId.Should().Be("example-client");
        resolved.Client.SoftwareVersion.Should().Be("1.0.0");
        resolved.Client.MetadataFetchedAt.Should().NotBeNull();
        resolved.Client.MetadataExpiresAt.Should().NotBeNull();
        resolved.Client.MetadataEtag.Should().Be("\"v1\"");
        resolved.Client.RedirectUrisJson.Should().Contain("https://client.example.test/callback");
        httpFactory.RequestCount.Should().Be(1);
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_UsesFreshCachedCimdClientWithoutRefetch()
    {
        using var context = CreateContext();
        var existing = new SqlOSClientApplication
        {
            Id = "cli_cached",
            ClientId = "https://client.example.test/oauth/client.json",
            Name = "Cached Client",
            Audience = "sqlos",
            ClientType = "public_pkce",
            RegistrationSource = "cimd",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            MetadataDocumentUrl = "https://client.example.test/oauth/client.json",
            MetadataFetchedAt = DateTime.UtcNow.AddMinutes(-1),
            MetadataExpiresAt = DateTime.UtcNow.AddMinutes(10),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.Set<SqlOSClientApplication>().Add(existing);
        await context.SaveChangesAsync();

        var httpFactory = new FakeHttpClientFactory(_ => throw new InvalidOperationException("CIMD metadata should not be fetched while cache is fresh."));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var resolved = await resolver.ResolveRequiredClientAsync(existing.ClientId, "https://client.example.test/callback");

        resolved.Client.Id.Should().Be(existing.Id);
        resolved.ResolutionKind.Should().Be("cimd");
        httpFactory.RequestCount.Should().Be(0);
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RevalidatesExpiredCimdClient_With304()
    {
        using var context = CreateContext();
        var existing = new SqlOSClientApplication
        {
            Id = "cli_304",
            ClientId = "https://client.example.test/oauth/client.json",
            Name = "Cached Client",
            Audience = "sqlos",
            ClientType = "public_pkce",
            RegistrationSource = "cimd",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            MetadataDocumentUrl = "https://client.example.test/oauth/client.json",
            MetadataFetchedAt = DateTime.UtcNow.AddHours(-2),
            MetadataExpiresAt = DateTime.UtcNow.AddMinutes(-5),
            MetadataEtag = "\"v1\"",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.Set<SqlOSClientApplication>().Add(existing);
        await context.SaveChangesAsync();

        var httpFactory = new FakeHttpClientFactory(request =>
        {
            request.Headers.IfNoneMatch.Should().ContainSingle();
            var response = new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };
            return response;
        });

        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var resolved = await resolver.ResolveRequiredClientAsync(existing.ClientId, "https://client.example.test/callback");

        resolved.Client.Id.Should().Be(existing.Id);
        resolved.ResolutionKind.Should().Be("cimd");
        resolved.Client.MetadataExpiresAt.Should().BeAfter(DateTime.UtcNow);
        httpFactory.RequestCount.Should().Be(1);
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsTrustPolicyDenial()
    {
        using var context = CreateContext();
        var options = CreateOptions();
        options.ClientRegistration.Cimd.TrustPolicy = (_, _) =>
            Task.FromResult(SqlOSClientRegistrationPolicyDecision.Deny("Trust policy rejected client."));
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            """
            {
              "client_id": "https://client.example.test/oauth/client.json",
              "client_name": "Example MCP Client",
              "redirect_uris": ["https://client.example.test/callback"],
              "token_endpoint_auth_method": "none"
            }
            """));

        var resolver = CreateResolver(context, options, httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(
            "https://client.example.test/oauth/client.json",
            "https://client.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Trust policy rejected client.*");
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsMismatchedClientId()
    {
        using var context = CreateContext();
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            """
            {
              "client_id": "https://evil.example.test/oauth/client.json",
              "client_name": "Mismatch",
              "redirect_uris": ["https://client.example.test/callback"],
              "token_endpoint_auth_method": "none"
            }
            """));

        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(
            "https://client.example.test/oauth/client.json",
            "https://client.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exact same client_id*");
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.validation-failed")).Should().BeTrue();
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsRedirectMismatch()
    {
        using var context = CreateContext();
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            """
            {
              "client_id": "https://client.example.test/oauth/client.json",
              "client_name": "Example MCP Client",
              "redirect_uris": ["https://client.example.test/other-callback"],
              "token_endpoint_auth_method": "none"
            }
            """));

        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(
            "https://client.example.test/oauth/client.json",
            "https://client.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Redirect URI*");
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_UsesMetadataChangesToFlagFreshConsent()
    {
        using var context = CreateContext();
        var existing = new SqlOSClientApplication
        {
            Id = "cli_changed",
            ClientId = "https://client.example.test/oauth/client.json",
            Name = "Cached Client",
            Audience = "sqlos",
            ClientType = "public_pkce",
            RegistrationSource = "cimd",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            MetadataDocumentUrl = "https://client.example.test/oauth/client.json",
            MetadataFetchedAt = DateTime.UtcNow.AddHours(-2),
            MetadataExpiresAt = DateTime.UtcNow.AddMinutes(-5),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.Set<SqlOSClientApplication>().Add(existing);
        context.Set<SqlOSSession>().Add(new SqlOSSession
        {
            Id = "sess_changed",
            UserId = "usr_1",
            ClientApplicationId = existing.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-1),
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        context.Set<SqlOSRefreshToken>().Add(new SqlOSRefreshToken
        {
            Id = "rt_changed",
            SessionId = "sess_changed",
            TokenHash = "hash_changed",
            FamilyId = "fam_changed",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        });
        await context.SaveChangesAsync();

        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            """
            {
              "client_id": "https://client.example.test/oauth/client.json",
              "client_name": "Example MCP Client",
              "redirect_uris": [
                "https://client.example.test/callback",
                "https://client.example.test/new-callback"
              ],
              "token_endpoint_auth_method": "none"
            }
            """));

        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var resolved = await resolver.ResolveRequiredClientAsync(
            "https://client.example.test/oauth/client.json",
            "https://client.example.test/callback");

        resolved.RequiresFreshConsent.Should().BeTrue();
        resolved.Client.RedirectUrisJson.Should().Contain("https://client.example.test/new-callback");
        var session = await context.Set<SqlOSSession>().SingleAsync(x => x.Id == "sess_changed");
        var refreshToken = await context.Set<SqlOSRefreshToken>().SingleAsync(x => x.Id == "rt_changed");
        session.RevokedAt.Should().NotBeNull();
        session.RevocationReason.Should().Be("cimd_metadata_changed");
        refreshToken.RevokedAt.Should().NotBeNull();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.metadata-changed")).Should().BeTrue();
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsInvalidContentType()
    {
        using var context = CreateContext();
        var httpFactory = new FakeHttpClientFactory(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "text/plain")
            };
            return response;
        });

        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(
            "https://client.example.test/oauth/client.json",
            "https://client.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be JSON*");
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_AuditsFetchFailure()
    {
        using var context = CreateContext();
        var httpFactory = new FakeHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(
            "https://client.example.test/oauth/client.json",
            "https://client.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*fetch failed*");
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.fetch-failed")).Should().BeTrue();
    }

    private static SqlOSAuthServerOptions CreateOptions()
        => new()
        {
            Issuer = "https://app.example.com/sqlos/auth",
            PublicOrigin = "https://app.example.com"
        };

    private static SqlOSClientResolutionService CreateResolver(
        TestSqlOSInMemoryDbContext context,
        SqlOSAuthServerOptions optionsValue,
        IHttpClientFactory httpClientFactory)
    {
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
        var cimd = new SqlOSCimdClientService(context, options, httpClientFactory, crypto);
        return new SqlOSClientResolutionService(context, options, cimd);
    }

    private static HttpResponseMessage JsonResponse(string json, int? cacheSeconds = null, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (cacheSeconds.HasValue)
        {
            response.Headers.CacheControl = new CacheControlHeaderValue
            {
                MaxAge = TimeSpan.FromSeconds(cacheSeconds.Value)
            };
        }

        if (!string.IsNullOrWhiteSpace(etag))
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }

        return response;
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public int RequestCount { get; private set; }

        public HttpClient CreateClient(string name)
            => new(new DelegateHandler(request =>
            {
                RequestCount++;
                return _handler(request);
            }))
            {
                BaseAddress = new Uri("https://client.example.test")
            };
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
