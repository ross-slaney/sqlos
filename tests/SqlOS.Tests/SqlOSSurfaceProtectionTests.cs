using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Authentication;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Models;
using SqlOS.Configuration;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// <c>app.Api</c> / <c>app.Mcp</c> are resource ids. Application routes lock with
/// <c>RequireAuthorization()</c> against the SqlOS JWT scheme (or <c>SqlOS.Mcp</c>).
/// </summary>
[TestClass]
public sealed class SqlOSSurfaceProtectionTests
{
    private const string Origin = SingleApplicationTestHost.Origin;

    [TestMethod]
    public async Task DeclaredApi_WithoutRequireAuthorization_StaysAnonymous()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(Configure, app =>
            app.MapGet("/api/me", () => "open"));
        var response = await host.Client.GetAsync("/api/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("open");
    }

    [TestMethod]
    public async Task RequireAuthorization_ProtectsMappedEndpoints_AndLeavesSiblingsUnknownPathsAndBranchesAlone()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(Configure, app =>
        {
            app.MapGet("/api/me", (HttpContext http) => http.User.Identity!.IsAuthenticated.ToString())
                .RequireAuthorization();
            app.Map("/api/legacy", branch => branch.Run(context => context.Response.WriteAsync("branch")));
            app.MapGet("/apiary", () => "public");
        });
        (await host.Client.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await host.Client.GetAsync("/API/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await host.Client.GetAsync("/api/unknown")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.Client.GetAsync("/api/legacy")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client.GetAsync("/apiary")).StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await host.MintAccessTokenAsync(Origin + "/api");
        var response = await Send(host, "/api/me", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("True");
    }

    [DataTestMethod]
    [DataRow("api", "mcp")]
    [DataRow("mcp", "api")]
    public async Task ApiAndMcpSchemes_RejectTheOtherAudience(string first, string second)
    {
        await using var host = await SingleApplicationTestHost.StartAsync(Configure, app =>
        {
            app.MapGet("/api/me", (HttpContext http) => http.GetSqlOSValidatedToken()!.Audience)
                .RequireAuthorization();
            app.MapGet("/mcp/me", (HttpContext http) => http.GetSqlOSValidatedToken()!.Audience)
                .RequireAuthorization(SqlOSJwtDefaults.McpPolicy);
        });
        var firstToken = await host.MintAccessTokenAsync($"{Origin}/{first}");
        var secondToken = await host.MintAccessTokenAsync($"{Origin}/{second}");
        (await Send(host, $"/{first}/me", firstToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(host, $"/{second}/me", firstToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(host, $"/{second}/me", secondToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(host, $"/{first}/me", secondToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => i % 2 == 0
            ? Send(host, $"/{first}/me", firstToken) : Send(host, $"/{second}/me", secondToken)));
        results.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task SecondScheme_AddSqlOSJwt_RequiresThatAudience()
    {
        var billingAudience = Origin + "/billing";
        await using var host = await SingleApplicationTestHost.StartAsync(Configure, app =>
        {
            app.MapGet("/api/me", (HttpContext http) => http.GetSqlOSValidatedToken()!.Audience)
                .RequireAuthorization();
            app.MapGet("/billing/me", (HttpContext http) => http.GetSqlOSValidatedToken()!.Audience)
                .RequireAuthorization("Billing");
        }, configureServices: services =>
        {
            services.AddAuthentication().AddSqlOSJwt("Billing", options =>
            {
                options.ExpectedAudience = billingAudience;
                options.Realm = "Review Billing";
            });
        });

        var apiToken = await host.MintAccessTokenAsync(Origin + "/api");
        var billingToken = await host.MintAccessTokenAsync(billingAudience);

        (await Send(host, "/api/me", apiToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(host, "/billing/me", apiToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var billing = await Send(host, "/billing/me", billingToken);
        billing.StatusCode.Should().Be(HttpStatusCode.OK);
        (await billing.Content.ReadAsStringAsync()).Should().Be(billingAudience);
        (await Send(host, "/api/me", billingToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var challenge = (await host.Client.GetAsync("/billing/me")).Headers.WwwAuthenticate.ToString();
        challenge.Should().Contain("realm=\"Review Billing\"");
    }

    [TestMethod]
    public async Task RequireAuthorization_RevokedSession_Returns401()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(Configure, app =>
            app.MapGet("/api/me", () => "private").RequireAuthorization());
        var token = await host.MintAccessTokenAsync(Origin + "/api");
        (await Send(host, "/api/me", token)).StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var session = await context.Set<SqlOSSession>().SingleAsync();
            session.RevokedAt = DateTime.UtcNow;
            session.RevocationReason = "test";
            await context.SaveChangesAsync();
        }

        (await Send(host, "/api/me", token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Challenge_NamesRealmAndResourceMetadata()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(
            Configure,
            app => app.MapGet("/api/me", () => "private").RequireAuthorization());
        var response = await host.Client.GetAsync("/api/me");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.Should().Contain("realm=\"Review API\"");
        challenge.Should().Contain($"resource_metadata=\"{Origin}/.well-known/oauth-protected-resource\"");
    }

    [TestMethod]
    public async Task CookieDefaultScheme_DoesNotStealRequireAuthorization()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(Configure, app =>
        {
            app.MapGet("/api/me", (HttpContext http) => http.GetSqlOSValidatedToken()!.UserId)
                .RequireAuthorization();
            app.MapGet("/public", () => "public");
        }, configureServices: services =>
        {
            services.AddAuthentication("cookie").AddCookie("cookie");
            services.AddAuthorization();
        });
        (await host.Client.GetAsync("/public")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var token = await host.MintAccessTokenAsync(Origin + "/api");
        var response = await Send(host, "/api/me", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().StartWith("usr_");
    }

    [TestMethod]
    public async Task CookieAndBearer_HandlerSeesTheBearerIdentity()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(Configure, app =>
        {
            app.UseAuthentication();
            app.MapGet("/signin", async (HttpContext http) =>
            {
                await http.SignInAsync("cookie", new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "bob")], "cookie")));
                return Results.Ok();
            });
            app.MapGet("/api/me", (HttpContext http) => Results.Json(new
            {
                user = http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier),
                tokenUser = http.GetSqlOSValidatedToken()!.UserId
            })).RequireAuthorization();
        }, configureServices: services => services.AddAuthentication("cookie").AddCookie("cookie"));

        using var signIn = await host.Client.GetAsync("/signin");
        signIn.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie = signIn.Headers.GetValues("Set-Cookie").First();
        var token = await host.MintAccessTokenAsync(Origin + "/api");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Cookie", cookie.Split(';', 2)[0]);
        using var response = await host.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"tokenUser\":\"usr_");
        body.Should().NotContain("bob", "the endpoint must expose the bearer principal, not the cookie");
    }

    [TestMethod]
    public async Task ConventionalStartupHost_RequireAuthorization_UsesSqlOSScheme()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Development")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddDbContext<TestSqlOSInMemoryDbContext>(db => db.UseInMemoryDatabase(databaseName));
                services.AddSqlOS<TestSqlOSInMemoryDbContext>(Configure);
                services.RemoveAll<IHostedService>();
                services.AddAuthentication("cookie").AddCookie("cookie");
                services.AddAuthorization();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/api/me", () => "private").RequireAuthorization();
                    endpoints.MapGet("/public", () => "public");
                });
            }));
        using var client = server.CreateClient();
        (await client.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/public")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/sqlos/auth/.well-known/openid-configuration")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostCors_ComposesWithoutSqlOSHandlingPreflight(bool endpointPolicy)
    {
        await using var host = await SingleApplicationTestHost.StartAsync(Configure, app =>
        {
            if (endpointPolicy) app.UseCors(); else app.UseCors("browser");
            // WebApplication inserts UseAuthentication/UseAuthorization immediately after
            // UseRouting unless the host calls them. CORS then never sees preflight or 401s.
            app.UseAuthentication();
            app.UseAuthorization();
            var route = app.MapGet("/api/me", () => "private").RequireAuthorization();
            if (endpointPolicy) route.RequireCors("browser");
        }, configureServices: services => services.AddCors(cors => cors.AddPolicy("browser", policy =>
            policy.WithOrigins("https://browser.example").WithMethods("GET").WithHeaders("Authorization"))));
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/me");
        preflight.Headers.Add("Origin", "https://browser.example");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "Authorization");
        var cors = await host.Client.SendAsync(preflight);
        cors.StatusCode.Should().Be(HttpStatusCode.NoContent);
        cors.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be("https://browser.example");
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        get.Headers.Add("Origin", "https://browser.example");
        var denied = await host.Client.SendAsync(get);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        denied.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be("https://browser.example");
        var token = await host.MintAccessTokenAsync(Origin + "/api");
        (await Send(host, "/api/me", token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task MappedOptionsEndpoint_IsValidatedLikeAnyOtherHandler()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(Configure, app =>
            app.MapMethods("/api/options", ["OPTIONS"], () => "private").RequireAuthorization());
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/options");
        request.Headers.Add("Origin", "https://browser.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        (await host.Client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task NoSurfaceDeclared_InstallsNoGuard()
    {
        await using var host = await SingleApplicationTestHost.StartAsync(
            options => options.UseSingleApplication("Review", app => app.Origin = Origin),
            app => app.MapGet("/api/me", () => "open"));
        (await host.Client.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static void Configure(SqlOSOptions options) => options.UseSingleApplication("Review", app =>
    {
        app.Origin = Origin;
        app.Api = "/api";
        app.Mcp = "/mcp";
    });

    private static Task<HttpResponseMessage> Send(SingleApplicationTestHost host, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return host.Client.SendAsync(request);
    }
}
