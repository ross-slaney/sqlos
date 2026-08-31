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
    public async Task ResolveRequiredClientAsync_PortableModeDoesNotRequireHostAllowlist()
    {
        using var context = CreateContext();
        var options = CreateOptions();
        options.ClientRegistration.Cimd.TrustedHosts.Clear();
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            """
            {
              "client_id": "https://portable.example.test/oauth/client.json",
              "client_name": "Portable Client",
              "redirect_uris": ["https://portable.example.test/callback"],
              "token_endpoint_auth_method": "none"
            }
            """));
        var resolver = CreateResolver(context, options, httpFactory);

        var resolved = await resolver.ResolveRequiredClientAsync(
            "https://portable.example.test/oauth/client.json",
            "https://portable.example.test/callback");

        resolved.ResolutionKind.Should().Be("cimd");
        httpFactory.RequestCount.Should().Be(1);
    }

    [DataTestMethod]
    [DataRow("http://client.example.test/callback")]
    [DataRow("ftp://client.example.test/callback")]
    [DataRow("custom-scheme://client.example.test/callback")]
    [DataRow("ftp://localhost/callback")]
    public async Task ResolveRequiredClientAsync_RejectsUnsafeRedirectUriSchemes(string redirectUri)
    {
        using var context = CreateContext();
        const string clientId = "https://client.example.test/oauth/client.json";
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            $$"""
            {
              "client_id": "{{clientId}}",
              "client_name": "Unsafe Redirect Client",
              "redirect_uris": ["{{redirectUri}}"],
              "token_endpoint_auth_method": "none"
            }
            """));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(clientId, redirectUri);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Client metadata document validation failed.");
        var audit = await context.Set<SqlOSAuditEvent>()
            .SingleAsync(x => x.EventType == "client.cimd.validation-failed");
        audit.DataJson.Should().Contain("must use HTTPS or an HTTP loopback address");
        (await context.Set<SqlOSClientApplication>().AnyAsync()).Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("http://localhost/callback")]
    [DataRow("http://127.0.0.1:49152/callback")]
    [DataRow("http://[::1]:49152/callback")]
    public async Task ResolveRequiredClientAsync_AllowsHttpLoopbackRedirectUris(string redirectUri)
    {
        using var context = CreateContext();
        const string clientId = "https://client.example.test/oauth/client.json";
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            $$"""
            {
              "client_id": "{{clientId}}",
              "client_name": "Native Client",
              "redirect_uris": ["{{redirectUri}}"],
              "token_endpoint_auth_method": "none"
            }
            """));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var resolved = await resolver.ResolveRequiredClientAsync(clientId, redirectUri);

        resolved.Client.RedirectUrisJson.Should().Contain(redirectUri);
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_AllowsEphemeralLoopbackPortForPortlessRegistration()
    {
        // Codex CIMD documents register a portless loopback redirect and bind an
        // ephemeral port at authorization time (RFC 8252 §7.3).
        using var context = CreateContext();
        const string clientId = "https://client.example.test/oauth/codex/abc123/client.json";
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            $$"""
            {
              "client_id": "{{clientId}}",
              "client_name": "Codex-Shaped Client",
              "redirect_uris": ["http://127.0.0.1/callback/abc123"],
              "token_endpoint_auth_method": "none"
            }
            """));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var resolved = await resolver.ResolveRequiredClientAsync(clientId, "http://127.0.0.1:49152/callback/abc123");

        resolved.ResolutionKind.Should().Be("cimd");
        resolved.Client.RedirectUrisJson.Should().Contain("http://127.0.0.1/callback/abc123");
    }

    [DataTestMethod]
    [DataRow("http://127.0.0.1:49152/callback/other")]
    [DataRow("http://127.0.0.1:49152/callback/abc123?extra=1")]
    [DataRow("http://[::1]:49152/callback/abc123")]
    public async Task ResolveRequiredClientAsync_RejectsLoopbackPathHostOrQueryMismatch(string redirectUri)
    {
        using var context = CreateContext();
        const string clientId = "https://client.example.test/oauth/codex/abc123/client.json";
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            $$"""
            {
              "client_id": "{{clientId}}",
              "client_name": "Codex-Shaped Client",
              "redirect_uris": ["http://127.0.0.1/callback/abc123"],
              "token_endpoint_auth_method": "none"
            }
            """));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(clientId, redirectUri);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Client metadata document validation failed.");
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_DoesNotRelaxPortsForLocalhostHostname()
    {
        using var context = CreateContext();
        const string clientId = "https://client.example.test/oauth/client.json";
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            $$"""
            {
              "client_id": "{{clientId}}",
              "client_name": "Localhost Client",
              "redirect_uris": ["http://localhost/callback"],
              "token_endpoint_auth_method": "none"
            }
            """));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(clientId, "http://localhost:49152/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Client metadata document validation failed.");
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_AllowsEphemeralLoopbackPortFromFreshCache()
    {
        using var context = CreateContext();
        var existing = new SqlOSClientApplication
        {
            Id = "cli_cached_loopback",
            ClientId = "https://client.example.test/oauth/codex/abc123/client.json",
            Name = "Cached Codex-Shaped Client",
            Audience = "sqlos",
            ClientType = "public_pkce",
            RegistrationSource = "cimd",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]",
            RedirectUrisJson = "[\"http://127.0.0.1/callback/abc123\"]",
            MetadataDocumentUrl = "https://client.example.test/oauth/codex/abc123/client.json",
            MetadataFetchedAt = DateTime.UtcNow.AddMinutes(-1),
            MetadataExpiresAt = DateTime.UtcNow.AddMinutes(10),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.Set<SqlOSClientApplication>().Add(existing);
        await context.SaveChangesAsync();

        var httpFactory = new FakeHttpClientFactory(_ => throw new InvalidOperationException("CIMD metadata should not be fetched while cache is fresh."));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var resolved = await resolver.ResolveRequiredClientAsync(existing.ClientId, "http://127.0.0.1:60001/callback/abc123");

        resolved.Client.Id.Should().Be(existing.Id);
        resolved.ResolutionKind.Should().Be("cimd");
        httpFactory.RequestCount.Should().Be(0);
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsEphemeralLoopbackPortWhenLoopbackDisabled()
    {
        using var context = CreateContext();
        const string clientId = "https://client.example.test/oauth/codex/abc123/client.json";
        var options = CreateOptions();
        options.ClientRegistration.Dcr.AllowLoopbackRedirectUris = false;
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            $$"""
            {
              "client_id": "{{clientId}}",
              "client_name": "Codex-Shaped Client",
              "redirect_uris": ["http://127.0.0.1/callback/abc123"],
              "token_endpoint_auth_method": "none"
            }
            """));
        var resolver = CreateResolver(context, options, httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(clientId, "http://127.0.0.1:49152/callback/abc123");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Client metadata document validation failed.");
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsLoopbackWhenDisabledForDcrAndCimd()
    {
        using var context = CreateContext();
        const string clientId = "https://client.example.test/oauth/client.json";
        const string redirectUri = "http://127.0.0.1:49152/callback";
        var options = CreateOptions();
        options.ClientRegistration.Dcr.AllowLoopbackRedirectUris = false;
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            $$"""
            {
              "client_id": "{{clientId}}",
              "client_name": "Native Client",
              "redirect_uris": ["{{redirectUri}}"],
              "token_endpoint_auth_method": "none"
            }
            """));
        var resolver = CreateResolver(context, options, httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(clientId, redirectUri);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Client metadata document validation failed.");
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
    public async Task ResolveRequiredClientAsync_RejectsUnsafeRedirectFromFreshCacheWithoutRefetch()
    {
        using var context = CreateContext();
        var existing = new SqlOSClientApplication
        {
            Id = "cli_cached_unsafe",
            ClientId = "https://client.example.test/oauth/client.json",
            Name = "Cached Unsafe Client",
            Audience = "sqlos",
            ClientType = "public_pkce",
            RegistrationSource = "cimd",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]",
            RedirectUrisJson = "[\"http://attacker.example.test/callback\"]",
            MetadataDocumentUrl = "https://client.example.test/oauth/client.json",
            MetadataFetchedAt = DateTime.UtcNow.AddMinutes(-1),
            MetadataExpiresAt = DateTime.UtcNow.AddMinutes(10),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.Set<SqlOSClientApplication>().Add(existing);
        await context.SaveChangesAsync();
        var httpFactory = new FakeHttpClientFactory(_ => throw new InvalidOperationException("Unsafe cached metadata must not be fetched or accepted."));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(existing.ClientId, "http://attacker.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Client metadata document validation failed.");
        httpFactory.RequestCount.Should().Be(0);
        var audit = await context.Set<SqlOSAuditEvent>()
            .SingleAsync(x => x.EventType == "client.cimd.validation-failed");
        audit.DataJson.Should().Contain("must use HTTPS or an HTTP loopback address");
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
    public async Task ResolveRequiredClientAsync_RejectsUnsafeCachedRedirectAfterNotModifiedResponse()
    {
        using var context = CreateContext();
        var existing = new SqlOSClientApplication
        {
            Id = "cli_cached_unsafe_304",
            ClientId = "https://client.example.test/oauth/client.json",
            Name = "Cached Unsafe Client",
            Audience = "sqlos",
            ClientType = "public_pkce",
            RegistrationSource = "cimd",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]",
            RedirectUrisJson = "[\"http://attacker.example.test/callback\"]",
            MetadataDocumentUrl = "https://client.example.test/oauth/client.json",
            MetadataFetchedAt = DateTime.UtcNow.AddHours(-2),
            MetadataExpiresAt = DateTime.UtcNow.AddMinutes(-1),
            MetadataEtag = "\"unsafe-v1\"",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.Set<SqlOSClientApplication>().Add(existing);
        await context.SaveChangesAsync();
        var httpFactory = new FakeHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var act = async () => await resolver.ResolveRequiredClientAsync(
            existing.ClientId,
            "http://attacker.example.test/callback");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Client metadata document validation failed.");
        httpFactory.RequestCount.Should().Be(1);
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.validation-failed"))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_RejectsTrustPolicyDenial()
    {
        using var context = CreateContext();
        var options = CreateOptions();
        SqlOSCimdTrustContext? observedContext = null;
        options.ClientRegistration.Cimd.TrustPolicy = (trustContext, _) =>
        {
            observedContext = trustContext;
            return Task.FromResult(SqlOSClientRegistrationPolicyDecision.Deny("Trust policy rejected client."));
        };
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
        httpFactory.RequestCount.Should().Be(1);
        observedContext.Should().NotBeNull();
        observedContext!.ClientName.Should().Be("Example MCP Client");
        observedContext.RedirectUris.Should().Equal("https://client.example.test/callback");
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
    public void SecureTransport_DisablesRedirectsAndAmbientProxyRouting()
    {
        using var handler = SqlOSCimdHttpHandlerFactory.Create();

        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseProxy.Should().BeFalse();
        handler.ConnectCallback.Should().NotBeNull();
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
            .WithMessage("*fetch failed*");
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
    public async Task ResolveRequiredClientAsync_MetadataChange_RevokesConsentGrantsAndAuditsInvalidation()
    {
        using var context = CreateContext();
        var existing = new SqlOSClientApplication
        {
            Id = "cli_grant_changed",
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
        context.Set<SqlOSConsentGrant>().Add(new SqlOSConsentGrant
        {
            Id = "cgr_changed",
            UserId = "usr_1",
            ClientApplicationId = existing.Id,
            Scope = "openid profile",
            GrantedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
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
        var grant = await context.Set<SqlOSConsentGrant>().SingleAsync(x => x.Id == "cgr_changed");
        grant.RevokedAt.Should().NotBeNull();
        grant.RevocationReason.Should().Be("cimd_metadata_changed");
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "oauth.consent.invalidated")).Should().BeTrue();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.metadata-changed")).Should().BeTrue();
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_WidenedScope_RevokesSessionsAndAuditsMetadataChange()
    {
        using var context = CreateContext();
        var existing = new SqlOSClientApplication
        {
            Id = "cli_scope_changed",
            ClientId = "https://client.example.test/oauth/client.json",
            Name = "Cached Client",
            Audience = "sqlos",
            ClientType = "public_pkce",
            RegistrationSource = "cimd",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]",
            AllowedScopesJson = "[\"openid\"]",
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
            Id = "sess_scope_changed",
            UserId = "usr_1",
            ClientApplicationId = existing.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-1),
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        context.Set<SqlOSRefreshToken>().Add(new SqlOSRefreshToken
        {
            Id = "rt_scope_changed",
            SessionId = "sess_scope_changed",
            TokenHash = "hash_scope_changed",
            FamilyId = "fam_scope_changed",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        });
        await context.SaveChangesAsync();

        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            """
            {
              "client_id": "https://client.example.test/oauth/client.json",
              "client_name": "Example MCP Client",
              "redirect_uris": ["https://client.example.test/callback"],
              "token_endpoint_auth_method": "none",
              "scope": "openid profile"
            }
            """));

        var resolver = CreateResolver(context, CreateOptions(), httpFactory);

        var resolved = await resolver.ResolveRequiredClientAsync(
            "https://client.example.test/oauth/client.json",
            "https://client.example.test/callback");

        resolved.RequiresFreshConsent.Should().BeTrue();
        resolved.Client.AllowedScopesJson.Should().Contain("profile");
        var session = await context.Set<SqlOSSession>().SingleAsync(x => x.Id == "sess_scope_changed");
        var refreshToken = await context.Set<SqlOSRefreshToken>().SingleAsync(x => x.Id == "rt_scope_changed");
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

    [TestMethod]
    public async Task ResolveClientAsync_SecuritySensitiveChange_CommitsMetadataAndRevocationsInOneSave()
    {
        using var context = CreateContext();
        var existing = new SqlOSClientApplication
        {
            Id = "cli_atomic",
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
            Id = "sess_atomic",
            UserId = "usr_1",
            ClientApplicationId = existing.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-1),
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        context.Set<SqlOSRefreshToken>().Add(new SqlOSRefreshToken
        {
            Id = "rt_atomic",
            SessionId = "sess_atomic",
            TokenHash = "hash_atomic",
            FamilyId = "fam_atomic",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        });
        context.Set<SqlOSConsentGrant>().Add(new SqlOSConsentGrant
        {
            Id = "cgr_atomic",
            UserId = "usr_1",
            ClientApplicationId = existing.Id,
            Scope = "openid profile",
            GrantedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
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
            """,
            cacheSeconds: 300));
        var options = Options.Create(CreateOptions());
        var cimd = new SqlOSCimdClientService(context, options, httpFactory, TestCryptoService.Create(context, options));

        var savesBefore = context.SaveChangesAsyncCallCount;
        var resolved = await cimd.ResolveClientAsync(
            existing.ClientId,
            "https://client.example.test/callback",
            existing);

        resolved.Should().NotBeNull();
        resolved!.RequiresFreshConsent.Should().BeTrue();
        context.SaveChangesAsyncCallCount.Should().Be(
            savesBefore + 1,
            "the metadata commit and the session/grant invalidation must share one save so no "
            + "failure window can leave refreshed metadata with stale grants alive");

        var client = await context.Set<SqlOSClientApplication>().SingleAsync(x => x.Id == existing.Id);
        client.RedirectUrisJson.Should().Contain("https://client.example.test/new-callback");
        client.MetadataExpiresAt.Should().BeAfter(DateTime.UtcNow, "the refreshed expiry committed in the same save");
        (await context.Set<SqlOSSession>().SingleAsync(x => x.Id == "sess_atomic")).RevokedAt.Should().NotBeNull();
        (await context.Set<SqlOSRefreshToken>().SingleAsync(x => x.Id == "rt_atomic")).RevokedAt.Should().NotBeNull();
        var grant = await context.Set<SqlOSConsentGrant>().SingleAsync(x => x.Id == "cgr_atomic");
        grant.RevokedAt.Should().NotBeNull();
        grant.RevocationReason.Should().Be("cimd_metadata_changed");
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cimd.metadata-changed")).Should().BeTrue();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "oauth.consent.invalidated")).Should().BeTrue();
    }

    [TestMethod]
    public void ComputeSensitiveMetadataFingerprint_TracksOnlySecuritySensitiveFields()
    {
        var client = new SqlOSClientApplication
        {
            Id = "cli_fp",
            ClientId = "fingerprint-client",
            Name = "Fingerprint Client",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            AllowedScopesJson = "[\"openid\",\"todo:read\"]"
        };
        var baseline = SqlOSCimdClientService.ComputeSensitiveMetadataFingerprint(client);

        client.Name = "Renamed Cosmetically";
        client.LogoUri = "https://client.example.test/logo.png";
        SqlOSCimdClientService.ComputeSensitiveMetadataFingerprint(client).Should().Be(
            baseline,
            "cosmetic metadata must not invalidate pending consent");

        client.RedirectUrisJson = "[\"https://client.example.test/callback\",\"https://client.example.test/new\"]";
        SqlOSCimdClientService.ComputeSensitiveMetadataFingerprint(client).Should().NotBe(
            baseline,
            "redirect URIs are security-sensitive");
    }

    [TestMethod]
    public void ComputeSensitiveMetadataFingerprint_IsInsensitiveToSetOrderAndDuplicates()
    {
        var client = new SqlOSClientApplication
        {
            Id = "cli_fp_order",
            ClientId = "fingerprint-order-client",
            Name = "Fingerprint Order Client",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]",
            RedirectUrisJson = "[\"https://client.example.test/a\",\"https://client.example.test/b\"]",
            AllowedScopesJson = "[\"openid\",\"todo:read\"]"
        };
        var baseline = SqlOSCimdClientService.ComputeSensitiveMetadataFingerprint(client);

        client.RedirectUrisJson = "[\"https://client.example.test/b\",\"https://client.example.test/a\"]";
        client.GrantTypesJson = "[\"refresh_token\",\"authorization_code\",\"authorization_code\"]";
        client.AllowedScopesJson = "[\"todo:read\",\"openid\"]";
        SqlOSCimdClientService.ComputeSensitiveMetadataFingerprint(client).Should().Be(
            baseline,
            "set-valued metadata is canonicalized, so order-only (or duplicate-only) refreshes must not "
            + "invalidate pending consent tokens or remembered grants");
    }

    [TestMethod]
    public async Task ResolveClientAsync_OrderOnlyMetadataReorder_DoesNotTripTheSensitiveChangeRevocation()
    {
        using var context = CreateContext();
        var existing = new SqlOSClientApplication
        {
            Id = "cli_reorder",
            ClientId = "https://client.example.test/oauth/client.json",
            Name = "Reorder Client",
            Audience = "sqlos",
            ClientType = "public_pkce",
            RegistrationSource = "cimd",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]",
            RedirectUrisJson = "[\"https://client.example.test/callback\",\"https://client.example.test/alt\"]",
            MetadataDocumentUrl = "https://client.example.test/oauth/client.json",
            MetadataFetchedAt = DateTime.UtcNow.AddHours(-2),
            MetadataExpiresAt = DateTime.UtcNow.AddMinutes(-5),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.Set<SqlOSClientApplication>().Add(existing);
        context.Set<SqlOSConsentGrant>().Add(new SqlOSConsentGrant
        {
            Id = "cgr_reorder",
            UserId = "usr_1",
            ClientApplicationId = existing.Id,
            Scope = "openid",
            GrantedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        });
        await context.SaveChangesAsync();

        // Same set, different order (and grant types repeated): semantically identical.
        var httpFactory = new FakeHttpClientFactory(_ => JsonResponse(
            """
            {
              "client_id": "https://client.example.test/oauth/client.json",
              "client_name": "Reorder Client",
              "redirect_uris": [
                "https://client.example.test/alt",
                "https://client.example.test/callback"
              ],
              "grant_types": ["refresh_token", "authorization_code"],
              "response_types": ["code"],
              "token_endpoint_auth_method": "none"
            }
            """,
            cacheSeconds: 300));
        var options = Options.Create(CreateOptions());
        var cimd = new SqlOSCimdClientService(context, options, httpFactory, TestCryptoService.Create(context, options));

        var resolved = await cimd.ResolveClientAsync(
            existing.ClientId,
            "https://client.example.test/callback",
            existing);

        resolved.Should().NotBeNull();
        resolved!.RequiresFreshConsent.Should().BeFalse(
            "an order-only refresh of set-valued metadata is not a security-sensitive change");
        var grant = await context.Set<SqlOSConsentGrant>().SingleAsync(x => x.Id == "cgr_reorder");
        grant.RevokedAt.Should().BeNull("remembered grants must survive an order-only metadata refresh");
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
