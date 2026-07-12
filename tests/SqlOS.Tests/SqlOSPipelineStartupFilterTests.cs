using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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
        var filter = new SqlOSPipelineStartupFilter();
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "SqlOS.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
