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
public sealed class SqlOSExampleEmailOtpIntegrationTests
{
    [TestMethod]
    public async Task ApiEmailOtpLogin_Works_EndToEnd()
    {
        using var factory = CreateOtpFactory(enableHeadlessAuthPage: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var sender = factory.Services.GetRequiredService<TestAuthEmailSender>();

        var email = $"api-otp-{Guid.NewGuid():N}@example.com";
        var organizationId = await CreateOrganizationAsync(client, $"OTP API Org {Guid.NewGuid():N}");
        var userId = await CreateUserAsync(client, email, "OTP API User", "P@ssword123!");
        await CreateMembershipAsync(client, organizationId, userId, "member");

        var startResponse = await client.PostAsJsonAsync("/api/v1/auth/email-otp/start", new
        {
            email
        });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        var challengeToken = startJson.RootElement.GetProperty("challengeToken").GetString();

        var verifyResponse = await client.PostAsJsonAsync("/api/v1/auth/email-otp/verify", new
        {
            challengeToken,
            code = sender.GetLatestCode(email)
        });
        verifyResponse.EnsureSuccessStatusCode();

        var verifyJson = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync());
        var accessToken = verifyJson.RootElement.GetProperty("accessToken").GetString();
        accessToken.Should().NotBeNullOrWhiteSpace();

        var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        sessionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var sessionResponse = await client.SendAsync(sessionRequest);
        sessionResponse.EnsureSuccessStatusCode();

        var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        sessionJson.RootElement.GetProperty("session").GetProperty("authenticationMethod").GetString().Should().Be("email_otp");
        sessionJson.RootElement.GetProperty("token").GetProperty("organizationId").GetString().Should().Be(organizationId);
    }

