using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Example.IntegrationTests.Infrastructure;

namespace SqlOS.Example.IntegrationTests;

[TestClass]
public sealed class SqlOSExampleHeadlessAuthIntegrationTests
{
    [TestMethod]
    public async Task HeadlessSignup_PersistsReferralSource_InExampleProfile()
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

        var email = $"headless-{Guid.NewGuid():N}@example.com";
        const string password = "P@ssword123!";
        const string verifier = "headless-test-verifier-123456789";
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

        var signupResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/signup", new
        {
            requestId,
            displayName = "Taylor Example",
            email,
            password,
            organizationName = "Northwind Retail",
            customFields = new
            {
                referralSource = "docs",
                firstName = "Taylor",
                lastName = "Example"
            }
        });

        signupResponse.EnsureSuccessStatusCode();
        var signupJson = JsonDocument.Parse(await signupResponse.Content.ReadAsStringAsync());
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
        var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString();
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

        var email = $"silent-{Guid.NewGuid():N}@example.com";
        const string firstVerifier = "headless-session-verifier-123456789";
        var firstChallenge = CreateCodeChallenge(firstVerifier);

        var authorizeResponse = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "example-web",
            ["redirect_uri"] = "http://localhost:3000/auth/callback",
            ["state"] = "headless-state",
            ["scope"] = "openid profile email",
            ["code_challenge"] = firstChallenge,
            ["code_challenge_method"] = "S256",
            ["view"] = "signup"
        }));

        authorizeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var handoffQuery = QueryHelpers.ParseQuery(authorizeResponse.Headers.Location!.Query);
        var requestId = handoffQuery["request"].ToString();
        requestId.Should().NotBeNullOrWhiteSpace();

        var signupResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/signup", new
        {
            requestId,
            displayName = "Taylor Silent",
            email,
            password = "P@ssword123!",
            organizationName = "Northwind Retail",
            customFields = new
            {
                referralSource = "docs",
                firstName = "Taylor",
                lastName = "Silent"
            }
        });

        signupResponse.EnsureSuccessStatusCode();
        var signupJson = JsonDocument.Parse(await signupResponse.Content.ReadAsStringAsync());
        signupJson.RootElement.GetProperty("type").GetString().Should().Be("redirect");

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

    private static string CreateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return WebEncoders.Base64UrlEncode(bytes);
    }
}
