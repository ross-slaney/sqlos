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
            .WithMessage("*validation failed*");
        httpFactory.RequestCount.Should().Be(0);
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.validation-failed"
            && (x.DataJson ?? string.Empty).Contains("Trust policy rejected client."))).Should().BeTrue();
    }

    [DataTestMethod]
    [DataRow("https://localhost/oauth/client.json")]
    [DataRow("https://127.0.0.1/oauth/client.json")]
    [DataRow("https://[::1]/oauth/client.json")]
    [DataRow("https://10.0.0.1/oauth/client.json")]
    [DataRow("https://172.16.0.1/oauth/client.json")]
    [DataRow("https://192.168.0.1/oauth/client.json")]
    [DataRow("https://169.254.0.1/oauth/client.json")]
    [DataRow("https://224.0.0.1/oauth/client.json")]
    [DataRow("https://240.0.0.1/oauth/client.json")]
    [DataRow("https://[fe80::1]/oauth/client.json")]
    [DataRow("https://[ff02::1]/oauth/client.json")]
    public async Task ResolveRequiredClientAsync_RejectsUnsafeMetadataHostsBeforeNetworkIo(string clientId)
    {
        using var context = CreateContext();
        var options = CreateOptions();
        var clientIdUri = new Uri(clientId);
        options.ClientRegistration.Cimd.TrustedHosts.Clear();
        options.ClientRegistration.Cimd.TrustedHosts.Add(clientIdUri.Host);
        var httpFactory = new FakeHttpClientFactory(_ => throw new InvalidOperationException("Network I/O should not happen for unsafe CIMD hosts."));
        var resolver = CreateResolver(context, options, httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(clientId, "https://client.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*validation failed*");
        httpFactory.RequestCount.Should().Be(0);
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.validation-failed"
            && (x.DataJson ?? string.Empty).Contains("not allowed"))).Should().BeTrue();
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsUntrustedMetadataHostsBeforeNetworkIo()
    {
        using var context = CreateContext();
        var httpFactory = new FakeHttpClientFactory(_ => throw new InvalidOperationException("Network I/O should not happen for untrusted CIMD hosts."));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(
            "https://untrusted.example.test/oauth/client.json",
            "https://untrusted.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*validation failed*");
        httpFactory.RequestCount.Should().Be(0);
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.validation-failed"
            && (x.DataJson ?? string.Empty).Contains("not trusted"))).Should().BeTrue();
    }

    [TestMethod]
    public async Task SecureTransport_RejectsAllowedHostnameWhenDnsResolvesToPrivateAddress()
    {
        var resolvedHosts = new List<string>();
        using var handler = SqlOSCimdHttpHandlerFactory.Create((host, _) =>
        {
            resolvedHosts.Add(host);
            return ValueTask.FromResult(new[] { IPAddress.Parse("127.0.0.1") });
        });
        using var client = new HttpClient(handler);

        var act = async () => await client.GetAsync("http://metadata.example.test/client.json");

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*non-public address*");
        resolvedHosts.Should().Equal("metadata.example.test");
    }

    [TestMethod]
    public async Task SecureTransport_RejectsMixedPublicAndPrivateDnsAnswers()
    {
        using var handler = SqlOSCimdHttpHandlerFactory.Create((_, _) =>
            ValueTask.FromResult(new[]
            {
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("10.0.0.12")
            }));
        using var client = new HttpClient(handler);

        var act = async () => await client.GetAsync("http://metadata.example.test/client.json");

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*non-public address*");
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsRedirectResponsesWithoutFollowing()
    {
        using var context = CreateContext();
        var httpFactory = new FakeHttpClientFactory(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://client.example.test/next-client.json");
            return response;
        });
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(
            "https://client.example.test/oauth/client.json",
            "https://client.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*redirect*not allowed*");
        httpFactory.RequestCount.Should().Be(1);
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.fetch-failed"
            && (x.DataJson ?? string.Empty).Contains("redirect"))).Should().BeTrue();
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsOversizedMetadataWhileStreaming()
    {
        using var context = CreateContext();
        var options = CreateOptions();
        options.ClientRegistration.Cimd.MaxMetadataBytes = 64;
        var body = $$"""
        {
          "client_id": "https://client.example.test/oauth/client.json",
          "client_name": "{{new string('A', 512)}}",
          "redirect_uris": ["https://client.example.test/callback"],
          "token_endpoint_auth_method": "none"
        }
        """;
        var stream = new TrackingReadStream(Encoding.UTF8.GetBytes(body));
        var httpFactory = new FakeHttpClientFactory(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        });
        var resolver = CreateResolver(context, options, httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(
            "https://client.example.test/oauth/client.json",
            "https://client.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*validation failed*");
        stream.BytesRead.Should().BeLessThan(stream.LengthBytes);
        httpFactory.RequestCount.Should().Be(1);
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.validation-failed"
            && (x.DataJson ?? string.Empty).Contains("exceeds the allowed size"))).Should().BeTrue();
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsUntrustedHostBeforeFetch()
    {
        using var context = CreateContext();
        var options = CreateOptions();
        options.ClientRegistration.Cimd.TrustedHosts.Add("trusted.example.test");
        var httpFactory = new FakeHttpClientFactory(_ =>
            throw new InvalidOperationException("Untrusted metadata must not be fetched."));
        var resolver = CreateResolver(context, options, httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(
            "https://untrusted.example.test/oauth/client.json",
            "https://untrusted.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*validation failed*");
        httpFactory.RequestCount.Should().Be(0);
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.validation-failed"
            && (x.DataJson ?? string.Empty).Contains("not trusted"))).Should().BeTrue();
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
            .WithMessage("*validation failed*");
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.validation-failed"
            && (x.DataJson ?? string.Empty).Contains("exact same client_id"))).Should().BeTrue();
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
            .WithMessage("*validation failed*");
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.validation-failed"
            && (x.DataJson ?? string.Empty).Contains("Redirect URI"))).Should().BeTrue();
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsCimdRedirectPathCaseMismatch()
    {
        using var context = CreateContext();
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            """
            {
              "client_id": "https://client.example.test/oauth/client.json",
              "client_name": "Exact Redirect MCP Client",
              "redirect_uris": ["https://client.example.test/Auth/Callback"],
              "token_endpoint_auth_method": "none"
            }
            """));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(
            "https://client.example.test/oauth/client.json",
            "https://client.example.test/auth/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*validation failed*");
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.validation-failed"
            && (x.DataJson ?? string.Empty).Contains("Redirect URI"))).Should().BeTrue();
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
            .WithMessage("*validation failed*");
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.validation-failed"
            && (x.DataJson ?? string.Empty).Contains("must be JSON"))).Should().BeTrue();
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
    {
        var options = new SqlOSAuthServerOptions
        {
            Issuer = "https://app.example.com/sqlos/auth",
            PublicOrigin = "https://app.example.com"
        };
        options.ClientRegistration.Cimd.Enabled = true;
        options.ClientRegistration.Cimd.TrustedHosts.Add("client.example.test");
        return options;
    }

    private static SqlOSClientResolutionService CreateResolver(
        TestSqlOSInMemoryDbContext context,
        SqlOSAuthServerOptions optionsValue,
        IHttpClientFactory httpClientFactory)
    {
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
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

    private sealed class TrackingReadStream : Stream
    {
        private readonly byte[] _payload;
        private int _position;

        public TrackingReadStream(byte[] payload)
        {
            _payload = payload;
        }

        public int BytesRead { get; private set; }

        public int LengthBytes => _payload.Length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesToRead = Math.Min(count, _payload.Length - _position);
            if (bytesToRead <= 0)
            {
                return 0;
            }

            _payload.AsSpan(_position, bytesToRead).CopyTo(buffer.AsSpan(offset, bytesToRead));
            _position += bytesToRead;
            BytesRead += bytesToRead;
            return bytesToRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesToRead = Math.Min(buffer.Length, _payload.Length - _position);
            if (bytesToRead <= 0)
            {
                return ValueTask.FromResult(0);
            }

            _payload.AsMemory(_position, bytesToRead).CopyTo(buffer);
            _position += bytesToRead;
            BytesRead += bytesToRead;
            return ValueTask.FromResult(bytesToRead);
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
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
