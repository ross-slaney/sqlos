using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.Configuration;
using SqlOS.Email.Interfaces;
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

    [DataTestMethod]
    [DataRow("issuer")]
    [DataRow("public-origin")]
    [DataRow("single-application")]
    public void AddSqlOS_Throws_WhenTrustedAuthorityIncludesUserInformation(string source)
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            if (source == "issuer")
            {
                options.AuthServer.Issuer = "https://trusted.example@attacker.example/sqlos/auth";
            }
            else if (source == "public-origin")
            {
                options.AuthServer.PublicOrigin = "https://trusted.example@attacker.example";
                options.AuthServer.Issuer = "https://trusted.example@attacker.example/sqlos/auth";
            }
            else
            {
                options.UseSingleApplication("Acme", application =>
                    application.Origin = "https://trusted.example@attacker.example");
            }
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot include user information.*");
    }

    [DataTestMethod]
    [DataRow("/")]
    [DataRow("sqlos/scim/v2")]
    [DataRow("/sqlos/scim/v2?tenant=acme")]
    [DataRow("/sqlos/auth/scim")]
    [DataRow("/SQLos/Auth/scim")]
    [DataRow("/sqlos/admin/auth/api/scim")]
    public void AddSqlOS_Throws_WhenScimBasePathIsUnsafe(string path)
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.EnableScim = true;
            options.AuthServer.ScimBasePath = path;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AuthServer.ScimBasePath*");
    }

    [TestMethod]
    public void AddSqlOS_AllowsHeadlessApiBasePathWithoutBuildUiUrl()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.Headless.HeadlessApiBasePath = "/sqlos/auth/custom-headless";
        });

        act.Should().NotThrow();
    }

    [TestMethod]
    public void AddSqlOS_Throws_WhenHeadlessApiBasePathIsSetButHeadlessApiIsDisabled()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.Headless.EnableApi = false;
            options.AuthServer.Headless.HeadlessApiBasePath = "/sqlos/auth/custom-headless";
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AuthServer.Headless.HeadlessApiBasePath requires AuthServer.Headless.EnableApi.*");
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

    [DataTestMethod]
    [DataRow("default-src 'self'; script-src 'self'")]
    [DataRow("default-src 'self'; script-src 'nonce-{nonce}'; style-src 'self'")]
    [DataRow("default-src 'self'; script-src 'self' 'unsafe-inline' 'nonce-{nonce}'; style-src 'nonce-{nonce}'")]
    [DataRow("default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'unsafe-inline' 'nonce-{nonce}'")]
    [DataRow("default-src 'self'; script-src 'nonce-{nonce}'; frame-ancestors https://example.test")]
    [DataRow("default-src 'self'; script-src 'nonce-{nonce}'\r\nX-Test: injected")]
    public void AddSqlOS_RejectsUnsafeBrowserSecurityPolicy(string policy)
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
            options.BrowserSecurity.ContentSecurityPolicy = policy);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BrowserSecurity.ContentSecurityPolicy*");
    }

    [TestMethod]
    public void AddSqlOS_SingleApplicationOriginDerivesIssuerWithoutPublicOriginSetup()
    {
        var services = new ServiceCollection();

        services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
            options.UseSingleApplication("Acme", application =>
                application.Origin = "http://localhost:5050"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SqlOSAuthServerOptions>>().Value;
        options.PublicOrigin.Should().BeNull();
        options.Issuer.Should().Be("http://localhost:5050/sqlos/auth");
    }

    [TestMethod]
    public void AddSqlOS_Throws_WhenSigningKeyCleanupIsShorterThanJwksGrace()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.DefaultSigningKeyGraceWindowDays = 7;
            options.AuthServer.DefaultSigningKeyRetiredCleanupDays = 6;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DefaultSigningKeyRetiredCleanupDays must be at least DefaultSigningKeyGraceWindowDays*");
    }

    [TestMethod]
    public void AddSqlOS_RegistersTransactionalEmailService()
    {
        var services = new ServiceCollection();

        services.AddSqlOS<TestSqlOSInMemoryDbContext>();

        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(ISqlOSTransactionalEmailService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(ISqlOSEmailSender));
    }

    [TestMethod]
    public void AddSqlOS_Throws_WhenDashboardLoginThrottlingWindowIsNotPositive()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.Dashboard.LoginThrottling.Window = TimeSpan.Zero;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Dashboard.LoginThrottling.Window must be greater than zero.*");
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
    public void AddSqlOS_AllowsPortableCimdWithoutHostAllowlist()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.EnablePortableMcpClients();
        });

        act.Should().NotThrow();
    }

    [TestMethod]
    public void AddSqlOS_Throws_WhenAccessTokenValidationIntervalsAreNegative()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.AccessTokenValidationSigningKeyCacheTtl = TimeSpan.FromSeconds(-1);
            options.AuthServer.AccessTokenValidationLastSeenDebounceInterval = TimeSpan.FromSeconds(-1);
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AuthServer.AccessTokenValidationSigningKeyCacheTtl must be zero or greater.*")
            .WithMessage("*AuthServer.AccessTokenValidationLastSeenDebounceInterval must be zero or greater.*");
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

    [TestMethod]
    public void AddSqlOS_Throws_WhenCalendarConnectSessionLifetimeIsNotPositive()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.Calendar.ConnectSessionLifetime = TimeSpan.Zero;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Calendar.ConnectSessionLifetime must be greater than zero.*");
    }

    [TestMethod]
    public void AddSqlOS_Throws_WhenCalendarSyncWindowsAreInvalid()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.Calendar.AccessTokenRefreshSkew = TimeSpan.FromSeconds(-1);
            options.Calendar.SyncWindowPastDays = -1;
            options.Calendar.SyncWindowFutureDays = 0;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Calendar.AccessTokenRefreshSkew must be zero or greater.*")
            .WithMessage("*Calendar.SyncWindowPastDays must be zero or greater.*")
            .WithMessage("*Calendar.SyncWindowFutureDays must be greater than zero.*");
    }

    [TestMethod]
    public void AddSqlOS_Throws_WhenCalendarSchedulerIsMisconfigured()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.ConfigureCalendar(calendar => calendar.ConfigureSyncScheduler(scheduler =>
            {
                scheduler.Interval = TimeSpan.Zero;
                scheduler.SyncEvery = TimeSpan.Zero;
                scheduler.MaxConnectionsPerRun = 0;
            }));
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Calendar.SyncScheduler.Interval must be greater than zero.*")
            .WithMessage("*Calendar.SyncScheduler.SyncEvery must be greater than zero.*")
            .WithMessage("*Calendar.SyncScheduler.MaxConnectionsPerRun must be greater than zero.*");
    }

    [TestMethod]
    public void AddSqlOS_Allows_DisabledCalendarSchedulerWithInvalidIntervals()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.ConfigureCalendar(calendar => calendar.ConfigureSyncScheduler(scheduler =>
            {
                scheduler.Enabled = false;
                scheduler.Interval = TimeSpan.Zero;
            }));
        });

        act.Should().NotThrow();
    }
}
