using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Security;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSHostedFormAntiforgeryTests
{
    private static readonly string[] HostedPostRoutes =
    [
        "/sqlos/auth/device/approve",
        "/sqlos/auth/device/deny",
        "/sqlos/auth/device/verify",
        "/sqlos/auth/login/email-otp/start",
        "/sqlos/auth/login/email-otp/verify",
        "/sqlos/auth/login/identify",
        "/sqlos/auth/login/magic-link/complete",
        "/sqlos/auth/login/magic-link/start",
        "/sqlos/auth/login/password",
        "/sqlos/auth/login/phone-otp/start",
        "/sqlos/auth/login/phone-otp/verify",
        "/sqlos/auth/login/select-organization",
        "/sqlos/auth/mfa/totp/enroll/verify",
        "/sqlos/auth/mfa/verify",
        "/sqlos/auth/password/forgot/submit",
        "/sqlos/auth/password/reset/submit",
        "/sqlos/auth/signup/email-otp/start",
        "/sqlos/auth/signup/email-otp/verify",
        "/sqlos/auth/signup/invitation/submit",
        "/sqlos/auth/signup/phone-otp/start",
        "/sqlos/auth/signup/phone-otp/verify",
        "/sqlos/auth/signup/submit"
    ];

    [TestMethod]
    public async Task HostedStateChangingFormRouteInventory_RequiresAntiforgeryMetadata()
    {
        await using var app = await CreateAppAsync();

        var protectedRoutes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
            .Where(endpoint => endpoint.Metadata.GetMetadata<SqlOSHostedFormAntiforgeryMetadata>() != null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Order(StringComparer.Ordinal)
            .ToArray();

        protectedRoutes.Should().Equal(HostedPostRoutes.Order(StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task HeadlessAndPublicJsonEndpoints_DoNotRequireHostedFormTokens()
    {
        await using var app = await CreateAppAsync();

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
            .ToDictionary(endpoint => endpoint.RoutePattern.RawText!, StringComparer.Ordinal);

        endpoints["/sqlos/auth/headless/password/login"].Metadata
            .GetMetadata<SqlOSHostedFormAntiforgeryMetadata>().Should().BeNull();
        endpoints["/sqlos/auth/password/login"].Metadata
            .GetMetadata<SqlOSHostedFormAntiforgeryMetadata>().Should().BeNull();
    }

    [TestMethod]
    public void AntiforgeryCookies_AreIsolatedByMountedAuthPath()
    {
        var first = CreateAntiforgery("/identity-one/auth");
        var second = CreateAntiforgery("/identity-two/auth");

        first.CookiePath.Should().Be("/identity-one/auth");
        second.CookiePath.Should().Be("/identity-two/auth");
        first.CookieName.Should().NotBe(second.CookieName);
    }

    [TestMethod]
    public void AntiforgeryToken_HasShortExpiryAndBoundedClockSkew()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var antiforgery = CreateAntiforgery("/sqlos/auth", time);

        var valid = IssueToken(antiforgery);
        time.UtcNow = now.AddMinutes(14);
        antiforgery.ValidateToken(valid.CookieSecret, valid.RequestToken).Should().BeTrue();
        time.UtcNow = now.AddMinutes(16);
        antiforgery.ValidateToken(valid.CookieSecret, valid.RequestToken).Should().BeFalse();

        time.UtcNow = now.AddSeconds(30);
        var withinSkew = IssueToken(antiforgery);
        time.UtcNow = now;
        antiforgery.ValidateToken(withinSkew.CookieSecret, withinSkew.RequestToken).Should().BeTrue();

        time.UtcNow = now.AddMinutes(2);
        var beyondSkew = IssueToken(antiforgery);
        time.UtcNow = now;
        antiforgery.ValidateToken(beyondSkew.CookieSecret, beyondSkew.RequestToken).Should().BeFalse();
    }

    [TestMethod]
    public void AddSqlOS_DoesNotChangeHostAntiforgeryConfigurationOrProvider()
    {
        var services = new ServiceCollection();
        var hostAdditionalData = new HostAntiforgeryAdditionalDataProvider();
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "host_app_csrf";
            options.Cookie.Path = "/";
            options.FormFieldName = "host_app_token";
        });
        services.AddSingleton<IAntiforgeryAdditionalDataProvider>(hostAdditionalData);
        services.AddDbContext<TestSqlOSInMemoryDbContext>(database =>
            database.UseInMemoryDatabase($"csrf-host-{Guid.NewGuid():N}"));
        services.AddSqlOS<TestSqlOSInMemoryDbContext>();

        using var provider = services.BuildServiceProvider();
        var hostOptions = provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        hostOptions.Cookie.Name.Should().Be("host_app_csrf");
        hostOptions.Cookie.Path.Should().Be("/");
        hostOptions.FormFieldName.Should().Be("host_app_token");
        provider.GetRequiredService<IAntiforgeryAdditionalDataProvider>().Should().BeSameAs(hostAdditionalData);
        provider.GetRequiredService<IAntiforgery>().Should().NotBeNull();
        provider.GetRequiredService<SqlOSHostedFormAntiforgery>().Should().NotBeNull();
    }

    private static async Task<WebApplication> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<TestSqlOSInMemoryDbContext>(database =>
            database.UseInMemoryDatabase($"csrf-routes-{Guid.NewGuid():N}"));
        builder.Services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.Issuer = "https://auth.example.test/sqlos/auth";
            options.AuthServer.Headless.EnableApi = true;
        });
        builder.Services.RemoveAll<IHostedService>();

        var app = builder.Build();
        app.MapAuthServer("/sqlos/auth");
        await app.StartAsync();
        return app;
    }

    private static SqlOSHostedFormAntiforgery CreateAntiforgery(
        string authBasePath,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new SqlOS.AuthServer.Configuration.SqlOSAuthServerOptions
        {
            BasePath = authBasePath,
            Issuer = $"https://auth.example.test{authBasePath}"
        });
        return new SqlOSHostedFormAntiforgery(
            new EphemeralDataProtectionProvider(),
            options,
            timeProvider ?? TimeProvider.System);
    }

    private static (string CookieSecret, string RequestToken) IssueToken(SqlOSHostedFormAntiforgery antiforgery)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        var requestToken = antiforgery.IssueRequestToken(context);
        var cookie = context.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        return (cookie.Split('=', 2)[1], requestToken);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class HostAntiforgeryAdditionalDataProvider : IAntiforgeryAdditionalDataProvider
    {
        public string GetAdditionalData(HttpContext context) => "host-app";
        public bool ValidateAdditionalData(HttpContext context, string additionalData)
            => additionalData == "host-app";
    }
}
