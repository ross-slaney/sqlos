using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Authentication;
using SqlOS.AuthServer.Extensions;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Extensions;
using SqlOS.Hosting;
using SqlOS.Tests.Infrastructure;

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

    // ----- Endpoint mapping and single-application surfaces (issue #356) -----

    [TestMethod]
    public async Task Startup_MapsAuthServerAdminAndDashboardRoutes()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(
            options => options.UseSingleApplication("Todo", app => app.Origin = SingleApplicationTestHost.Origin),
            app => app.MapGet("/hello", () => "app"));

        (await host.Client.GetAsync("/sqlos/auth/.well-known/oauth-authorization-server")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client.GetAsync("/sqlos/auth/.well-known/jwks.json")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client.GetAsync("/sqlos/auth/authorize")).StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        (await host.Client.PostAsync("/sqlos/auth/token", new FormUrlEncodedContent([]))).StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        (await host.Client.GetAsync("/sqlos/auth/login")).StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        (await host.Client.GetAsync("/sqlos/admin/auth/api/stats")).StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        (await host.Client.GetAsync("/sqlos")).StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the dashboard middleware serves the operator UI in Development");
        (await host.Client.GetStringAsync("/hello")).Should().Be("app", "unmatched requests fall through to the application's pipeline");
        host.Logs.Entries.Should().NotContain(entry => entry.Message.Contains("MapAuthServer()", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NoDeclaredSurface_InstallsNoPathMiddlewareAndNoProtectedResourceRoutes()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(
            options => options.UseSingleApplication("Todo", app => app.Origin = SingleApplicationTestHost.Origin),
            app => app.MapGet("/api/anything", (HttpContext context) => Results.Ok(new
            {
                validated = context.GetSqlOSValidatedToken() != null
            })));

        var response = await host.Client.GetAsync("/api/anything");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "no surface was declared, so /api is the application's own unprotected route");
        response.Headers.WwwAuthenticate.Should().BeEmpty();
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("validated").GetBoolean().Should().BeFalse();
        (await host.Client.GetAsync("/.well-known/oauth-protected-resource")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.Client.GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task ApiSurface_Unauthenticated_Returns401WithRealmAndResourceMetadataChallenge()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(
            options => options.UseSingleApplication("Todo", app =>
            {
                app.Origin = SingleApplicationTestHost.Origin;
                app.Api = "/api";
            }),
            app => app.MapGet("/api/anything", () => "should not run").RequireAuthorization());

        var response = await host.Client.GetAsync("/api/anything");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.Should().StartWith("Bearer realm=\"Todo API\"");
        challenge.Should().Contain($"resource_metadata=\"{SingleApplicationTestHost.Origin}/.well-known/oauth-protected-resource\"");
        challenge.Should().Contain("error=\"invalid_token\"");

        var metadata = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        metadata.GetProperty("resource").GetString().Should().Be($"{SingleApplicationTestHost.Origin}/api");
        metadata.GetProperty("authorization_servers").EnumerateArray().Select(x => x.GetString())
            .Should().Equal(host.AuthOptions.Issuer);
        metadata.GetProperty("scopes_supported").EnumerateArray().Select(x => x.GetString())
            .Should().BeEquivalentTo("openid", "profile", "email", "offline_access");
        metadata.GetProperty("bearer_methods_supported").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("header");

        // RFC 9728 §3 path-suffixed location is served too.
        (await host.Client.GetAsync("/.well-known/oauth-protected-resource/api")).StatusCode.Should().Be(HttpStatusCode.OK);
        // The MCP document is not served because no MCP surface is declared.
        (await host.Client.GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task ApiSurface_ValidToken_ReachesApplicationHandlerWithValidatedTokenPopulated()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(
            options => options.UseSingleApplication("Todo", app =>
            {
                app.Origin = SingleApplicationTestHost.Origin;
                app.Api = "/api";
            }),
            app => app.MapGet("/api/me", (HttpContext context) =>
            {
                var token = context.GetSqlOSValidatedToken();
                return Results.Ok(new
                {
                    audience = token?.Audience,
                    userId = token?.UserId,
                    authenticated = context.User.Identity?.IsAuthenticated ?? false
                });
            }).RequireAuthorization());

        var token = await host.MintAccessTokenAsync($"{SingleApplicationTestHost.Origin}/api");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("audience").GetString().Should().Be($"{SingleApplicationTestHost.Origin}/api");
        body.GetProperty("userId").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("authenticated").GetBoolean().Should().BeTrue();

        // Malformed and expired-shaped tokens challenge rather than fault.
        using var malformed = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        malformed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        (await host.Client.SendAsync(malformed)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task ApiAndMcpSurfaces_RejectTokensMintedForTheOtherResource()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(
            options => options.UseSingleApplication("Todo", app =>
            {
                app.Origin = SingleApplicationTestHost.Origin;
                app.Api = "/api";
                app.Mcp = "/mcp";
            }),
            app =>
            {
                app.MapGet("/api/me", (HttpContext context) => context.GetSqlOSValidatedToken()?.Audience)
                    .RequireAuthorization();
                app.MapPost("/mcp", (HttpContext context) => context.GetSqlOSValidatedToken()?.Audience)
                    .RequireAuthorization(SqlOSJwtDefaults.McpPolicy);
            });

        var apiToken = await host.MintAccessTokenAsync($"{SingleApplicationTestHost.Origin}/api");
        var mcpToken = await host.MintAccessTokenAsync($"{SingleApplicationTestHost.Origin}/mcp");

        (await SendAsync(host, HttpMethod.Get, "/api/me", apiToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(host, HttpMethod.Post, "/mcp", mcpToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var apiTokenAtMcp = await SendAsync(host, HttpMethod.Post, "/mcp", apiToken);
        apiTokenAtMcp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        apiTokenAtMcp.Headers.WwwAuthenticate.ToString().Should().StartWith("Bearer realm=\"Todo MCP\"")
            .And.Contain($"resource_metadata=\"{SingleApplicationTestHost.Origin}/.well-known/oauth-protected-resource/mcp\"");

        var mcpTokenAtApi = await SendAsync(host, HttpMethod.Get, "/api/me", mcpToken);
        mcpTokenAtApi.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        mcpTokenAtApi.Headers.WwwAuthenticate.ToString().Should().StartWith("Bearer realm=\"Todo API\"");

        var mcpMetadata = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp");
        mcpMetadata.GetProperty("resource").GetString().Should().Be($"{SingleApplicationTestHost.Origin}/mcp");
        var apiMetadata = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        apiMetadata.GetProperty("resource").GetString().Should().Be($"{SingleApplicationTestHost.Origin}/api");

        var asMetadata = await host.Client.GetFromJsonAsync<JsonElement>("/sqlos/auth/.well-known/oauth-authorization-server");
        asMetadata.GetProperty("client_id_metadata_document_supported").GetBoolean().Should().BeTrue();
        asMetadata.GetProperty("resource_parameter_supported").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task McpOnlySurface_ServesMcpDocumentAtRootFallbackToo()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(
            options => options.UseSingleApplication("Todo", app =>
            {
                app.Origin = SingleApplicationTestHost.Origin;
                app.Mcp = "/mcp";
            }));

        var root = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        root.GetProperty("resource").GetString().Should().Be($"{SingleApplicationTestHost.Origin}/mcp");
        var suffixed = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp");
        suffixed.GetProperty("resource").GetString().Should().Be($"{SingleApplicationTestHost.Origin}/mcp");
        (await host.Client.GetAsync("/.well-known/oauth-protected-resource/api")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task HostExtension_ServicesAreRegisteredAndEndpointsMapInsideTheProtectedMcpBranch()
    {
        var extension = new RecordingHostExtension();
        await using var host = await SingleApplicationTestHost.StartAsync(
            options => options.UseSingleApplication("Todo", app =>
            {
                app.Origin = SingleApplicationTestHost.Origin;
                app.Mcp = "/mcp";
                app.HostExtensions.Add(extension);
            }));

        extension.ConfiguredServices.Should().BeTrue();
        host.App.Services.GetService<RecordingHostExtension.Marker>().Should().NotBeNull();

        var anonymous = await host.Client.PostAsync("/mcp", new StringContent("{}"));
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the extension requires the SqlOS.Mcp policy");
        anonymous.Headers.WwwAuthenticate.ToString().Should().Contain("/.well-known/oauth-protected-resource/mcp");

        var token = await host.MintAccessTokenAsync($"{SingleApplicationTestHost.Origin}/mcp");
        var response = await SendAsync(host, HttpMethod.Post, "/mcp", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be($"{SingleApplicationTestHost.Origin}/mcp");
    }

    private static async Task<HttpResponseMessage> SendAsync(SingleApplicationTestHost host, HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        return await host.Client.SendAsync(request);
    }

    private sealed class RecordingHostExtension : ISqlOSHostExtension
    {
        public bool ConfiguredServices { get; private set; }

        public void ConfigureServices(IServiceCollection services, SqlOSOptions options)
        {
            ConfiguredServices = true;
            services.AddSingleton<Marker>();
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints, SqlOSOptions options)
        {
            endpoints.MapPost(options.AuthServer.SingleApplication!.Mcp!, (HttpContext context) =>
                Results.Text(context.GetSqlOSValidatedToken()?.Audience ?? "unvalidated"))
                .RequireAuthorization(SqlOSJwtDefaults.McpPolicy);
        }

        public sealed class Marker;
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
