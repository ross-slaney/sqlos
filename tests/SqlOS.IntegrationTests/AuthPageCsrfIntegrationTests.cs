using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Services;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class AuthPageCsrfIntegrationTests
{
    private const string TrustedOrigin = "https://auth.example.test";
    private const string AttackerOrigin = "https://attacker.example.test";
    private const string Password = "P@ssword123!";

    [TestMethod]
    public async Task HostedPasswordLogin_CrossSitePostWithoutAntiforgery_IsRejectedAndDoesNotSetCookie()
    {
        await using var server = await AuthPageCsrfServer.CreateAsync();
        using var client = server.App.GetTestClient();
        client.BaseAddress = new Uri(TrustedOrigin);

        using var attack = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/login/password")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = server.Email,
                ["password"] = Password
            })
        };
        attack.Headers.TryAddWithoutValidation("Origin", AttackerOrigin);
        var rejected = await client.SendAsync(attack);

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        rejected.Headers.TryGetValues("Set-Cookie", out var rejectedCookies).Should().BeFalse();
        (await server.CountAuthPageSessionsAsync()).Should().Be(0);

        var authorize = await client.GetAsync(
            "/sqlos/auth/authorize?response_type=code&client_id=browser-client"
            + "&redirect_uri=https%3A%2F%2Fclient.example.test%2Fcallback"
            + "&scope=openid%20profile%20email&state=victim-state"
            + "&code_challenge=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&code_challenge_method=S256");
        authorize.StatusCode.Should().Be(HttpStatusCode.OK);
        authorize.Headers.Location.Should().BeNull("the attacker login must not create a reusable AuthPage session");
        (await authorize.Content.ReadAsStringAsync()).Should().Contain("/sqlos/auth/login/identify");
    }

    [TestMethod]
    public async Task HostedStandaloneLogin_ValidAntiforgery_SetsFreshSession()
    {
        await using var server = await AuthPageCsrfServer.CreateAsync();
        using var client = server.App.GetTestClient();
        client.BaseAddress = new Uri(TrustedOrigin);

        var loginPage = await client.GetAsync("/sqlos/auth/login");
        loginPage.EnsureSuccessStatusCode();
        var html = await loginPage.Content.ReadAsStringAsync();
        var requestToken = ExtractInputValue(html, "__RequestVerificationToken");
        var antiforgeryCookie = ExtractCookie(loginPage, "sqlos_auth_page_csrf_");

        using var login = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/login/password")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = server.Email,
                ["password"] = Password,
                ["__RequestVerificationToken"] = requestToken
            })
        };
        login.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
        login.Headers.TryAddWithoutValidation("Origin", TrustedOrigin);
        var response = await client.SendAsync(login);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/sqlos/auth/login?status=signed-in", UriKind.Relative));
        var authPageCookie = ExtractCookie(response, "sqlos_auth_page=");
        authPageCookie.Should().NotContain(antiforgeryCookie.Split('=', 2)[1]);

        await using var scope = server.App.Services.CreateAsyncScope();
        var sessionService = scope.ServiceProvider.GetRequiredService<SqlOSAuthPageSessionService>();
        var sessionContext = new DefaultHttpContext();
        sessionContext.Request.Headers.Cookie = authPageCookie;
        var session = await sessionService.TryGetSessionAsync(sessionContext);
        session.Should().NotBeNull();
        session!.User.Id.Should().Be(server.UserId);
        session.AuthenticationMethod.Should().Be("password");
    }

    [TestMethod]
    public async Task HostedStandaloneLogin_OpaqueOrigin_IsRejectedAndDoesNotSetCookie()
    {
        await using var server = await AuthPageCsrfServer.CreateAsync();
        using var client = server.App.GetTestClient();
        client.BaseAddress = new Uri(TrustedOrigin);

        var loginPage = await client.GetAsync("/sqlos/auth/login");
        loginPage.EnsureSuccessStatusCode();
        var html = await loginPage.Content.ReadAsStringAsync();
        var requestToken = ExtractInputValue(html, "__RequestVerificationToken");
        var antiforgeryCookie = ExtractCookie(loginPage, "sqlos_auth_page_csrf_");

        using var login = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/login/password")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = server.Email,
                ["password"] = Password,
                ["__RequestVerificationToken"] = requestToken
            })
        };
        login.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
        login.Headers.TryAddWithoutValidation("Origin", "null");
        var response = await client.SendAsync(login);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await server.CountAuthPageSessionsAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task HostedAuthPage_UsesLockedBrowserSecurityHeadersAndPerResponseNonce()
    {
        await using var server = await AuthPageCsrfServer.CreateAsync();
        using var client = server.App.GetTestClient();
        client.BaseAddress = new Uri(TrustedOrigin);

        var first = await client.GetAsync("/sqlos/auth/login");
        var second = await client.GetAsync("/sqlos/auth/login");
        var firstHtml = await first.Content.ReadAsStringAsync();
        var secondHtml = await second.Content.ReadAsStringAsync();
        var firstNonce = Regex.Match(firstHtml, "<style nonce=\"([A-Za-z0-9_-]+)\"").Groups[1].Value;
        var secondNonce = Regex.Match(secondHtml, "<style nonce=\"([A-Za-z0-9_-]+)\"").Groups[1].Value;

        first.Headers.GetValues("X-Frame-Options").Should().ContainSingle("DENY");
        first.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
        first.Headers.GetValues("Referrer-Policy").Should().ContainSingle("same-origin");
        var policy = first.Headers.GetValues("Content-Security-Policy").Single();
        policy.Should().Contain("frame-ancestors 'none'");
        policy.Should().Contain($"'nonce-{firstNonce}'");
        policy.Should().NotContain("unsafe-inline");
        firstHtml.Should().Contain($"<script nonce=\"{firstNonce}\">");
        firstNonce.Should().NotBeNullOrWhiteSpace();
        secondNonce.Should().NotBe(firstNonce, "every HTML response receives a fresh nonce");
    }

    private static string ExtractInputValue(string html, string name)
    {
        var match = Regex.Match(
            html,
            $@"name=""{Regex.Escape(name)}"" value=""([^""]+)""",
            RegexOptions.CultureInvariant);
        match.Success.Should().BeTrue($"the hosted page should contain {name}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string ExtractCookie(HttpResponseMessage response, string prefix)
    {
        response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
        var cookie = values!.Select(value => value.Split(';', 2)[0])
            .Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return cookie;
    }

    private sealed class AuthPageCsrfServer : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required string Email { get; init; }
        public required string UserId { get; init; }

        public static async Task<AuthPageCsrfServer> CreateAsync()
        {
            await using var bootstrapContext = await AspireFixture.CreateIsolatedAuthContextAsync("AuthPageCsrf");
            var connectionString = bootstrapContext.Database.GetConnectionString()!;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSDbContext>(database => database.UseSqlServer(connectionString));
            builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
            {
                options.AuthServer.Issuer = $"{TrustedOrigin}/sqlos/auth";
                options.AuthServer.BasePath = "/sqlos/auth";
                options.AuthServer.SeedBrowserClient(
                    "browser-client",
                    "Browser Client",
                    "https://client.example.test/callback");
            });
            builder.Services.RemoveAll<IHostedService>();

            var app = builder.Build();
            app.MapAuthServer("/sqlos/auth");
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SqlOSBootstrapper>().InitializeAsync();
            var email = $"csrf-{Guid.NewGuid():N}@example.test";
            var user = await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .CreateUserAsync(new SqlOSCreateUserRequest("CSRF Test User", email, Password));
            await app.StartAsync();

            return new AuthPageCsrfServer
            {
                App = app,
                Email = email,
                UserId = user.Id
            };
        }

        public async Task<int> CountAuthPageSessionsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>()
                .Set<SqlOS.AuthServer.Models.SqlOSTemporaryToken>()
                .CountAsync(token => token.Purpose == "auth_page_session" && token.ConsumedAt == null);
        }

        public async ValueTask DisposeAsync()
        {
            await using (var scope = App.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.EnsureDeletedAsync();
            }
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
