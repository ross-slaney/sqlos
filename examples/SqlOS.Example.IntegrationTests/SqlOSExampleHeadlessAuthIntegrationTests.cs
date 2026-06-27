using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
public sealed class SqlOSExampleHeadlessAuthIntegrationTests
{
    [TestMethod]
    public async Task HeadlessSignup_PersistsReferralSource_InExampleProfile()
    {
        using var factory = CreateHeadlessOtpFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var sender = factory.Services.GetRequiredService<TestAuthEmailSender>();

        var email = $"headless-{Guid.NewGuid():N}@example.com";
        const string verifier = "headless-test-verifier-123456789";
        var accessToken = await SignUpWithEmailOtpAsync(
            client,
            sender,
            email,
            "Taylor Example",
            "Northwind Retail",
            "Taylor",
            "Example",
            verifier);
        accessToken.Should().NotBeNullOrWhiteSpace();

        var profileRequest = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var profileResponse = await client.SendAsync(profileRequest);
        profileResponse.EnsureSuccessStatusCode();

        var profileJson = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync());
        profileJson.RootElement.GetProperty("profile").GetProperty("referralSource").GetString().Should().Be("docs");
        profileJson.RootElement.GetProperty("profile").GetProperty("organizationName").GetString().Should().Be("Northwind Retail");
        profileJson.RootElement.GetProperty("email").GetString().Should().Be(email);
    }

    [TestMethod]
    public async Task PromptNone_WithoutSession_ReturnsLoginRequiredRedirect()
    {
        using var factory = ExampleApiFixture.CreateFactory(builder =>
        {
            builder.UseSetting("SqlOS:HeadlessFrontendUrl", "http://localhost:3000");
            builder.UseSetting("ExampleFrontend:ClientId", "example-web");
            builder.UseSetting("ExampleFrontend:CallbackUrl", "http://localhost:3000/auth/callback");
        });

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        const string verifier = "silent-test-verifier-123456789";
        var challenge = CreateCodeChallenge(verifier);

        var authorizeResponse = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "example-web",
            ["redirect_uri"] = "http://localhost:3000/auth/callback",
            ["state"] = "silent-state",
            ["scope"] = "openid profile email",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "none"
        }));

        authorizeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        authorizeResponse.Headers.Location.Should().NotBeNull();
        authorizeResponse.Headers.Location!.ToString().Should().StartWith("http://localhost:3000/auth/callback?");
        var query = QueryHelpers.ParseQuery(authorizeResponse.Headers.Location.Query);
        query["error"].ToString().Should().Be("login_required");
        query["state"].ToString().Should().Be("silent-state");
    }

    [TestMethod]
    public async Task HeadlessSignup_EstablishesSession_ForPromptNoneAuthorize()
    {
        using var factory = CreateHeadlessOtpFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var sender = factory.Services.GetRequiredService<TestAuthEmailSender>();

        var email = $"silent-{Guid.NewGuid():N}@example.com";
        const string firstVerifier = "headless-session-verifier-123456789";
        _ = await SignUpWithEmailOtpAsync(
            client,
            sender,
            email,
            "Taylor Silent",
            "Northwind Retail",
            "Taylor",
            "Silent",
            firstVerifier);

        const string secondVerifier = "prompt-none-verifier-987654321";
        var secondChallenge = CreateCodeChallenge(secondVerifier);
        var silentAuthorize = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "example-web",
            ["redirect_uri"] = "http://localhost:3000/auth/callback",
            ["state"] = "silent-success-state",
            ["scope"] = "openid profile email",
            ["code_challenge"] = secondChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "none",
            ["login_hint"] = email
        }));

        silentAuthorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        silentAuthorize.Headers.Location.Should().NotBeNull();
        silentAuthorize.Headers.Location!.ToString().Should().StartWith("http://localhost:3000/auth/callback?");
        var silentQuery = QueryHelpers.ParseQuery(silentAuthorize.Headers.Location.Query);
        silentQuery.ContainsKey("error").Should().BeFalse();
        silentQuery["code"].ToString().Should().NotBeNullOrWhiteSpace();
        silentQuery["state"].ToString().Should().Be("silent-success-state");
    }

    private static WebApplicationFactory<Program> CreateHeadlessOtpFactory()
        => ExampleApiFixture.CreateFactory(builder =>
        {
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

    private static async Task<string> SignUpWithEmailOtpAsync(
        HttpClient client,
        TestAuthEmailSender sender,
        string email,
        string displayName,
        string organizationName,
        string firstName,
        string lastName,
        string verifier)
    {
        var challenge = CreateCodeChallenge(verifier);

        var authorizeResponse = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "example-web",
            ["redirect_uri"] = "http://localhost:3000/auth/callback",
            ["state"] = "headless-state",
            ["scope"] = "openid profile email",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["view"] = "signup"
        }));

        authorizeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var handoffLocation = authorizeResponse.Headers.Location;
        handoffLocation.Should().NotBeNull();
        var handoffQuery = QueryHelpers.ParseQuery(handoffLocation!.Query);
        var requestId = handoffQuery["request"].ToString();
        requestId.Should().NotBeNullOrWhiteSpace();

        var startResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/signup/email-otp/start", new
        {
            requestId,
            displayName,
            email,
            organizationName,
            customFields = new
            {
                referralSource = "docs",
                firstName,
                lastName
            }
        });

        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        startJson.RootElement.GetProperty("type").GetString().Should().Be("view");
        var viewModel = startJson.RootElement.GetProperty("viewModel");
        viewModel.GetProperty("view").GetString().Should().Be("email-otp-signup-verify");
        var signupToken = viewModel.GetProperty("signupToken").GetString();
        var challengeToken = viewModel.GetProperty("challengeToken").GetString();

        var verifyResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/signup/email-otp/verify", new
        {
            requestId,
            signupToken,
            challengeToken,
            code = sender.GetLatestCode(email)
        });
        verifyResponse.EnsureSuccessStatusCode();
        var signupJson = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync());
        signupJson.RootElement.GetProperty("type").GetString().Should().Be("redirect");

        var finalRedirect = signupJson.RootElement.GetProperty("redirectUrl").GetString();
        finalRedirect.Should().NotBeNullOrWhiteSpace();
        var finalRedirectUri = new Uri(finalRedirect!);
        var redirectQuery = QueryHelpers.ParseQuery(finalRedirectUri.Query);
        var code = redirectQuery["code"].ToString();
        code.Should().NotBeNullOrWhiteSpace();

        var tokenResponse = await client.PostAsync("/sqlos/auth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = "example-web",
            ["redirect_uri"] = "http://localhost:3000/auth/callback",
            ["code_verifier"] = verifier
        }));

        tokenResponse.EnsureSuccessStatusCode();
        var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        return tokenJson.RootElement.GetProperty("access_token").GetString()!;
    }

    private static string CreateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return WebEncoders.Base64UrlEncode(bytes);
    }
}
