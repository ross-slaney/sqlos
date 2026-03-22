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
            builder.UseSetting("SqlOS:AuthMode", "Headless");
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

    private static string CreateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return WebEncoders.Base64UrlEncode(bytes);
    }
}
