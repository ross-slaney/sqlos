using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Security;
using SqlOS.Dashboard;
using SqlOS.Extensions;
using SqlOS.Security;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSCookieMutationCsrfTests
{
    private const string TrustedOrigin = "https://auth.example.test";
    private const string SiblingOrigin = "https://evil.example.test";

    [TestMethod]
    public async Task CookieAuthenticatedMutations_ShareTheCsrfMetadata()
    {
        await using var app = await CreateAppAsync();
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToArray();

        var unsafeEndpoints = endpoints.Where(IsUnsafe).ToArray();
        unsafeEndpoints.Should().NotBeEmpty();

        var adminMutations = unsafeEndpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<SqlOSAdminRequiredMetadata>() != null)
            .ToArray();
        adminMutations.Should().NotBeEmpty();
        adminMutations.Should().OnlyContain(endpoint =>
            endpoint.Metadata.GetMetadata<SqlOSCookieMutationCsrfMetadata>() != null);

        var portalMutations = unsafeEndpoints
            .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("/sso-portal/", StringComparison.Ordinal) == true)
            .ToArray();
        portalMutations.Should().NotBeEmpty();
        portalMutations.Should().OnlyContain(endpoint =>
            endpoint.Metadata.GetMetadata<SqlOSCookieMutationCsrfMetadata>() != null);

        Post(endpoints, "/sqlos/admin/auth/api/clients/{clientId}/enable")
            .Metadata.GetMetadata<SqlOSCookieMutationCsrfMetadata>().Should().NotBeNull();
        Post(endpoints, "/sqlos/admin/auth/api/signing-keys/rotate")
            .Metadata.GetMetadata<SqlOSCookieMutationCsrfMetadata>().Should().NotBeNull();
        Post(endpoints, "/sqlos/admin/auth/sso-portal/api/signout")
            .Metadata.GetMetadata<SqlOSCookieMutationCsrfMetadata>().Should().NotBeNull();
        Post(endpoints, "/sqlos/admin/auth/sso-portal/api/setup/signout")
            .Metadata.GetMetadata<SqlOSCookieMutationCsrfMetadata>().Should().NotBeNull();
        Post(endpoints, "/sqlos/admin/auth/api/clients")
            .Metadata.GetMetadata<SqlOSCookieMutationCsrfMetadata>().Should().NotBeNull();

        Post(endpoints, "/sqlos/auth/token")
            .Metadata.GetMetadata<SqlOSCookieMutationCsrfMetadata>().Should().BeNull();
        Post(endpoints, "/sqlos/auth/password/login")
            .Metadata.GetMetadata<SqlOSCookieMutationCsrfMetadata>().Should().BeNull();
        Post(endpoints, "/sqlos/scim/v2/Users")
            .Metadata.GetMetadata<SqlOSCookieMutationCsrfMetadata>().Should().BeNull();
        Post(endpoints, "/sqlos/auth/login/password")
            .Metadata.GetMetadata<SqlOSHostedFormAntiforgeryMetadata>().Should().NotBeNull();
        Post(endpoints, "/sqlos/auth/login/password")
            .Metadata.GetMetadata<SqlOSCookieMutationCsrfMetadata>().Should().BeNull();
    }

    [TestMethod]
    public void DashboardAndPortalJavascript_SendTheCsrfHeaderWithoutBrowserStorage()
    {
        var root = FindRepositoryRoot();
        var dashboard = File.ReadAllText(Path.Combine(root, "src", "SqlOS", "Dashboard", "wwwroot", "app.js"));
        var fga = File.ReadAllText(Path.Combine(root, "src", "SqlOS", "Fga", "Dashboard", "wwwroot", "app.js"));
        var portal = File.ReadAllText(Path.Combine(root, "src", "SqlOS", "AuthServer", "Services", "SqlOSSsoPortalPageRenderer.cs"));

        dashboard.Should().Contain("\"X-SqlOS-Request\": \"1\"");
        fga.Should().Contain("'X-SqlOS-Request': '1'");
        portal.Should().Contain("\"X-SqlOS-Request\": \"1\"");
        dashboard.Should().NotContain("localStorage").And.NotContain("sessionStorage");
        fga.Should().NotContain("localStorage").And.NotContain("sessionStorage");
        portal.Should().NotContain("localStorage").And.NotContain("sessionStorage");
    }

    [TestMethod]
    public async Task Evaluate_RejectsSiblingOriginAndAmbiguousSource_AndIgnoresRawForwardedHost()
    {
        var hostile = await EvaluateAsync(request =>
        {
            request.Headers.Origin = SiblingOrigin;
            request.Headers[SqlOSCookieMutationCsrf.HeaderName] = SqlOSCookieMutationCsrf.HeaderValue;
            request.Headers["X-Forwarded-Host"] = "evil.example.test";
            request.Headers["X-Forwarded-Proto"] = "https";
        });
        hostile.IsAllowed.Should().BeFalse();
        hostile.Reason.Should().Be("untrusted_origin");

        var missingHeader = await EvaluateAsync(request => request.Headers.Origin = TrustedOrigin);
        missingHeader.IsAllowed.Should().BeFalse();
        missingHeader.Reason.Should().Be("missing_header");

        var ambiguousOrigin = await EvaluateAsync(request =>
        {
            request.Headers.Append("Origin", TrustedOrigin);
            request.Headers.Append("Origin", SiblingOrigin);
            request.Headers[SqlOSCookieMutationCsrf.HeaderName] = SqlOSCookieMutationCsrf.HeaderValue;
        });
        ambiguousOrigin.IsAllowed.Should().BeFalse();
        ambiguousOrigin.Reason.Should().Be("ambiguous_origin");

        var opaque = await EvaluateAsync(request =>
        {
            request.Headers.Origin = "null";
            request.Headers[SqlOSCookieMutationCsrf.HeaderName] = SqlOSCookieMutationCsrf.HeaderValue;
        });
        opaque.IsAllowed.Should().BeFalse();
        opaque.Reason.Should().Be("untrusted_origin");

        var missingSource = await EvaluateAsync(request =>
            request.Headers[SqlOSCookieMutationCsrf.HeaderName] = SqlOSCookieMutationCsrf.HeaderValue);
        missingSource.IsAllowed.Should().BeFalse();
        missingSource.Reason.Should().Be("untrusted_origin");

        var sameOriginFetch = await EvaluateAsync(request =>
        {
            request.Headers[SqlOSCookieMutationCsrf.HeaderName] = SqlOSCookieMutationCsrf.HeaderValue;
            request.Headers["Sec-Fetch-Site"] = "same-origin";
        });
        sameOriginFetch.IsAllowed.Should().BeTrue();

        var sameSiteFetch = await EvaluateAsync(request =>
        {
            request.Headers[SqlOSCookieMutationCsrf.HeaderName] = SqlOSCookieMutationCsrf.HeaderValue;
            request.Headers["Sec-Fetch-Site"] = "same-site";
        });
        sameSiteFetch.IsAllowed.Should().BeFalse();

        var noCookie = await EvaluateAsync(request =>
        {
            request.Headers.Origin = SiblingOrigin;
        }, includeCookie: false);
        noCookie.IsAllowed.Should().BeTrue();

        var safeMethod = await EvaluateAsync(request => request.Headers.Origin = SiblingOrigin, method: HttpMethods.Get);
        safeMethod.IsAllowed.Should().BeTrue();

        var forwardedApplied = await EvaluateAsync(
            request =>
            {
                request.Scheme = Uri.UriSchemeHttps;
                request.Host = new HostString("proxy.example.test");
                request.Headers.Origin = "https://proxy.example.test";
                request.Headers[SqlOSCookieMutationCsrf.HeaderName] = SqlOSCookieMutationCsrf.HeaderValue;
            });
        forwardedApplied.IsAllowed.Should().BeTrue();
    }

    [TestMethod]
    public async Task RejectionResult_IsStableAndStoresNothing()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await SqlOSCookieMutationCsrfResult.Instance.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Be("{\"error\":\"csrf_rejected\",\"message\":\"The request could not be verified.\"}");
    }

    private static async Task<SqlOSCookieMutationCsrfDecision> EvaluateAsync(
        Action<HttpRequest> configure,
        bool includeCookie = true,
        string method = "POST")
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new SqlOSAuthServerOptions
        {
            Issuer = $"{TrustedOrigin}/sqlos/auth",
            PublicOrigin = TrustedOrigin
        }));
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = method;
        context.Request.Scheme = Uri.UriSchemeHttps;
        context.Request.Host = new HostString("auth.example.test");
        if (includeCookie)
        {
            context.Request.Headers.Cookie = $"{SqlOSCookieMutationCsrf.DashboardSessionCookieName}=session-value";
        }

        configure(context.Request);
        return SqlOSCookieMutationCsrf.Evaluate(context);
    }

    private static bool IsUnsafe(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        return methods?.Any(method =>
            !HttpMethods.IsGet(method)
            && !HttpMethods.IsHead(method)
            && !HttpMethods.IsOptions(method)) == true;
    }

    private static RouteEndpoint Post(IEnumerable<RouteEndpoint> endpoints, string rawText)
        => endpoints.Single(endpoint =>
            endpoint.RoutePattern.RawText == rawText
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post) == true);

    private static async Task<WebApplication> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<TestSqlOSInMemoryDbContext>(database =>
            database.UseInMemoryDatabase($"cookie-csrf-routes-{Guid.NewGuid():N}"));
        builder.Services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.Issuer = $"{TrustedOrigin}/sqlos/auth";
            options.AuthServer.PublicOrigin = TrustedOrigin;
            options.AuthServer.EnableScim = true;
            options.Calendar.Enabled = true;
        });
        builder.Services.RemoveAll<IHostedService>();
        var app = builder.Build();
        await app.StartAsync();
        return app;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SqlOS.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}
