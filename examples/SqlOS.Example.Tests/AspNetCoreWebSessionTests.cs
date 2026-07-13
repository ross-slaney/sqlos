using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Example.AspNetCoreWeb.Pages;

namespace SqlOS.Example.Tests;

[TestClass]
public sealed class AspNetCoreWebSessionTests
{
    [TestMethod]
    public async Task ExpiringAccessToken_RotatesRefreshToken_RenewsTicket_AndCallsApi()
    {
        var handler = new RecordingSqlOSHandler();
        var authentication = CreateAuthentication(
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            refreshToken: "original-refresh");
        var model = CreatePageModel(handler, authentication);

        await model.OnGetAsync(CancellationToken.None);

        model.ApiSucceeded.Should().BeTrue();
        model.ApiStatusCode.Should().Be(200);
        model.ApiResponse.Should().Contain("usr_test");

        authentication.SignedInProperties.Should().NotBeNull();
        var renewedProperties = authentication.SignedInProperties!;
        renewedProperties.GetTokenValue("access_token")
            .Should().Be("refreshed-access");
        renewedProperties.GetTokenValue("refresh_token")
            .Should().Be("rotated-refresh");

        DateTimeOffset.Parse(renewedProperties.GetTokenValue("expires_at")!)
            .Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(9));
        handler.RefreshForm.Should().Contain("client_id=custom-client");
        handler.RefreshForm.Should().Contain("refresh_token=original-refresh");
        handler.ApiAuthorization.Should().Be("Bearer refreshed-access");
    }

    [TestMethod]
    public async Task RemoteLogoutTimeout_StillClearsLocalCookie()
    {
        var handler = new RecordingSqlOSHandler { TimeoutLogout = true };
        var authentication = CreateAuthentication(
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            refreshToken: "refresh-to-revoke");
        var model = CreatePageModel(handler, authentication);

        var result = await model.OnPostLogoutAsync(CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        authentication.SignedOutScheme.Should()
            .Be(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static IndexModel CreatePageModel(
        HttpMessageHandler handler,
        RecordingAuthenticationService authentication)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlOS:Origin"] = "https://sqlos.example.test",
                ["SqlOS:ClientId"] = "custom-client"
            })
            .Build();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://sqlos.example.test")
        };
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = authentication.Principal
        };
        var model = new IndexModel(
            new SingleHttpClientFactory(httpClient),
            configuration,
            NullLogger<IndexModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = httpContext }
        };

        return model;
    }

    private static RecordingAuthenticationService CreateAuthentication(
        DateTimeOffset expiresAt,
        string refreshToken)
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "usr_test"),
                    new Claim("client_id", "custom-client"),
                    new Claim("session_id", "ses_test")
                ],
                CookieAuthenticationDefaults.AuthenticationScheme));
        var properties = new AuthenticationProperties();
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = "expired-access" },
            new AuthenticationToken { Name = "refresh_token", Value = refreshToken },
            new AuthenticationToken { Name = "token_type", Value = "Bearer" },
            new AuthenticationToken { Name = "expires_at", Value = expiresAt.ToString("O") }
        ]);

        return new RecordingAuthenticationService(principal, properties);
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingSqlOSHandler : HttpMessageHandler
    {
        public string? RefreshForm { get; private set; }
        public string? ApiAuthorization { get; private set; }
        public bool TimeoutLogout { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            switch (request.RequestUri?.AbsolutePath)
            {
                case "/sqlos/auth/token":
                    RefreshForm = await request.Content!.ReadAsStringAsync(cancellationToken);
                    return Json("""
                        {
                          "access_token": "refreshed-access",
                          "refresh_token": "rotated-refresh",
                          "token_type": "Bearer",
                          "expires_in": 600
                        }
                        """);

                case "/api/me":
                    ApiAuthorization = request.Headers.Authorization?.ToString();
                    return Json("""
                        {
                          "subjectId": "usr_test",
                          "organizationId": "org_test",
                          "clientId": "custom-client",
                          "claims": []
                        }
                        """);

                case "/sqlos/auth/logout" when TimeoutLogout:
                    throw new TaskCanceledException("Simulated remote logout timeout.");

                case "/sqlos/auth/logout":
                    return new HttpResponseMessage(HttpStatusCode.NoContent);

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string content)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
    }

    private sealed class RecordingAuthenticationService(
        ClaimsPrincipal principal,
        AuthenticationProperties properties) : IAuthenticationService
    {
        public ClaimsPrincipal Principal { get; } = principal;
        public AuthenticationProperties? SignedInProperties { get; private set; }
        public string? SignedOutScheme { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(
            HttpContext context,
            string? scheme)
            => Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(
                    Principal,
                    SignedInProperties ?? properties,
                    scheme ?? CookieAuthenticationDefaults.AuthenticationScheme)));

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? challengeProperties)
            => Task.CompletedTask;

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? forbidProperties)
            => Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal signedInPrincipal,
            AuthenticationProperties? signedInProperties)
        {
            SignedInProperties = signedInProperties;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? signOutProperties)
        {
            SignedOutScheme = scheme;
            return Task.CompletedTask;
        }
    }
}
