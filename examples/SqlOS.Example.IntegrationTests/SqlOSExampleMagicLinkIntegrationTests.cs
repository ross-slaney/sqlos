using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Email.Interfaces;
using SqlOS.Example.IntegrationTests.Infrastructure;

namespace SqlOS.Example.IntegrationTests;

[TestClass]
public sealed class SqlOSExampleMagicLinkIntegrationTests
{
    [TestMethod]
    public async Task ApiMagicLink_IsHostHeaderSafeAndAtomicallySingleUse()
    {
        using var factory = CreateFactory(enableHeadlessAuthPage: true, enableMagicLink: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var sender = factory.Services.GetRequiredService<TestAuthEmailSender>();
        var email = $"magic-api-{Guid.NewGuid():N}@example.com";
        var organizationId = await CreateOrganizationAsync(client, $"Magic API Org {Guid.NewGuid():N}");
        var userId = await CreateUserAsync(client, email, "Magic API User", "P@ssword123!");
        await CreateMembershipAsync(client, organizationId, userId);

        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/magic-link/start")
        {
            Content = JsonContent.Create(new { email, organizationId })
        };
        startRequest.Headers.Host = "attacker.example";
        var startResponse = await client.SendAsync(startRequest);
        startResponse.EnsureSuccessStatusCode();

        var message = sender.GetLatestMessage(email);
        message.TextBody.Should().Contain("https://localhost/sqlos/auth/login/magic-link/complete?token=");
        message.TextBody.Should().NotContain("attacker.example");
        var token = ExtractToken(message.TextBody);

        var completions = await Task.WhenAll(
            client.PostAsJsonAsync("/api/v1/auth/magic-link/complete", new { token }),
            client.PostAsJsonAsync("/api/v1/auth/magic-link/complete", new { token }));

        completions.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        completions.Count(response => response.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);
        var rejected = completions.Single(response => response.StatusCode == HttpStatusCode.BadRequest);
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        rejectedBody.Should().Contain("The sign-in link is invalid or expired.");
        rejectedBody.Should().NotContain(token);

        var replay = await client.PostAsJsonAsync("/api/v1/auth/magic-link/complete", new { token });
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task HeadlessMagicLink_CompletesAuthorizationCodeFlow()
    {
        using var factory = CreateFactory(enableHeadlessAuthPage: true, enableMagicLink: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var sender = factory.Services.GetRequiredService<TestAuthEmailSender>();
        var email = $"magic-headless-{Guid.NewGuid():N}@example.com";
        var organizationId = await CreateOrganizationAsync(client, $"Magic Headless Org {Guid.NewGuid():N}");
        var userId = await CreateUserAsync(client, email, "Magic Headless User", "P@ssword123!");
        await CreateMembershipAsync(client, organizationId, userId);
        const string verifier = "headless-magic-link-verifier-123456789-rfc7636-secure-value";
        var authorization = await StartAuthorizationAsync(client, verifier, expectHostedPage: false);
        var requestId = authorization.RequestId;

        var start = await client.PostAsJsonAsync("/sqlos/auth/headless/magic-link/start", new { requestId, email });
        start.EnsureSuccessStatusCode();
        var token = ExtractToken(sender.GetLatestMessage(email).TextBody);

        var complete = await client.PostAsJsonAsync("/sqlos/auth/headless/magic-link/complete", new { requestId, token });
        complete.EnsureSuccessStatusCode();
        using var completionJson = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        completionJson.RootElement.GetProperty("type").GetString().Should().Be("redirect");
        var redirectUrl = completionJson.RootElement.GetProperty("redirectUrl").GetString()!;

        using var tokenJson = await ExchangeAuthCodeAsync(client, redirectUrl, verifier);
        await AssertMagicLinkSessionAsync(client, tokenJson, organizationId);
    }

    [TestMethod]
    public async Task HostedMagicLink_CompletesAuthorizationCodeFlow()
    {
        using var factory = CreateFactory(enableHeadlessAuthPage: false, enableMagicLink: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var sender = factory.Services.GetRequiredService<TestAuthEmailSender>();
        var email = $"magic-hosted-{Guid.NewGuid():N}@example.com";
        var organizationId = await CreateOrganizationAsync(client, $"Magic Hosted Org {Guid.NewGuid():N}");
        var userId = await CreateUserAsync(client, email, "Magic Hosted User", "P@ssword123!");
        await CreateMembershipAsync(client, organizationId, userId);
        const string verifier = "hosted-magic-link-verifier-123456789-rfc7636-secure-value";
        var authorization = await StartAuthorizationAsync(client, verifier, expectHostedPage: true);
        var requestId = authorization.RequestId;
        var antiforgeryToken = authorization.AntiforgeryToken!;

        var start = await client.PostAsync("/sqlos/auth/login/magic-link/start", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["requestId"] = requestId,
            ["email"] = email,
            ["__RequestVerificationToken"] = antiforgeryToken
        }));
        start.EnsureSuccessStatusCode();
        var token = ExtractToken(sender.GetLatestMessage(email).TextBody);

        var complete = await client.PostAsync("/sqlos/auth/login/magic-link/complete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["requestId"] = requestId,
            ["token"] = token,
            ["__RequestVerificationToken"] = antiforgeryToken
        }));
        // Hosted-form POST completions answer 200 with a same-origin meta-refresh
        // interstitial rather than a direct 302 (browsers enforce the page CSP's
        // form-action 'self' against a form submission's redirect target), so read
        // the client redirect the way a browser would.
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        var interstitial = await complete.Content.ReadAsStringAsync();
        var clientRedirect = WebUtility.HtmlDecode(
            Regex.Match(interstitial, "http-equiv=\"refresh\" content=\"0;url=([^\"]+)\"").Groups[1].Value);
        clientRedirect.Should().NotBeNullOrWhiteSpace();

        using var tokenJson = await ExchangeAuthCodeAsync(client, clientRedirect, verifier);
        await AssertMagicLinkSessionAsync(client, tokenJson, organizationId);
    }