    [TestMethod]
    public async Task HeadlessEmailOtpLogin_CompletesAuthorizationCodeFlow()
    {
        using var factory = CreateOtpFactory(enableHeadlessAuthPage: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var sender = factory.Services.GetRequiredService<TestAuthEmailSender>();

        var email = $"headless-otp-{Guid.NewGuid():N}@example.com";
        var organizationId = await CreateOrganizationAsync(client, $"Headless OTP Org {Guid.NewGuid():N}");
        var userId = await CreateUserAsync(client, email, "Headless OTP User", "P@ssword123!");
        await CreateMembershipAsync(client, organizationId, userId, "member");

        const string verifier = "headless-email-otp-verifier-123456789";
        var challenge = CreateCodeChallenge(verifier);

        var authorizeResponse = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "example-web",
            ["redirect_uri"] = "http://localhost:3000/auth/callback",
            ["state"] = "headless-email-otp-state",
            ["scope"] = "openid profile email",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        }));

        authorizeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var requestId = QueryHelpers.ParseQuery(authorizeResponse.Headers.Location!.Query)["request"].ToString();
        requestId.Should().NotBeNullOrWhiteSpace();

        var startResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/email-otp/start", new
        {
            requestId,
            email
        });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        var challengeToken = startJson.RootElement.GetProperty("viewModel").GetProperty("challengeToken").GetString();

        var verifyResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/email-otp/verify", new
        {
            requestId,
            challengeToken,
            code = sender.GetLatestCode(email)
        });
        verifyResponse.EnsureSuccessStatusCode();
        var verifyJson = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync());
        verifyJson.RootElement.GetProperty("type").GetString().Should().Be("redirect");

        var redirectUrl = verifyJson.RootElement.GetProperty("redirectUrl").GetString();
        redirectUrl.Should().NotBeNullOrWhiteSpace();
        var tokenResponse = await ExchangeAuthCodeAsync(client, redirectUrl!, verifier);
        var accessToken = tokenResponse.RootElement.GetProperty("access_token").GetString();

        var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        sessionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var sessionResponse = await client.SendAsync(sessionRequest);
        sessionResponse.EnsureSuccessStatusCode();

        var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        sessionJson.RootElement.GetProperty("session").GetProperty("authenticationMethod").GetString().Should().Be("email_otp");
        sessionJson.RootElement.GetProperty("token").GetProperty("organizationId").GetString().Should().Be(organizationId);
    }

    [TestMethod]
    public async Task HeadlessEmailOtpSignup_CompletesAuthorizationCodeFlow_AndRunsSignupHook()
    {
        using var factory = CreateOtpFactory(enableHeadlessAuthPage: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var sender = factory.Services.GetRequiredService<TestAuthEmailSender>();

        var email = $"headless-otp-signup-{Guid.NewGuid():N}@example.com";
        const string verifier = "headless-email-otp-signup-verifier-123456789";
        var challenge = CreateCodeChallenge(verifier);

        var authorizeResponse = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "example-web",
            ["redirect_uri"] = "http://localhost:3000/auth/callback",
            ["state"] = "headless-email-otp-signup-state",
            ["scope"] = "openid profile email",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["view"] = "signup"
        }));

        authorizeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var requestId = QueryHelpers.ParseQuery(authorizeResponse.Headers.Location!.Query)["request"].ToString();
        requestId.Should().NotBeNullOrWhiteSpace();

        var startResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/signup/email-otp/start", new
        {
            requestId,
            displayName = "Otp Signup User",
            email,
            organizationName = "OTP Signup Org",
            customFields = new
            {
                referralSource = "docs",
                firstName = "Otp",
                lastName = "Signup"
            }
        });

        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        startJson.RootElement.GetProperty("type").GetString().Should().Be("view");
        var viewModel = startJson.RootElement.GetProperty("viewModel");
        viewModel.GetProperty("view").GetString().Should().Be("email-otp-signup-verify");
        var challengeToken = viewModel.GetProperty("challengeToken").GetString();
        var signupToken = viewModel.GetProperty("signupToken").GetString();
        challengeToken.Should().NotBeNullOrWhiteSpace();
        signupToken.Should().NotBeNullOrWhiteSpace();

        var verifyResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/signup/email-otp/verify", new
        {
            requestId,
            signupToken,
            challengeToken,
            code = sender.GetLatestCode(email)
        });
        verifyResponse.EnsureSuccessStatusCode();
        var verifyJson = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync());
        verifyJson.RootElement.GetProperty("type").GetString().Should().Be("redirect");

        var redirectUrl = verifyJson.RootElement.GetProperty("redirectUrl").GetString();
        redirectUrl.Should().NotBeNullOrWhiteSpace();
        var tokenResponse = await ExchangeAuthCodeAsync(client, redirectUrl!, verifier);
        var accessToken = tokenResponse.RootElement.GetProperty("access_token").GetString();

        var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        sessionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var sessionResponse = await client.SendAsync(sessionRequest);
        sessionResponse.EnsureSuccessStatusCode();

        var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        sessionJson.RootElement.GetProperty("session").GetProperty("authenticationMethod").GetString().Should().Be("email_otp");

        var profileRequest = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var profileResponse = await client.SendAsync(profileRequest);
        profileResponse.EnsureSuccessStatusCode();

        var profileJson = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync());
        profileJson.RootElement.GetProperty("profile").GetProperty("referralSource").GetString().Should().Be("docs");
        profileJson.RootElement.GetProperty("profile").GetProperty("organizationName").GetString().Should().Be("OTP Signup Org");
        profileJson.RootElement.GetProperty("email").GetString().Should().Be(email);
    }

    [TestMethod]
    public async Task HostedEmailOtpLogin_CompletesAuthorizationCodeFlow()
    {
        using var factory = CreateOtpFactory(enableHeadlessAuthPage: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var sender = factory.Services.GetRequiredService<TestAuthEmailSender>();

        var email = $"hosted-otp-{Guid.NewGuid():N}@example.com";
        var organizationId = await CreateOrganizationAsync(client, $"Hosted OTP Org {Guid.NewGuid():N}");
        var userId = await CreateUserAsync(client, email, "Hosted OTP User", "P@ssword123!");
        await CreateMembershipAsync(client, organizationId, userId, "member");

        const string verifier = "hosted-email-otp-verifier-123456789";
        var challenge = CreateCodeChallenge(verifier);

        var authorizeResponse = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "example-web",
            ["redirect_uri"] = "http://localhost:3000/auth/callback",
            ["state"] = "hosted-email-otp-state",
            ["scope"] = "openid profile email",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        }));

        authorizeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var authorizeHtml = await authorizeResponse.Content.ReadAsStringAsync();
        var requestId = ExtractHiddenInput(authorizeHtml, "requestId");

        var startResponse = await client.PostAsync("/sqlos/auth/login/email-otp/start", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["requestId"] = requestId,
            ["email"] = email
        }));
        startResponse.EnsureSuccessStatusCode();
        var verifyPageHtml = await startResponse.Content.ReadAsStringAsync();
        var challengeToken = ExtractHiddenInput(verifyPageHtml, "challengeToken");

        var verifyResponse = await client.PostAsync("/sqlos/auth/login/email-otp/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["requestId"] = requestId,
            ["email"] = email,
            ["challengeToken"] = challengeToken,
            ["code"] = sender.GetLatestCode(email)
        }));

        verifyResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var tokenResponse = await ExchangeAuthCodeAsync(client, verifyResponse.Headers.Location!.ToString(), verifier);
        var accessToken = tokenResponse.RootElement.GetProperty("access_token").GetString();

        var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        sessionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var sessionResponse = await client.SendAsync(sessionRequest);
        sessionResponse.EnsureSuccessStatusCode();

        var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        sessionJson.RootElement.GetProperty("session").GetProperty("authenticationMethod").GetString().Should().Be("email_otp");
        sessionJson.RootElement.GetProperty("token").GetProperty("organizationId").GetString().Should().Be(organizationId);
    }

    private static WebApplicationFactory<Program> CreateOtpFactory(bool enableHeadlessAuthPage)
        => ExampleApiFixture.CreateFactory(builder =>
        {
            builder.UseSetting("SqlOS:EnableHeadlessAuthPage", enableHeadlessAuthPage ? "true" : "false");
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

    private static async Task<string> CreateOrganizationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/sqlos/admin/auth/api/organizations", new { name });
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateUserAsync(HttpClient client, string email, string displayName, string password)
    {
        var response = await client.PostAsJsonAsync("/sqlos/admin/auth/api/users", new
        {
            displayName,
            email,
            password
        });
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task CreateMembershipAsync(HttpClient client, string organizationId, string userId, string role)
    {
        var response = await client.PostAsJsonAsync("/sqlos/admin/auth/api/memberships", new
        {
            organizationId,
            userId,
            role
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonDocument> ExchangeAuthCodeAsync(HttpClient client, string redirectUrl, string verifier)
    {
        var redirectUri = new Uri(redirectUrl);
        var query = QueryHelpers.ParseQuery(redirectUri.Query);
        var code = query["code"].ToString();
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
        return JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
    }

    private static string ExtractHiddenInput(string html, string fieldName)
    {
        var match = Regex.Match(html, $"name=\\\"{Regex.Escape(fieldName)}\\\" value=\\\"([^\\\"]+)\\\"");
        if (!match.Success)
        {
            throw new InvalidOperationException($"Could not find hidden input '{fieldName}'.");
        }

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string CreateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return WebEncoders.Base64UrlEncode(bytes);
    }
}
