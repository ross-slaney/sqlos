using System.Text.RegularExpressions;
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
    public void DashboardJavascript_EveryMutatingFetchSendsTheRequestHeader()
    {
        var root = FindRepositoryRoot();
        foreach (var relative in new[]
        {
            Path.Combine("src", "SqlOS", "Dashboard", "wwwroot", "app.js"),
            Path.Combine("src", "SqlOS", "Fga", "Dashboard", "wwwroot", "app.js")
        })
        {
            var source = File.ReadAllText(Path.Combine(root, relative));
            source.Should().NotContain("XMLHttpRequest", "{0} sends every request through fetch", relative);
            source.Should().NotContain("sendBeacon", "{0} sends every request through fetch", relative);

            var calls = FetchCalls(source).ToArray();
            calls.Should().NotBeEmpty(relative);
            foreach (var call in calls)
            {
                // The main dashboard funnels every call through fetchJson, which sets the header
                // after spreading caller headers so no caller can drop it. The FGA dashboard sets
                // it on each mutating call.
                var mutating = Regex.IsMatch(call, @"method:\s*['""](POST|PUT|PATCH|DELETE)['""]")
                    || call.Contains("...requestOptions", StringComparison.Ordinal);
                if (!mutating)
                {
                    continue;
                }

                call.Should().MatchRegex(
                    @"['""]X-SqlOS-Request['""]\s*:\s*['""]1['""]",
                    "{0}: a mutating fetch must send the request header: {1}", relative, call);
                if (call.Contains("...requestOptions", StringComparison.Ordinal))
                {
                    call.LastIndexOf("X-SqlOS-Request", StringComparison.Ordinal).Should().BeGreaterThan(
                        call.LastIndexOf("...(requestOptions.headers", StringComparison.Ordinal),
                        "caller headers must not be able to override the request header");
                }
            }
        }
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

    [DataTestMethod]
    // Admin and FGA dashboard requests: the session cookie is not the only ambient credential.
    [DataRow(false, false, null, null, false, "missing_header", DisplayName = "admin: no cookie, no header, no origin")]
    [DataRow(false, false, SiblingOrigin, null, false, "missing_header", DisplayName = "admin: no cookie, no header, cross-site")]
    [DataRow(false, false, TrustedOrigin, null, false, "missing_header", DisplayName = "admin: no cookie, no header, same origin")]
    [DataRow(false, true, SiblingOrigin, null, false, "untrusted_origin", DisplayName = "admin: no cookie, header, cross-site")]
    [DataRow(false, true, null, null, false, "untrusted_origin", DisplayName = "admin: no cookie, header, no origin")]
    [DataRow(false, true, TrustedOrigin, null, true, "", DisplayName = "admin: no cookie, header, same origin")]
    [DataRow(false, false, null, "Bearer token-value", true, "", DisplayName = "admin: bearer without cookie")]
    [DataRow(false, false, SiblingOrigin, "Bearer token-value", true, "", DisplayName = "admin: bearer, cross-site origin")]
    [DataRow(false, false, SiblingOrigin, "Bearer ", false, "missing_header", DisplayName = "admin: empty bearer")]
    [DataRow(false, false, SiblingOrigin, "Basic dXNlcjpwYXNz", false, "missing_header", DisplayName = "admin: basic credentials")]
    [DataRow(true, false, TrustedOrigin, "Bearer token-value", false, "missing_header", DisplayName = "admin: bearer with session cookie")]
    [DataRow(true, false, TrustedOrigin, null, false, "missing_header", DisplayName = "admin: cookie, no header")]
    [DataRow(true, true, SiblingOrigin, null, false, "untrusted_origin", DisplayName = "admin: cookie, header, cross-site")]
    [DataRow(true, true, TrustedOrigin, null, true, "", DisplayName = "admin: cookie, header, same origin")]
    public async Task Evaluate_AdminScope_RequiresTheProofWithoutACookieUnlessBearer(
        bool includeCookie,
        bool includeHeader,
        string? origin,
        string? authorization,
        bool allowed,
        string reason)
    {
        var decision = await EvaluateAsync(request =>
        {
            if (includeHeader)
            {
                request.Headers[SqlOSCookieMutationCsrf.HeaderName] = SqlOSCookieMutationCsrf.HeaderValue;
            }

            if (origin != null)
            {
                request.Headers.Origin = origin;
            }

            if (authorization != null)
            {
                request.Headers.Authorization = authorization;
            }
        }, includeCookie, scope: SqlOSMutationCsrfScope.Admin);

        decision.IsAllowed.Should().Be(allowed);
        decision.Reason.Should().Be(reason);
    }

    [TestMethod]
    public async Task Evaluate_SafeMethodsAndTheSessionCookieScope_KeepTheirRules()
    {
        (await EvaluateAsync(request => request.Headers.Origin = SiblingOrigin, includeCookie: false, method: HttpMethods.Get, scope: SqlOSMutationCsrfScope.Admin))
            .IsAllowed.Should().BeTrue("reads never need the proof");
        (await EvaluateAsync(request => request.Headers.Origin = SiblingOrigin, includeCookie: false, scope: SqlOSMutationCsrfScope.SessionCookie))
            .IsAllowed.Should().BeTrue("the SSO portal keeps its cookie-scoped rule");
        (await EvaluateAsync(request => request.Headers.Origin = TrustedOrigin, includeCookie: true, scope: SqlOSMutationCsrfScope.SessionCookie))
            .Reason.Should().Be("missing_header");
        foreach (var method in new[] { HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete })
        {
            (await EvaluateAsync(request => request.Headers.Origin = TrustedOrigin, includeCookie: false, method: method, scope: SqlOSMutationCsrfScope.Admin))
                .Reason.Should().Be("missing_header", "{0} is unsafe", method);
        }
    }

    private static async Task<SqlOSCookieMutationCsrfDecision> EvaluateAsync(
        Action<HttpRequest> configure,
        bool includeCookie = true,
        string method = "POST",
        SqlOSMutationCsrfScope scope = SqlOSMutationCsrfScope.SessionCookie)
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
        return SqlOSCookieMutationCsrf.Evaluate(context, scope);
    }

    /// <summary>Returns the argument text of every <c>fetch(...)</c> call.</summary>
    private static IEnumerable<string> FetchCalls(string source)
    {
        foreach (Match match in Regex.Matches(source, @"\bfetch\("))
        {
            var depth = 0;
            for (var index = match.Index + match.Length - 1; index < source.Length; index++)
            {
                depth += source[index] switch { '(' => 1, ')' => -1, _ => 0 };
                if (depth == 0)
                {
                    yield return source[(match.Index + match.Length)..index];
                    break;
                }
            }
        }
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
