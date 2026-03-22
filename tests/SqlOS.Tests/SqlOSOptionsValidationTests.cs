using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.Configuration;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSOptionsValidationTests
{
    [TestMethod]
    public void AddSqlOS_Throws_WhenDashboardPasswordModeHasNoPassword()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Dashboard.Password is required when Dashboard.AuthMode is Password.*");
    }

    [TestMethod]
    public void AddSqlOS_Throws_WhenPublicOriginIncludesPath()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.PublicOrigin = "https://app.example.com/root";
            options.AuthServer.Issuer = "https://app.example.com/sqlos/auth";
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AuthServer.PublicOrigin must be an origin only, without a path.*");
    }

    [TestMethod]
    public void AddSqlOS_Throws_WhenIssuerDoesNotMatchPublicOriginAndBasePath()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.PublicOrigin = "https://app.example.com";
            options.AuthServer.Issuer = "https://app.example.com/not-sqlos/auth";
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AuthServer.Issuer must be 'https://app.example.com/sqlos/auth' when AuthServer.PublicOrigin is set.*");
    }

    [TestMethod]
    public void AddSqlOS_Throws_WhenHeadlessApiBasePathIsSetWithoutBuildUiUrl()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.Headless.HeadlessApiBasePath = "/sqlos/auth/custom-headless";
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AuthServer.Headless.HeadlessApiBasePath requires AuthServer.Headless.BuildUiUrl.*");
    }

    [TestMethod]
    public void AddSqlOS_AllowsValidHostedConfiguration()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.PublicOrigin = "https://app.example.com";
            options.AuthServer.Issuer = "https://app.example.com/sqlos/auth";
            options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
            options.Dashboard.Password = "test-password";
        });

        act.Should().NotThrow();
    }

    [TestMethod]
    public void AddSqlOS_AllowsValidHeadlessConfiguration()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.PublicOrigin = "https://app.example.com";
            options.AuthServer.Issuer = "https://app.example.com/sqlos/auth";
            options.AuthServer.UseHeadlessAuthPage(headless =>
            {
                headless.HeadlessApiBasePath = "/sqlos/auth/custom-headless";
                headless.BuildUiUrl = _ => "https://app.example.com/auth/authorize";
            });
        });

        act.Should().NotThrow();
    }

    [TestMethod]
    public void AddSqlOS_Throws_WhenCimdDefaultCacheTtlIsNotPositive()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.ClientRegistration.Cimd.DefaultCacheTtl = TimeSpan.Zero;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AuthServer.ClientRegistration.Cimd.DefaultCacheTtl must be greater than zero.*");
    }

    [TestMethod]
    public void AddSqlOS_Throws_WhenDcrRateLimitWindowIsNotPositive()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.ClientRegistration.Dcr.RateLimitWindow = TimeSpan.Zero;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AuthServer.ClientRegistration.Dcr.RateLimitWindow must be greater than zero.*");
    }
}