    [TestMethod]
    public async Task MagicLink_RemainsUnavailableUnlessExplicitlyEnabled()
    {
        using var factory = CreateFactory(enableHeadlessAuthPage: true, enableMagicLink: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var sender = factory.Services.GetRequiredService<TestAuthEmailSender>();
        var email = $"magic-disabled-{Guid.NewGuid():N}@example.com";

        var response = await client.PostAsJsonAsync("/api/v1/auth/magic-link/start", new { email });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        sender.Invoking(value => value.GetLatestMessage(email)).Should().Throw<InvalidOperationException>();
    }

    private static WebApplicationFactory<Program> CreateFactory(bool enableHeadlessAuthPage, bool enableMagicLink)
        => ExampleApiFixture.CreateFactory(builder =>
        {
            builder.UseSetting("SqlOS:EnableHeadlessAuthPage", enableHeadlessAuthPage ? "true" : "false");
            builder.UseSetting("SqlOS:EnableMagicLink", enableMagicLink ? "true" : "false");
            builder.UseSetting("SqlOS:HeadlessFrontendUrl", "http://localhost:3000");
            builder.UseSetting("ExampleFrontend:ClientId", "example-web");
            builder.UseSetting("ExampleFrontend:CallbackUrl", "http://localhost:3000/auth/callback");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISqlOSAuthEmailSender>();
                services.RemoveAll<ISqlOSEmailSender>();
                services.AddSingleton<TestAuthEmailSender>();
                services.AddSingleton<ISqlOSAuthEmailSender>(sp => sp.GetRequiredService<TestAuthEmailSender>());
                services.AddSingleton<ISqlOSEmailSender>(sp => sp.GetRequiredService<TestAuthEmailSender>());
            });
        });

    private static async Task<AuthorizationStart> StartAuthorizationAsync(HttpClient client, string verifier, bool expectHostedPage)
    {
        var response = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "example-web",
            ["redirect_uri"] = "http://localhost:3000/auth/callback",
            ["state"] = $"magic-state-{Guid.NewGuid():N}",
            ["scope"] = "openid profile email",
            ["code_challenge"] = CreateCodeChallenge(verifier),
            ["code_challenge_method"] = "S256"
        }));

        if (expectHostedPage)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var html = await response.Content.ReadAsStringAsync();
            return new AuthorizationStart(
                ExtractHiddenInput(html, "requestId"),
                ExtractHiddenInput(html, "__RequestVerificationToken"));
        }

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return new AuthorizationStart(
            QueryHelpers.ParseQuery(response.Headers.Location!.Query)["request"].ToString(),
            null);
    }

    private sealed record AuthorizationStart(string RequestId, string? AntiforgeryToken);

    private static async Task AssertMagicLinkSessionAsync(HttpClient client, JsonDocument tokenJson, string organizationId)
    {
        var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        session.RootElement.GetProperty("session").GetProperty("authenticationMethod").GetString().Should().Be("magic_link");
        session.RootElement.GetProperty("token").GetProperty("organizationId").GetString().Should().Be(organizationId);
    }

    private static async Task<JsonDocument> ExchangeAuthCodeAsync(HttpClient client, string redirectUrl, string verifier)
    {
        var query = QueryHelpers.ParseQuery(new Uri(redirectUrl).Query);
        var response = await client.PostAsync("/sqlos/auth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = query["code"].ToString(),
            ["client_id"] = "example-web",
            ["redirect_uri"] = "http://localhost:3000/auth/callback",
            ["code_verifier"] = verifier
        }));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateOrganizationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/sqlos/admin/auth/api/organizations", new { name });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateUserAsync(HttpClient client, string email, string displayName, string password)
    {
        var response = await client.PostAsJsonAsync("/sqlos/admin/auth/api/users", new { displayName, email, password });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task CreateMembershipAsync(HttpClient client, string organizationId, string userId)
    {
        var response = await client.PostAsJsonAsync("/sqlos/admin/auth/api/memberships", new { organizationId, userId, role = "member" });
        response.EnsureSuccessStatusCode();
    }

    private static string ExtractToken(string? body)
    {
        var match = Regex.Match(body ?? string.Empty, @"[?&]token=([^\s&]+)");
        match.Success.Should().BeTrue();
        return Uri.UnescapeDataString(match.Groups[1].Value.TrimEnd('.', ')'));
    }

    private static string ExtractHiddenInput(string html, string fieldName)
    {
        var match = Regex.Match(html, $"name=\\\"{Regex.Escape(fieldName)}\\\" value=\\\"([^\\\"]+)\\\"");
        match.Success.Should().BeTrue();
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string CreateCodeChallenge(string verifier)
        => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
}
