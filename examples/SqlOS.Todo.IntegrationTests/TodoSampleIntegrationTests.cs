using System.IdentityModel.Tokens.Jwt;
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Todo.IntegrationTests.Infrastructure;

namespace SqlOS.Todo.IntegrationTests;

[TestClass]
public sealed class TodoSampleIntegrationTests
{
    private const string HostedClientId = "todo-web";
    private const string HostedRedirectUri = "https://todo.example.test/callback.html";
    private const string TodoResource = "https://todo.example.test/api/todos";

    [TestMethod]
    public async Task HostedSignup_IssuesResourceBoundToken_AndReadsTodos()
    {
        await using var factory = TodoApiFixture.CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var signup = await ExecuteHostedSignupAsync(client, HostedClientId, HostedRedirectUri);
        var tokens = await ExchangeAuthorizationCodeAsync(client, signup.Code, HostedClientId, HostedRedirectUri, signup.CodeVerifier);

        ReadAudience(tokens.AccessToken).Should().Be(TodoResource);

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/todos")
        {
            Content = JsonContent.Create(new { title = "Ship hosted-first login" })
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var createResponse = await client.SendAsync(createRequest);
        createResponse.EnsureSuccessStatusCode();

        var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/todos");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var listResponse = await client.SendAsync(listRequest);
        listResponse.EnsureSuccessStatusCode();

        var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listJson.RootElement.GetProperty("resource").GetString().Should().Be(TodoResource);
        listJson.RootElement.GetProperty("items").EnumerateArray()
            .Should().Contain(item => item.GetProperty("title").GetString() == "Ship hosted-first login");
    }

    [TestMethod]
    public async Task HeadlessFollowOn_CanCreateUser_AndUseSameTodoResource()
    {
        await using var factory = TodoApiFixture.CreateFactory(builder =>
        {
            builder.UseSetting("TodoSample:EnableHeadless", "true");
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var authorize = await StartAuthorizationAsync(client, HostedClientId, HostedRedirectUri);
        authorize.HeadlessRedirect.Should().NotBeNull();
        authorize.HeadlessRedirect!.AbsolutePath.Should().Be("/headless.html");

        var headlessResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/signup", new
        {
            requestId = authorize.RequestId,
            displayName = "Headless User",
            email = $"headless-{Guid.NewGuid():N}@example.com",
            password = "P@ssword123!",
            organizationName = "Todo Org"
        });
        headlessResponse.EnsureSuccessStatusCode();
        var headlessJson = JsonDocument.Parse(await headlessResponse.Content.ReadAsStringAsync());
        headlessJson.RootElement.GetProperty("type").GetString().Should().Be("redirect");
        var code = QueryHelpers.ParseQuery(new Uri(headlessJson.RootElement.GetProperty("redirectUrl").GetString()!).Query)["code"].ToString();

        var tokens = await ExchangeAuthorizationCodeAsync(client, code, HostedClientId, HostedRedirectUri, authorize.CodeVerifier);
        ReadAudience(tokens.AccessToken).Should().Be(TodoResource);

        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var meResponse = await client.SendAsync(meRequest);
        meResponse.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task ProtectedResourceMetadata_AndChallengeBehavior_AreExposed()
    {
        var configResponse = await TodoApiFixture.Client.GetAsync("/sample/config");
        configResponse.EnsureSuccessStatusCode();
        var configJson = JsonDocument.Parse(await configResponse.Content.ReadAsStringAsync());
        configJson.RootElement.GetProperty("headlessEnabled").GetBoolean().Should().BeFalse();
        configJson.RootElement.GetProperty("dcrEnabled").GetBoolean().Should().BeFalse();

        var metadataResponse = await TodoApiFixture.Client.GetAsync("/.well-known/oauth-protected-resource");
        metadataResponse.EnsureSuccessStatusCode();
        var metadataJson = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        metadataJson.RootElement.GetProperty("resource").GetString().Should().Be(TodoResource);
        metadataJson.RootElement.GetProperty("authorization_servers")[0].GetString().Should().Be("https://todo.example.test/sqlos/auth");

        var challengeResponse = await TodoApiFixture.Client.GetAsync("/api/todos");
        challengeResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        challengeResponse.Headers.WwwAuthenticate.ToString().Should().Contain("resource_metadata");
    }

    [TestMethod]
    public async Task LocalPreregisteredClient_CanExchangeAuthorizationCode()
    {
        const string redirectUri = "http://localhost:8787/oauth/callback";

        await using var factory = TodoApiFixture.CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var signup = await ExecuteHostedSignupAsync(client, "todo-local", redirectUri);
        var tokens = await ExchangeAuthorizationCodeAsync(
            client,
            signup.Code,
            "todo-local",
            redirectUri,
            signup.CodeVerifier);

        ReadAudience(tokens.AccessToken).Should().Be(TodoResource);
        tokens.ClientId.Should().Be("todo-local");
    }

    [TestMethod]
    public async Task CimdClient_CanCompleteAuthorizationFlow_WhenMetadataDocumentIsTrusted()
    {
        const string clientMetadataUrl = "https://portable.todo.test/clients/fake.json";
        const string redirectUri = "https://portable.todo.test/callback";

        await using var factory = TodoApiFixture.CreateFactory(builder =>
        {
            builder.UseSetting("TodoSample:EnableHeadless", "true");
            builder.UseSetting("TodoSample:CimdTrustedHosts:0", "portable.todo.test");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IHttpClientFactory>(new FakeTodoCimdHttpClientFactory(new Dictionary<string, string>
                {
                    [clientMetadataUrl] = JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["client_id"] = clientMetadataUrl,
                        ["client_name"] = "Portable Todo Client",
                        ["redirect_uris"] = new[] { redirectUri },
                        ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                        ["response_types"] = new[] { "code" },
                        ["token_endpoint_auth_method"] = "none",
                        ["client_uri"] = "https://portable.todo.test",
                        ["software_id"] = "portable.todo",
                        ["software_version"] = "2026.1"
                    })
                }));
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var authorize = await StartAuthorizationAsync(client, clientMetadataUrl, redirectUri);
        var headlessResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/signup", new
        {
            requestId = authorize.RequestId,
            displayName = "Portable User",
            email = $"portable-{Guid.NewGuid():N}@example.com",
            password = "P@ssword123!",
            organizationName = "Portable Org"
        });
        headlessResponse.EnsureSuccessStatusCode();
        var headlessJson = JsonDocument.Parse(await headlessResponse.Content.ReadAsStringAsync());
        var code = QueryHelpers.ParseQuery(new Uri(headlessJson.RootElement.GetProperty("redirectUrl").GetString()!).Query)["code"].ToString();

        var tokens = await ExchangeAuthorizationCodeAsync(client, code, clientMetadataUrl, redirectUri, authorize.CodeVerifier);
        ReadAudience(tokens.AccessToken).Should().Be(TodoResource);
    }

    [TestMethod]
    public async Task DcrClient_CanRegister_AndUseAuthorizationCodeFlow_WhenEnabled()
    {
        const string redirectUri = "https://dcr.todo.test/callback";

        await using var factory = TodoApiFixture.CreateFactory(builder =>
        {
            builder.UseSetting("TodoSample:EnableHeadless", "true");
            builder.UseSetting("TodoSample:EnableDcr", "true");
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var registerResponse = await client.PostAsJsonAsync("/sqlos/auth/register", new Dictionary<string, object?>
        {
            ["client_name"] = "DCR Todo Client",
            ["redirect_uris"] = new[] { redirectUri },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
            ["software_id"] = "dcr.todo",
            ["software_version"] = "2026.1"
        });
        registerResponse.EnsureSuccessStatusCode();
        var registerJson = JsonDocument.Parse(await registerResponse.Content.ReadAsStringAsync());
        var clientId = registerJson.RootElement.GetProperty("client_id").GetString();
        clientId.Should().NotBeNullOrWhiteSpace();

        var authorize = await StartAuthorizationAsync(client, clientId!, redirectUri);
        var headlessResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/signup", new
        {
            requestId = authorize.RequestId,
            displayName = "DCR User",
            email = $"dcr-{Guid.NewGuid():N}@example.com",
            password = "P@ssword123!",
            organizationName = "DCR Org"
        });
        headlessResponse.EnsureSuccessStatusCode();
        var headlessJson = JsonDocument.Parse(await headlessResponse.Content.ReadAsStringAsync());
        var code = QueryHelpers.ParseQuery(new Uri(headlessJson.RootElement.GetProperty("redirectUrl").GetString()!).Query)["code"].ToString();

        var tokens = await ExchangeAuthorizationCodeAsync(client, code, clientId!, redirectUri, authorize.CodeVerifier);
        ReadAudience(tokens.AccessToken).Should().Be(TodoResource);
    }

    private static async Task<AuthorizationStartResult> StartAuthorizationAsync(HttpClient client, string clientId, string redirectUri)
    {
        var codeVerifier = CreateCodeVerifier();
        var challenge = CreateCodeChallenge(codeVerifier);

        var authorizeResponse = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = $"state-{Guid.NewGuid():N}",
            ["scope"] = "openid profile email offline_access todos.read todos.write",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = TodoResource,
            ["view"] = "signup"
        }));

        if (authorizeResponse.StatusCode == HttpStatusCode.Redirect)
        {
            var redirectUriResult = authorizeResponse.Headers.Location!;
            var requestId = QueryHelpers.ParseQuery(redirectUriResult.Query)["request"].ToString();
            return new AuthorizationStartResult(requestId, codeVerifier, redirectUriResult);
        }

        authorizeResponse.EnsureSuccessStatusCode();
        var html = await authorizeResponse.Content.ReadAsStringAsync();
        var requestIdMatch = Regex.Match(html, "name=\"requestId\" value=\"([^\"]+)\"");
        requestIdMatch.Success.Should().BeTrue();
        return new AuthorizationStartResult(requestIdMatch.Groups[1].Value, codeVerifier, null);
    }

