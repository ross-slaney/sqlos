using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
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
        var first = BuildAntiforgeryOptions("/identity-one");
        var second = BuildAntiforgeryOptions("/identity-two");

        first.Cookie.Path.Should().Be("/identity-one/auth");
        second.Cookie.Path.Should().Be("/identity-two/auth");
        first.Cookie.Name.Should().NotBe(second.Cookie.Name);
        first.Cookie.HttpOnly.Should().BeTrue();
        first.Cookie.SameSite.Should().Be(Microsoft.AspNetCore.Http.SameSiteMode.Strict);
        first.Cookie.MaxAge.Should().Be(SqlOSAntiforgeryAdditionalDataProvider.TokenLifetime);
    }

    [TestMethod]
    public void AntiforgeryAdditionalData_ExpiresAfterDocumentedLifetime()
    {
        var provider = new SqlOSAntiforgeryAdditionalDataProvider();
        var context = new DefaultHttpContext();
        var now = DateTimeOffset.UtcNow;

        provider.ValidateAdditionalData(
            context,
            now.Subtract(TimeSpan.FromMinutes(14)).ToUnixTimeSeconds().ToString()).Should().BeTrue();
        provider.ValidateAdditionalData(
            context,
            now.Subtract(TimeSpan.FromMinutes(16)).ToUnixTimeSeconds().ToString()).Should().BeFalse();
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

    private static AntiforgeryOptions BuildAntiforgeryOptions(string dashboardBasePath)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestSqlOSInMemoryDbContext>(database =>
            database.UseInMemoryDatabase($"csrf-options-{Guid.NewGuid():N}"));
        services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.DashboardBasePath = dashboardBasePath;
            options.AuthServer.Issuer = $"https://localhost{dashboardBasePath}/auth";
        });
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
    }
}
