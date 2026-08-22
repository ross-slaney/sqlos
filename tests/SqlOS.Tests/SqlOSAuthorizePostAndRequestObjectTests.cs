using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// OIDC Core 3.1.2.1: the authorization endpoint accepts GET (query) and POST
/// (form) with identical semantics. OIDC Core 6.1: request objects are not
/// supported, so a validated client gets an explicit
/// request_not_supported / request_uri_not_supported error redirect.
/// </summary>
[TestClass]
public sealed class SqlOSAuthorizePostAndRequestObjectTests
{
    private const string ClientId = "authorize-post-client";
    private const string RedirectUri = "https://client.example.test/callback";
    private const string CodeChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task PostAuthorize_WithFormParameters_RendersLoginPageWithRequestId()
    {
        await using var harness = await Harness.StartAsync();

        using var response = await harness.PostAuthorizeAsync(BaseParameters());

        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, html);
        html.Should().Contain("name=\"requestId\"", "a POSTed authorize request must start a real authorization request");
    }

    [TestMethod]
    public async Task GetAndPostAuthorize_ShareValidation_MissingResponseTypeIsBadRequestOnBoth()
    {
        await using var harness = await Harness.StartAsync();
        var parameters = BaseParameters();
        parameters.Remove("response_type");

        using var get = await harness.GetAuthorizeAsync(parameters);
        using var post = await harness.PostAuthorizeAsync(parameters);

        get.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        post.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task PostAuthorize_PromptNoneWithoutSession_RedirectsLoginRequired()
    {
        await using var harness = await Harness.StartAsync();
        var parameters = BaseParameters();
        parameters["prompt"] = "none";

        using var response = await harness.PostAuthorizeAsync(parameters);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        location.AbsoluteUri.Should().StartWith(RedirectUri);
        var query = QueryHelpers.ParseQuery(location.Query);
        query["error"].ToString().Should().Be("login_required");
        query["state"].ToString().Should().Be("state-post");
    }

    [TestMethod]
    public async Task Authorize_WithRequestParameter_RedirectsRequestNotSupported()
    {
        await using var harness = await Harness.StartAsync();
        var parameters = BaseParameters();
        parameters["request"] = "eyJhbGciOiJub25lIn0.eyJzdGF0ZSI6ImlubmVyIn0.";

        using var response = await harness.GetAuthorizeAsync(parameters);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        location.AbsoluteUri.Should().StartWith(RedirectUri);
        var query = QueryHelpers.ParseQuery(location.Query);
        query["error"].ToString().Should().Be("request_not_supported");
        query["error_description"].ToString().Should().Be("The request parameter is not supported.");
        query["state"].ToString().Should().Be("state-post", "the OUTER state must be echoed even when the RP tucked one inside the request object");
    }

    [TestMethod]
    public async Task PostAuthorize_WithRequestUriParameter_RedirectsRequestUriNotSupported()
    {
        await using var harness = await Harness.StartAsync();
        var parameters = BaseParameters();
        parameters["request_uri"] = "https://client.example.test/request.jwt";

        using var response = await harness.PostAuthorizeAsync(parameters);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        query["error"].ToString().Should().Be("request_uri_not_supported");
        query["error_description"].ToString().Should().Be("The request_uri parameter is not supported.");
        query["state"].ToString().Should().Be("state-post");
    }

    [TestMethod]
    public async Task Authorize_WithBothRequestAndRequestUri_RequestTakesPrecedence()
    {
        await using var harness = await Harness.StartAsync();
        var parameters = BaseParameters();
        parameters["request"] = "eyJhbGciOiJub25lIn0.e30.";
        parameters["request_uri"] = "https://client.example.test/request.jwt";

        using var response = await harness.GetAuthorizeAsync(parameters);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(response.Headers.Location!.Query)["error"].ToString()
            .Should().Be("request_not_supported");
    }

    [TestMethod]
    public async Task Authorize_RequestParameterWithUnknownClient_ShowsErrorPageInsteadOfRedirect()
    {
        await using var harness = await Harness.StartAsync();
        var parameters = BaseParameters();
        parameters["client_id"] = "unknown-client";
        parameters["request"] = "eyJhbGciOiJub25lIn0.e30.";

        using var response = await harness.GetAuthorizeAsync(parameters);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "an unvalidated client must keep getting the generic error page, never an error redirect");
        response.Headers.Location.Should().BeNull();
    }

    private static Dictionary<string, string> BaseParameters()
        => new()
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["state"] = "state-post",
            ["scope"] = "openid",
            ["code_challenge"] = CodeChallenge,
            ["code_challenge_method"] = "S256"
        };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly HttpClient _client;

        private Harness(IHost host)
        {
            _host = host;
            _client = host.GetTestClient();
        }

        public static async Task<Harness> StartAsync()
        {
            var databaseName = Guid.NewGuid().ToString("N");
            var host = await new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddLogging();
                        services.AddDbContext<TestSqlOSInMemoryDbContext>(db =>
                            db.UseInMemoryDatabase(databaseName));
                        services.AddSqlOS<TestSqlOSInMemoryDbContext>(sqlos =>
                        {
                            sqlos.AuthServer.Issuer = "https://tests.example.local/sqlos/auth";
                            sqlos.AuthServer.BasePath = "/sqlos/auth";
                            sqlos.AuthServer.SeedBrowserClient(ClientId, "Authorize POST Client", RedirectUri);
                        });
                        foreach (var hostedService in services
                            .Where(x => x.ServiceType == typeof(IHostedService))
                            .ToList())
                        {
                            services.Remove(hostedService);
                        }
                        services.AddSingleton<ISqlOSAuthEmailSender>(
                            new TestAuthEmailSender { IsConfigured = true });
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapAuthServer("/sqlos/auth"));
                    }))
                .StartAsync();

            using var scope = host.Services.CreateScope();
            var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var settings = scope.ServiceProvider.GetRequiredService<SqlOSSettingsService>();
            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.EnsureDefaultSettingsAsync();
            return new Harness(host);
        }

        public async Task<HttpResponseMessage> GetAuthorizeAsync(IDictionary<string, string> parameters)
            => await _client.GetAsync(QueryHelpers.AddQueryString(
                "/sqlos/auth/authorize",
                parameters.ToDictionary(x => x.Key, x => (string?)x.Value)));

        public async Task<HttpResponseMessage> PostAuthorizeAsync(IDictionary<string, string> parameters)
            => await _client.PostAsync("/sqlos/auth/authorize", new FormUrlEncodedContent(parameters));

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            _host.Dispose();
            await Task.CompletedTask;
        }
    }
}
