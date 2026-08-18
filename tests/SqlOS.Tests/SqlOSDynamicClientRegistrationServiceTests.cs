using System.Net;
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
public sealed class SqlOSDynamicClientRegistrationServiceTests
{
    [TestMethod]
    public async Task RegisterAsync_PersistsPublicClientAndAuditEvent()
    {
        using var context = CreateContext();
        var optionsValue = CreateOptions();
        var service = CreateService(context, optionsValue, new SqlOSDynamicClientRegistrationRateLimiter());
        var httpContext = CreateHttpContext("10.0.0.1");

        var result = await service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "ChatGPT Client",
            RedirectUris = ["https://client.example.test/callback"],
            GrantTypes = ["authorization_code", "refresh_token"],
            ResponseTypes = ["code"],
            TokenEndpointAuthMethod = "none",
            SoftwareId = "chatgpt",
            SoftwareVersion = "2026.03"
        }, httpContext);

        result.ClientId.Should().StartWith("dcrcli_");
        result.TokenEndpointAuthMethod.Should().Be("none");
        result.Scope.Should().BeEmpty();
        var storedClient = await context.Set<SqlOSClientApplication>().SingleAsync();
        storedClient.RegistrationSource.Should().Be("dcr");
        storedClient.TokenEndpointAuthMethod.Should().Be("none");
        storedClient.AllowedScopesJson.Should().Be("[]");
        storedClient.MetadataJson.Should().Contain("ChatGPT Client");
        storedClient.SoftwareId.Should().Be("chatgpt");
        storedClient.SoftwareVersion.Should().Be("2026.03");
        var audit = await context.Set<SqlOSAuditEvent>().SingleAsync();
        audit.EventType.Should().Be("client.dcr.registered");
    }

    [TestMethod]
    public async Task RegisterAsync_PersistsEmptyAllowedScopesJson()
    {
        using var context = CreateContext();
        var service = CreateService(context, CreateOptions(), new SqlOSDynamicClientRegistrationRateLimiter());

        var result = await service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Empty Allowlist Client",
            RedirectUris = ["https://client.example.test/callback"]
        }, CreateHttpContext("10.0.0.8"));

        result.Scope.Should().BeEmpty();
        var storedClient = await context.Set<SqlOSClientApplication>().SingleAsync();
        storedClient.AllowedScopesJson.Should().Be("[]");
    }

    [TestMethod]
    public async Task RegisterAsync_PersistsAndEchoesRequestedScope()
    {
        using var context = CreateContext();
        var service = CreateService(context, CreateOptions(), new SqlOSDynamicClientRegistrationRateLimiter());

        var result = await service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Scoped Client",
            RedirectUris = ["https://client.example.test/callback"],
            Scope = "openid profile offline_access"
        }, CreateHttpContext("10.0.0.10"));

        result.Scope.Should().Be("openid profile offline_access");
        var storedClient = await context.Set<SqlOSClientApplication>().SingleAsync();
        storedClient.AllowedScopesJson.Should().Be("[\"openid\",\"profile\",\"offline_access\"]");
    }

    [TestMethod]
    public async Task RegisterAsync_RejectsScopesOutsideOperatorCeiling()
    {
        using var context = CreateContext();
        var optionsValue = CreateOptions();
        optionsValue.ClientRegistration.Dcr.AllowedScopes.Add("openid");
        optionsValue.ClientRegistration.Dcr.AllowedScopes.Add("profile");
        var service = CreateService(context, optionsValue, new SqlOSDynamicClientRegistrationRateLimiter());

        var act = () => service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Ceiling Client",
            RedirectUris = ["https://client.example.test/callback"],
            Scope = "openid admin.superuser"
        }, CreateHttpContext("10.0.0.11"));

        var ex = await act.Should().ThrowAsync<SqlOSClientRegistrationException>();
        ex.Which.Error.Should().Be("invalid_client_metadata");
        ex.Which.Message.Should().Contain("admin.superuser");
        (await context.Set<SqlOSClientApplication>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task RegisterAsync_HonorsSubsetOfOperatorCeiling()
    {
        using var context = CreateContext();
        var optionsValue = CreateOptions();
        optionsValue.ClientRegistration.Dcr.AllowedScopes.Add("openid");
        optionsValue.ClientRegistration.Dcr.AllowedScopes.Add("profile");
        var service = CreateService(context, optionsValue, new SqlOSDynamicClientRegistrationRateLimiter());

        var result = await service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Ceiling Subset",
            RedirectUris = ["https://client.example.test/callback"],
            Scope = "openid"
        }, CreateHttpContext("10.0.0.12"));

        result.Scope.Should().Be("openid");
        (await context.Set<SqlOSClientApplication>().SingleAsync())
            .AllowedScopesJson.Should().Be("[\"openid\"]");
    }

    [TestMethod]
    public async Task RegisterAsync_RejectsScopeCountAboveMax()
    {
        using var context = CreateContext();
        var optionsValue = CreateOptions();
        optionsValue.ClientRegistration.Dcr.MaxScopeCount = 2;
        var service = CreateService(context, optionsValue, new SqlOSDynamicClientRegistrationRateLimiter());

        var act = () => service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Too Many Scopes",
            RedirectUris = ["https://client.example.test/callback"],
            Scope = "one two three"
        }, CreateHttpContext("10.0.0.13"));

        var ex = await act.Should().ThrowAsync<SqlOSClientRegistrationException>();
        ex.Which.Error.Should().Be("invalid_client_metadata");
        (await context.Set<SqlOSClientApplication>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task RegisterAsync_RejectsClientSecrets_AndAuditsFailure()
    {
        using var context = CreateContext();
        var service = CreateService(context, CreateOptions(), new SqlOSDynamicClientRegistrationRateLimiter());
        var httpContext = CreateHttpContext("10.0.0.2");

        var act = async () => await service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Secret Client",
            RedirectUris = ["https://client.example.test/callback"],
            ClientSecret = "top-secret"
        }, httpContext);

        var ex = await act.Should().ThrowAsync<SqlOSClientRegistrationException>();
        ex.Which.Error.Should().Be("invalid_client_metadata");

        var audit = await context.Set<SqlOSAuditEvent>().SingleAsync();
        audit.EventType.Should().Be("client.dcr.rejected");
        audit.DataJson.Should().Contain("Secret Client");
    }

    [TestMethod]
    public async Task RegisterAsync_CannotCreateClientCredentialsClient()
    {
        using var context = CreateContext();
        var service = CreateService(context, CreateOptions(), new SqlOSDynamicClientRegistrationRateLimiter());

        var act = () => service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Machine Client",
            RedirectUris = ["https://client.example.test/callback"],
            GrantTypes = ["client_credentials"],
            TokenEndpointAuthMethod = "client_secret_basic"
        }, CreateHttpContext("10.0.0.9"));

        var ex = await act.Should().ThrowAsync<SqlOSClientRegistrationException>();
        ex.Which.Error.Should().Be("invalid_client_metadata");
        (await context.Set<SqlOSClientApplication>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task RegisterAsync_RejectsInvalidRedirectUris()
    {
        using var context = CreateContext();
        var service = CreateService(context, CreateOptions(), new SqlOSDynamicClientRegistrationRateLimiter());
        var httpContext = CreateHttpContext("10.0.0.3");

        var act = async () => await service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Bad Redirect",
            RedirectUris = ["http://evil.example.test/callback"]
        }, httpContext);

        var ex = await act.Should().ThrowAsync<SqlOSClientRegistrationException>();
        ex.Which.Error.Should().Be("invalid_redirect_uri");
    }

    [DataTestMethod]
    [DataRow("ftp://localhost/callback")]
    [DataRow("custom-scheme://127.0.0.1/callback")]
    public async Task RegisterAsync_RejectsNonHttpLoopbackRedirectUris(string redirectUri)
    {
        using var context = CreateContext();
        var service = CreateService(context, CreateOptions(), new SqlOSDynamicClientRegistrationRateLimiter());
        var httpContext = CreateHttpContext("10.0.0.3");

        var act = async () => await service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Bad Loopback Redirect",
            RedirectUris = [redirectUri]
        }, httpContext);

        var ex = await act.Should().ThrowAsync<SqlOSClientRegistrationException>();
        ex.Which.Error.Should().Be("invalid_redirect_uri");
    }

    [TestMethod]
    public async Task RegisterAsync_RejectsWhenDisabled()
    {
        using var context = CreateContext();
        var optionsValue = CreateOptions();
        optionsValue.ClientRegistration.Dcr.Enabled = false;
        var service = CreateService(context, optionsValue, new SqlOSDynamicClientRegistrationRateLimiter());
        var httpContext = CreateHttpContext("10.0.0.4");

        var act = async () => await service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Disabled Registration",
            RedirectUris = ["https://client.example.test/callback"]
        }, httpContext);

        var ex = await act.Should().ThrowAsync<SqlOSClientRegistrationException>();
        ex.Which.Error.Should().Be("unsupported_operation");
    }

    [TestMethod]
    public async Task RegisterAsync_EnforcesRateLimit()
    {
        using var context = CreateContext();
        var optionsValue = CreateOptions();
        optionsValue.ClientRegistration.Dcr.MaxRegistrationsPerWindow = 1;
        optionsValue.ClientRegistration.Dcr.RateLimitWindow = TimeSpan.FromMinutes(10);
        var limiter = new SqlOSDynamicClientRegistrationRateLimiter();
        var service = CreateService(context, optionsValue, limiter);
        var httpContext = CreateHttpContext("10.0.0.5");

        await service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "First Registration",
            RedirectUris = ["https://client.example.test/callback"]
        }, httpContext);

        var act = async () => await service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Second Registration",
            RedirectUris = ["https://client.example.test/callback"]
        }, httpContext);

        var ex = await act.Should().ThrowAsync<SqlOSClientRegistrationException>();
        ex.Which.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        ex.Which.Error.Should().Be("slow_down");
    }

    [TestMethod]
    public async Task RegisterAsync_RespectsPolicyHook()
    {
        using var context = CreateContext();
        var optionsValue = CreateOptions();
        optionsValue.ClientRegistration.Dcr.Policy = (_, _) =>
            Task.FromResult(SqlOSClientRegistrationPolicyDecision.Deny("Policy rejected registration."));
        var service = CreateService(context, optionsValue, new SqlOSDynamicClientRegistrationRateLimiter());
        var httpContext = CreateHttpContext("10.0.0.6");

        var act = async () => await service.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
        {
            ClientName = "Policy Client",
            RedirectUris = ["https://client.example.test/callback"]
        }, httpContext);

        var ex = await act.Should().ThrowAsync<SqlOSClientRegistrationException>();
        ex.Which.Error.Should().Be("invalid_client_metadata");
        ex.Which.Message.Should().Contain("Policy rejected registration.");
    }

    private static SqlOSAuthServerOptions CreateOptions()
    {
        var options = new SqlOSAuthServerOptions
        {
            Issuer = "https://app.example.com/sqlos/auth",
            PublicOrigin = "https://app.example.com"
        };
        options.ClientRegistration.Dcr.Enabled = true;
        return options;
    }

    private static SqlOSDynamicClientRegistrationService CreateService(
        TestSqlOSInMemoryDbContext context,
        SqlOSAuthServerOptions optionsValue,
        SqlOSDynamicClientRegistrationRateLimiter limiter)
    {
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        return new SqlOSDynamicClientRegistrationService(context, options, crypto, admin, limiter);
    }

    private static DefaultHttpContext CreateHttpContext(string ipAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        return context;
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}
