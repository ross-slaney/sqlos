using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Fga.Dashboard;
using SqlOS.Security;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSDashboardAuthMiddlewareTests
{
    [TestMethod]
    public async Task DashboardLogin_RepeatedWrongPassword_FromSameIp_IsThrottled()
    {
        using var harness = CreateHarness(options =>
        {
            options.LoginThrottling.MaxFailuresPerIp = 2;
            options.LoginThrottling.MaxGlobalFailures = 20;
        });

        (await harness.PostLoginAsync("wrong-password", "203.0.113.10")).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        (await harness.PostLoginAsync("wrong-password", "203.0.113.10")).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

        var throttled = await harness.PostLoginAsync("wrong-password", "203.0.113.10");

        throttled.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        throttled.Body.Should().Contain("Too many dashboard login attempts");
        throttled.Body.Should().Contain("ip");
        throttled.RetryAfter.Should().NotBeNullOrWhiteSpace();

        var auditEvents = await harness.ListAuditEventsAsync();
        auditEvents.Should().ContainSingle(x =>
            x.EventType == "dashboard.login.lockout"
            && x.DataJson != null
            && x.DataJson.Contains("\"scope\":\"ip\"", StringComparison.Ordinal));
        auditEvents.Should().ContainSingle(x =>
            x.EventType == "dashboard.login.rate-limited"
            && x.DataJson != null
            && x.DataJson.Contains("\"scope\":\"ip\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DashboardLogin_GlobalFailureSpike_IsThrottled()
    {
        using var harness = CreateHarness(options =>
        {
            options.LoginThrottling.MaxFailuresPerIp = 10;
            options.LoginThrottling.MaxGlobalFailures = 2;
        });

        (await harness.PostLoginAsync("wrong-password", "203.0.113.10")).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        (await harness.PostLoginAsync("wrong-password", "203.0.113.11")).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

        var throttled = await harness.PostLoginAsync("wrong-password", "203.0.113.12");

        throttled.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        throttled.Body.Should().Contain("global");

        var auditEvents = await harness.ListAuditEventsAsync();
        auditEvents.Should().ContainSingle(x =>
            x.EventType == "dashboard.login.lockout"
            && x.DataJson != null
            && x.DataJson.Contains("\"scope\":\"global\"", StringComparison.Ordinal));
        auditEvents.Should().ContainSingle(x =>
            x.EventType == "dashboard.login.rate-limited"
            && x.DataJson != null
            && x.DataJson.Contains("\"scope\":\"global\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DashboardLogin_Success_WritesAuditEvent_AndCreatesSessionCookie()
    {
        using var harness = CreateHarness();

        var response = await harness.PostLoginAsync("correct-password", "203.0.113.20");

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.SetCookie.Should().Contain(cookie => cookie.Contains("SqlOS.Dashboard.Session=", StringComparison.Ordinal));

        var auditEvents = await harness.ListAuditEventsAsync();
        auditEvents.Should().ContainSingle(x =>
            x.EventType == "dashboard.login.success"
            && x.ActorType == "dashboard"
            && x.IpAddress == "203.0.113.20");
    }

    [TestMethod]
    public async Task DashboardLogin_Failure_WritesAuditEvent_WithoutPasswordValue()
    {
        using var harness = CreateHarness();

        var response = await harness.PostLoginAsync("submitted-secret", "203.0.113.30");

        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        var auditEvent = (await harness.ListAuditEventsAsync())
            .Single(x => x.EventType == "dashboard.login.failure");
        auditEvent.IpAddress.Should().Be("203.0.113.30");
        auditEvent.DataJson.Should().Contain("invalid_password");
        auditEvent.DataJson.Should().NotContain("submitted-secret");
    }

    [TestMethod]
    public async Task DashboardLogin_ProductionCookie_IsSecureHttpOnlyAndPathScoped()
    {
        using var harness = CreateHarness();

        var response = await harness.PostLoginAsync("correct-password", "203.0.113.40");

        var sessionCookie = response.SetCookie.Single(cookie => cookie.Contains("SqlOS.Dashboard.Session=", StringComparison.Ordinal));
        var normalizedCookie = sessionCookie.ToLowerInvariant();
        normalizedCookie.Should().Contain("httponly");
        normalizedCookie.Should().Contain("secure");
        normalizedCookie.Should().Contain("path=/sqlos");
    }

    [TestMethod]
    public async Task DashboardLogout_WritesAuditEvent_AndClearsSessionCookie()
    {
        using var harness = CreateHarness();

        var response = await harness.PostLogoutAsync("203.0.113.50");

        response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        response.SetCookie.Should().Contain(cookie => cookie.Contains("SqlOS.Dashboard.Session=", StringComparison.Ordinal));

        var auditEvents = await harness.ListAuditEventsAsync();
        auditEvents.Should().ContainSingle(x =>
            x.EventType == "dashboard.logout"
            && x.ActorType == "dashboard"
            && x.IpAddress == "203.0.113.50");
    }

    [TestMethod]
    public async Task DashboardShell_ExposesScimCapability()
    {
        using var disabledHarness = CreateHarness(options =>
        {
            options.AuthMode = SqlOSDashboardAuthMode.DevelopmentOnly;
            options.AuthorizationCallback = _ => Task.FromResult(true);
        });
        using var enabledHarness = CreateHarness(options =>
        {
            options.AuthMode = SqlOSDashboardAuthMode.DevelopmentOnly;
            options.AuthorizationCallback = _ => Task.FromResult(true);
        }, scimEnabled: true);

        var disabled = await disabledHarness.GetDashboardAsync();
        var enabled = await enabledHarness.GetDashboardAsync();

        disabled.StatusCode.Should().Be(StatusCodes.Status200OK);
        disabled.Body.Should().Contain("window.__SQL_OS_CAPABILITIES__ = {\"scimEnabled\":false};");
        enabled.StatusCode.Should().Be(StatusCodes.Status200OK);
        enabled.Body.Should().Contain("window.__SQL_OS_CAPABILITIES__ = {\"scimEnabled\":true};");
        enabled.Body.Should().Contain("window.__SQL_OS_BASE_PATH__ = \"/sqlos\";");
        enabled.Body.Should().NotContain("window./sqlos");
    }

    [TestMethod]
    public async Task DashboardShell_IncludesLockedSecurityHeadersAndNonce()
    {
        using var harness = CreateHarness(options =>
        {
            options.AuthMode = SqlOSDashboardAuthMode.DevelopmentOnly;
            options.AuthorizationCallback = _ => Task.FromResult(true);
        });

        var response = await harness.GetDashboardAsync();

        response.XFrameOptions.Should().Be("DENY");
        response.ContentTypeOptions.Should().Be("nosniff");
        response.ReferrerPolicy.Should().Be("no-referrer");
        response.ContentSecurityPolicy.Should().Contain("frame-ancestors 'none'");
        response.ContentSecurityPolicy.Should().NotContain("unsafe-inline");
        response.Body.Should().MatchRegex("<script nonce=\"[A-Za-z0-9_-]+\">");
        response.Body.Should().MatchRegex("<script nonce=\"[A-Za-z0-9_-]+\" src=");
    }

    [TestMethod]
    public async Task DashboardPathPrefix_RejectsLookalikeSegment()
    {
        using var harness = CreateHarness(options =>
        {
            options.AuthMode = SqlOSDashboardAuthMode.DevelopmentOnly;
            options.AuthorizationCallback = null;
        });

        var response = await harness.GetAsync("/sqlos-evil/admin/auth");

        response.StatusCode.Should().Be(StatusCodes.Status418ImATeapot,
            "lookalike paths must continue to the host pipeline instead of entering dashboard authorization");
    }

    [TestMethod]
    public async Task DashboardFgaRoutes_UseUnifiedShell_WhileApiAndAssetsPassThrough()
    {
        using var harness = CreateHarness(options =>
        {
            options.AuthMode = SqlOSDashboardAuthMode.DevelopmentOnly;
            options.AuthorizationCallback = _ => Task.FromResult(true);
        });

        var lookalike = await harness.GetAsync("/sqlos/admin/fga-evil", "?embed=1");
        var resources = await harness.GetAsync("/sqlos/admin/fga/resources", "?embed=1");
        var api = await harness.GetAsync("/sqlos/admin/fga/api/stats");
        var asset = await harness.GetAsync("/sqlos/admin/fga/app.js");

        lookalike.StatusCode.Should().Be(StatusCodes.Status418ImATeapot);
        resources.StatusCode.Should().Be(StatusCodes.Status200OK);
        resources.XFrameOptions.Should().Be("DENY");
        resources.ContentSecurityPolicy.Should().Contain("frame-ancestors 'none'");
        resources.Body.Should().Contain("/admin/fga/app.js");
        api.StatusCode.Should().Be(StatusCodes.Status418ImATeapot);
        asset.StatusCode.Should().Be(StatusCodes.Status418ImATeapot);
    }

    [TestMethod]
    public async Task FgaDashboardPathPrefix_RejectsLookalikeSegment()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddSingleton<SqlOSDashboardSessionService>();
        await using var provider = services.BuildServiceProvider();
        var options = new SqlOSDashboardOptions();
        var middleware = new SqlOSFgaDashboardMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status418ImATeapot;
                return Task.CompletedTask;
            },
            "/sqlos/admin/fga",
            new TestHostEnvironment(),
            options,
            provider.GetRequiredService<SqlOSDashboardSessionService>(),
            Options.Create(new SqlOSOptions()));
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/sqlos/admin/fga-evil";

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status418ImATeapot);
    }

    [TestMethod]
    public async Task FgaDashboardMiddleware_ServesOnlyApiAndComponentAssets_NotASecondDashboardDocument()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddSingleton<SqlOSDashboardSessionService>();
        await using var provider = services.BuildServiceProvider();
        var options = new SqlOSDashboardOptions
        {
            AuthMode = SqlOSDashboardAuthMode.DevelopmentOnly
        };
        var middleware = new SqlOSFgaDashboardMiddleware(
            _ => Task.CompletedTask,
            "/sqlos/admin/fga",
            new TestHostEnvironment { EnvironmentName = Environments.Development },
            options,
            provider.GetRequiredService<SqlOSDashboardSessionService>(),
            Options.Create(new SqlOSOptions()));

        var routeContext = new DefaultHttpContext { RequestServices = provider };
        routeContext.Request.Path = "/sqlos/admin/fga/resources";
        await middleware.InvokeAsync(routeContext);

        var assetContext = new DefaultHttpContext { RequestServices = provider };
        assetContext.Request.Path = "/sqlos/admin/fga/app.js";
        assetContext.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(assetContext);

        routeContext.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        assetContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        assetContext.Response.ContentType.Should().Be("application/javascript");
        assetContext.Response.Headers.XFrameOptions.ToString().Should().Be("DENY");
    }

    [TestMethod]
    public async Task DashboardClientReturnPath_UsesExactOrChildSegmentBoundary()
    {
        var files = new ManifestEmbeddedFileProvider(
            typeof(SqlOSDashboardMiddleware).Assembly,
            "Dashboard/wwwroot");
        await using var stream = files.GetFileInfo("app.js").CreateReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var source = await reader.ReadToEndAsync();

        source.Should().Contain("parsed.pathname === dashboardBasePath");
        source.Should().Contain("parsed.pathname.startsWith(`${dashboardBasePath}/`)");
        source.Should().NotContain("parsed.pathname.startsWith(dashboardBasePath || \"/\")");
    }

    [TestMethod]
    public async Task DashboardFgaClient_MountsTheCompleteSharedComponent_WithoutAnIframe()
    {
        var files = new ManifestEmbeddedFileProvider(
            typeof(SqlOSDashboardMiddleware).Assembly,
            "Dashboard/wwwroot");
        await using var dashboardStream = files.GetFileInfo("app.js").CreateReadStream();
        using var dashboardReader = new StreamReader(dashboardStream, Encoding.UTF8);
        var dashboardSource = await dashboardReader.ReadToEndAsync();

        var fgaFiles = new ManifestEmbeddedFileProvider(
            typeof(SqlOSFgaDashboardMiddleware).Assembly,
            "Fga/Dashboard/wwwroot");
        await using var fgaStream = fgaFiles.GetFileInfo("app.js").CreateReadStream();
        using var fgaReader = new StreamReader(fgaStream, Encoding.UTF8);
        var fgaSource = await fgaReader.ReadToEndAsync();

        dashboardSource.Should().Contain("window.SqlOSFgaDashboard.mount");
        dashboardSource.Should().Contain("initialRoute: route.componentRoute");
        dashboardSource.Should().NotContain("<iframe");
        dashboardSource.Should().NotContain("embed=1");

        fgaSource.Should().Contain("window.SqlOSFgaDashboard = Object.freeze({ mount })");
        fgaSource.Should().Contain("host.attachShadow({ mode: 'open' })");
        fgaSource.Should().Contain("loadResources()");
        fgaSource.Should().Contain("loadGrants()");
        fgaSource.Should().Contain("loadRoles()");
        fgaSource.Should().Contain("loadPermissions()");
        fgaSource.Should().Contain("loadUsers()");
        fgaSource.Should().Contain("loadAgents()");
        fgaSource.Should().Contain("loadServiceAccounts()");
        fgaSource.Should().Contain("loadUserGroups()");
        fgaSource.Should().Contain("loadAccessTester()");
        fgaSource.Should().Contain("nextCursor");
        fgaSource.Should().NotContain("pageSize=500");
        fgaSource.Should().NotContain("maxDepth=5");
    }

    private static DashboardMiddlewareHarness CreateHarness(
        Action<SqlOSDashboardOptions>? configure = null,
        bool scimEnabled = false)
    {
        var dashboardOptions = new SqlOSDashboardOptions
        {
            AuthMode = SqlOSDashboardAuthMode.Password,
            Password = "correct-password"
        };
        configure?.Invoke(dashboardOptions);

        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString("N");
        services.AddDataProtection();
        services.AddDbContext<TestSqlOSInMemoryDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddScoped<ISqlOSAuthServerDbContext>(sp => sp.GetRequiredService<TestSqlOSInMemoryDbContext>());
        var sqlosOptions = new SqlOSOptions();
        services.AddSingleton(Options.Create(sqlosOptions));
        services.AddSingleton(Options.Create(new SqlOSAuthServerOptions()));
        services.AddScoped<SqlOSCryptoService>();
        services.AddScoped<SqlOSAdminService>();
        services.AddSingleton<SqlOSDashboardSessionService>();
        services.AddSingleton<SqlOSDashboardLoginThrottlingService>();
        services.AddSingleton<SqlOSBrowserSecurityHeaders>();

        var provider = services.BuildServiceProvider(validateScopes: true);
        var middleware = new SqlOSDashboardMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status418ImATeapot;
                return Task.CompletedTask;
            },
            "/sqlos",
            new TestHostEnvironment(),
            dashboardOptions,
            scimEnabled,
            provider.GetRequiredService<SqlOSDashboardSessionService>(),
            provider.GetRequiredService<IOptions<SqlOSOptions>>());

        return new DashboardMiddlewareHarness(provider, middleware);
    }

    private sealed class DashboardMiddlewareHarness : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly SqlOSDashboardMiddleware _middleware;

        public DashboardMiddlewareHarness(ServiceProvider services, SqlOSDashboardMiddleware middleware)
        {
            _services = services;
            _middleware = middleware;
        }

        public Task<DashboardResponse> PostLoginAsync(string password, string ipAddress)
            => SendAsync(
                HttpMethods.Post,
                "/sqlos/dashboard-auth/login",
                $$"""{"password":"{{password}}"}""",
                ipAddress);

        public Task<DashboardResponse> PostLogoutAsync(string ipAddress)
            => SendAsync(HttpMethods.Post, "/sqlos/dashboard-auth/logout", null, ipAddress);

        public Task<DashboardResponse> GetDashboardAsync()
            => SendAsync(HttpMethods.Get, "/sqlos/admin/auth/organizations", null, "203.0.113.60");

        public Task<DashboardResponse> GetAsync(string path, string? queryString = null)
            => SendAsync(HttpMethods.Get, path, null, "203.0.113.61", queryString);

        public async Task<List<SqlOSAuditEvent>> ListAuditEventsAsync()
        {
            using var scope = _services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            return await context.Set<SqlOSAuditEvent>()
                .OrderBy(x => x.OccurredAt)
                .ToListAsync();
        }

        private async Task<DashboardResponse> SendAsync(
            string method,
            string path,
            string? body,
            string ipAddress,
            string? queryString = null)
        {
            using var scope = _services.CreateScope();
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider
            };
            context.Request.Method = method;
            context.Request.Path = path;
            if (!string.IsNullOrWhiteSpace(queryString))
            {
                context.Request.QueryString = new QueryString(queryString);
            }
            context.Request.Scheme = Uri.UriSchemeHttps;
            context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
            context.Response.Body = new MemoryStream();

            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
                context.Request.ContentType = "application/json";
            }

            await _middleware.InvokeAsync(
                context,
                scope.ServiceProvider.GetRequiredService<SqlOSDashboardLoginThrottlingService>());

            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
            var responseBody = await reader.ReadToEndAsync();
            var setCookie = context.Response.Headers.TryGetValue("Set-Cookie", out var cookies)
                ? cookies.Select(cookie => cookie ?? string.Empty).ToArray()
                : Array.Empty<string>();
            var retryAfter = context.Response.Headers.TryGetValue("Retry-After", out var retryAfterValues)
                ? retryAfterValues.ToString()
                : null;

            return new DashboardResponse(
                context.Response.StatusCode,
                responseBody,
                setCookie,
                retryAfter,
                context.Response.Headers["X-Frame-Options"].ToString(),
                context.Response.Headers["X-Content-Type-Options"].ToString(),
                context.Response.Headers["Referrer-Policy"].ToString(),
                context.Response.Headers["Content-Security-Policy"].ToString());
        }

        public void Dispose()
            => _services.Dispose();
    }

    private sealed record DashboardResponse(
        int StatusCode,
        string Body,
        string[] SetCookie,
        string? RetryAfter,
        string XFrameOptions,
        string ContentTypeOptions,
        string ReferrerPolicy,
        string ContentSecurityPolicy);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "SqlOS.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