    private static async Task<HostedSignupResult> ExecuteHostedSignupAsync(HttpClient client, string clientId, string redirectUri)
    {
        var authorize = await StartAuthorizationAsync(client, clientId, redirectUri);

        var signupResponse = await client.PostAsync("/sqlos/auth/signup/submit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["requestId"] = authorize.RequestId,
            ["displayName"] = "Hosted User",
            ["email"] = $"hosted-{Guid.NewGuid():N}@example.com",
            ["password"] = "P@ssword123!",
            ["organizationName"] = "Hosted Org"
        }));

        signupResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = signupResponse.Headers.Location!.ToString();
        var code = QueryHelpers.ParseQuery(new Uri(location).Query)["code"].ToString();
        code.Should().NotBeNullOrWhiteSpace();

        return new HostedSignupResult(code, authorize.CodeVerifier);
    }

    private static async Task<TokenResult> ExchangeAuthorizationCodeAsync(
        HttpClient client,
        string code,
        string clientId,
        string redirectUri,
        string codeVerifier)
    {
        var tokenResponse = await client.PostAsync("/sqlos/auth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["resource"] = TodoResource
        }));
        tokenResponse.EnsureSuccessStatusCode();
        var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());

        return new TokenResult(
            tokenJson.RootElement.GetProperty("access_token").GetString()!,
            tokenJson.RootElement.GetProperty("refresh_token").GetString()!,
            clientId);
    }

    private static string ReadAudience(string accessToken)
    {
        var handler = new JwtSecurityTokenHandler();
        return handler.ReadJwtToken(accessToken).Audiences.Single();
    }

    private static string CreateCodeVerifier()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');

    private static string CreateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return WebEncoders.Base64UrlEncode(bytes);
    }

    private sealed record AuthorizationStartResult(string RequestId, string CodeVerifier, Uri? HeadlessRedirect);
    private sealed record HostedSignupResult(string Code, string CodeVerifier);
    private sealed record TokenResult(string AccessToken, string RefreshToken, string ClientId);
}
