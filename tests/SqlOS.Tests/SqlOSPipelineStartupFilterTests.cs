using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Hosting;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSPipelineStartupFilterTests
{
    [TestMethod]
    public async Task Configure_AppliesTrustedForwardedHeadersBeforeDashboardMiddleware()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton(Options.Create(new SqlOSOptions()));
        services.AddSingleton<SqlOSDashboardSessionService>();
        services.AddSingleton<SqlOSDashboardLoginThrottlingService>();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.KnownProxies.Add(IPAddress.Parse("10.0.0.10"));
        });

        await using var provider = services.BuildServiceProvider();
        var appBuilder = new ApplicationBuilder(provider);
        IPAddress? observedClientIp = null;
        var filter = new SqlOSPipelineStartupFilter(new RecordingLogger<SqlOSPipelineStartupFilter>());
        filter.Configure(app => app.Run(context =>
        {
            observedClientIp = context.Connection.RemoteIpAddress;
            return Task.CompletedTask;
        }))(appBuilder);
        var pipeline = appBuilder.Build();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/health";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.10");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.42";

        await pipeline(context);

        observedClientIp.Should().Be(IPAddress.Parse("203.0.113.42"));
    }

    [TestMethod]
    public async Task Configure_IgnoresForwardedClientIpFromUntrustedProxy()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton(Options.Create(new SqlOSOptions()));
        services.AddSingleton<SqlOSDashboardSessionService>();
        services.AddSingleton<SqlOSDashboardLoginThrottlingService>();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.KnownProxies.Add(IPAddress.Parse("10.0.0.10"));
        });

        await using var provider = services.BuildServiceProvider();
        var appBuilder = new ApplicationBuilder(provider);
        IPAddress? observedClientIp = null;
        var filter = new SqlOSPipelineStartupFilter(new RecordingLogger<SqlOSPipelineStartupFilter>());
        filter.Configure(app => app.Run(context =>
        {
            observedClientIp = context.Connection.RemoteIpAddress;
            return Task.CompletedTask;
        }))(appBuilder);
        var pipeline = appBuilder.Build();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/health";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.11");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.42";

        await pipeline(context);

        observedClientIp.Should().Be(IPAddress.Parse("10.0.0.11"));
    }

    [TestMethod]
    public void Configure_PublicThrottleWithOnlyLoopbackForwardingTrust_EmitsSafetyWarning()
    {
        var options = new SqlOSOptions();
        options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
        options.Dashboard.Password = "test-password";
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<SqlOSDashboardSessionService>();
        services.AddSingleton<SqlOSDashboardLoginThrottlingService>();
        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            forwarded.KnownProxies.Clear();
            forwarded.KnownNetworks.Clear();
            forwarded.KnownProxies.Add(IPAddress.Loopback);
        });

        using var provider = services.BuildServiceProvider();
        var appBuilder = new ApplicationBuilder(provider);
        var logger = new RecordingLogger<SqlOSPipelineStartupFilter>();

        new SqlOSPipelineStartupFilter(logger).Configure(_ => { })(appBuilder);

        logger.Messages.Should().Contain(message =>
            message.Contains("no non-loopback KnownProxies or KnownNetworks", StringComparison.Ordinal)
            && message.Contains("rate-limit buckets", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Configure_DevelopmentOnlyDashboard_EmitsProductionSafetyWarning()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton(Options.Create(new SqlOSOptions()));
        services.AddSingleton<SqlOSDashboardSessionService>();
        services.AddSingleton<SqlOSDashboardLoginThrottlingService>();

        using var provider = services.BuildServiceProvider();
        var appBuilder = new ApplicationBuilder(provider);
        var logger = new RecordingLogger<SqlOSPipelineStartupFilter>();

        new SqlOSPipelineStartupFilter(logger).Configure(_ => { })(appBuilder);

        logger.Messages.Should().ContainSingle(message =>
            message.Contains("DevelopmentOnly", StringComparison.Ordinal)
            && message.Contains("return 404 outside Development", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Configure_DevelopmentOnlyDashboard_EmitsUnauthenticatedDevelopmentWarning()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment
        {
            EnvironmentName = Environments.Development
        });
        services.AddSingleton(Options.Create(new SqlOSOptions()));
        services.AddSingleton<SqlOSDashboardSessionService>();
        services.AddSingleton<SqlOSDashboardLoginThrottlingService>();

        using var provider = services.BuildServiceProvider();
        var appBuilder = new ApplicationBuilder(provider);
        var logger = new RecordingLogger<SqlOSPipelineStartupFilter>();

        new SqlOSPipelineStartupFilter(logger).Configure(_ => { })(appBuilder);

        logger.Messages.Should().ContainSingle(message =>
            message.Contains("available without a login", StringComparison.Ordinal)
            && message.Contains("Do not use Development in a production deployment", StringComparison.Ordinal));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "SqlOS.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
