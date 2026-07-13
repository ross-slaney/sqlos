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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Email.Interfaces;
using SqlOS.Todo.IntegrationTests.Infrastructure;

namespace SqlOS.Todo.IntegrationTests;

[TestClass]
public sealed class TodoSampleIntegrationTests
{
    private const string HostedClientId = "todo-web";
    private const string HostedRedirectUri = "https://todo.example.test/callback.html";
    private const string TodoResource = "https://todo.example.test/api/todos";

    [TestMethod]
    public async Task HostedAuthStandaloneStatus_ShowsSessionStateInsteadOfLoginForm()
    {
        await using var factory = TodoApiFixture.CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/sqlos/auth/login?status=signed-in");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("You are signed in.");
        html.Should().Contain("href=\"/sqlos/auth/logout\"");
        html.Should().NotContain("action=\"/sqlos/auth/login/identify\"");
    }

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
        var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        createJson.RootElement.GetProperty("resourceId").GetString().Should().StartWith("todo::");

        var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/todos");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var listResponse = await client.SendAsync(listRequest);
        listResponse.EnsureSuccessStatusCode();

        var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listJson.RootElement.GetProperty("resource").GetString().Should().Be(TodoResource);
        listJson.RootElement.GetProperty("items").EnumerateArray()
            .Should().Contain(item =>
                item.GetProperty("title").GetString() == "Ship hosted-first login"
                && item.GetProperty("resourceId").GetString()!.StartsWith("todo::", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AspNetCorePublicClient_PreservesProtectedState_ThroughCodeExchange()
    {
        const string clientId = "example-aspnet";
        const string redirectUri = "http://localhost:5090/signin-sqlos";
        var protectedState = $"CfDJ8-{new string('s', 1024)}";

        await using var factory = TodoApiFixture.CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var authorize = await StartAuthorizationAsync(
            client,
            clientId,
            redirectUri,
            protectedState);
        var signupResponse = await client.PostAsync(
            "/sqlos/auth/signup/submit",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["requestId"] = authorize.RequestId,
                ["displayName"] = "ASP.NET Core User",
                ["email"] = $"aspnet-{Guid.NewGuid():N}@example.com",
                ["password"] = "P@ssword123!",
                ["organizationName"] = "ASP.NET Core Org"
            }));

        signupResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        signupResponse.Headers.Location.Should().NotBeNull();
        var callback = signupResponse.Headers.Location!;
        var callbackQuery = QueryHelpers.ParseQuery(callback.Query);
        callbackQuery["state"].ToString().Should().Be(protectedState);
        var code = callbackQuery["code"].ToString();
        code.Should().NotBeNullOrWhiteSpace();

        var tokens = await ExchangeAuthorizationCodeAsync(
            client,
            code,
            clientId,
            redirectUri,
            authorize.CodeVerifier);

        tokens.ClientId.Should().Be(clientId);
        ReadAudience(tokens.AccessToken).Should().Be(TodoResource);
    }

    [TestMethod]
    public async Task AuthorizationRequest_RejectsStateBeyondStorageContract()
    {
        await using var factory = TodoApiFixture.CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var codeVerifier = CreateCodeVerifier();

        var response = await client.GetAsync(QueryHelpers.AddQueryString(
            "/sqlos/auth/authorize",
            new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = "example-aspnet",
                ["redirect_uri"] = "http://localhost:5090/signin-sqlos",
                ["state"] = new string('s', 2049),
                ["scope"] = "openid profile email offline_access todos.read todos.write",
                ["code_challenge"] = CreateCodeChallenge(codeVerifier),
                ["code_challenge_method"] = "S256",
                ["resource"] = TodoResource
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain(SqlOSPublicAuthErrorMapper.DefaultAuthorizationRequestMessage);
    }

    [TestMethod]
    public async Task HostedEmailOtpSignup_DoesNotIssueTokenUntilCodeIsVerified()
    {
        await using var factory = TodoApiFixture.CreateFactory(builder =>
        {
            builder.UseSetting("TodoSample:EnableEmailOtp", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISqlOSAuthEmailSender>();
                services.RemoveAll<ISqlOSEmailSender>();
                services.AddSingleton<TestAuthEmailSender>();
                services.AddSingleton<ISqlOSAuthEmailSender>(provider => provider.GetRequiredService<TestAuthEmailSender>());
                services.AddSingleton<ISqlOSEmailSender>(provider => provider.GetRequiredService<TestAuthEmailSender>());
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var authorize = await StartAuthorizationAsync(client, HostedClientId, HostedRedirectUri);
        authorize.Html.Should().Contain("/sqlos/auth/signup/email-otp/start");
        authorize.Html.Should().NotContain("name=\"password\"");
        var emailSender = factory.Services.GetRequiredService<TestAuthEmailSender>();
        var email = $"otp-hosted-{Guid.NewGuid():N}@example.com";

        var passwordSignupResponse = await client.PostAsync("/sqlos/auth/signup/submit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["requestId"] = authorize.RequestId,
            ["displayName"] = "Password Bypass User",
            ["email"] = $"password-bypass-{Guid.NewGuid():N}@example.com",
            ["password"] = "P@ssword123!",
            ["organizationName"] = "Password Bypass Org"
        }));
        passwordSignupResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        passwordSignupResponse.Headers.Location.Should().BeNull("password signup is disabled in the Todo email OTP demo");

        var startResponse = await client.PostAsync("/sqlos/auth/signup/email-otp/start", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["requestId"] = authorize.RequestId,
            ["displayName"] = "OTP Hosted User",
            ["email"] = email,
            ["organizationName"] = "OTP Hosted Org"
        }));

        startResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        startResponse.Headers.Location.Should().BeNull("email OTP signup must render code verification before issuing an authorization code");

        var verifyHtml = await startResponse.Content.ReadAsStringAsync();
        verifyHtml.Should().Contain("Verify and create account");
        var challengeToken = ExtractHiddenInput(verifyHtml, "challengeToken");
        var signupToken = ExtractHiddenInput(verifyHtml, "signupToken");
        var code = emailSender.GetLatestCode(email);

        var verifyResponse = await client.PostAsync("/sqlos/auth/signup/email-otp/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["requestId"] = authorize.RequestId,
            ["email"] = email,
            ["challengeToken"] = challengeToken,
            ["signupToken"] = signupToken,
            ["code"] = code
        }));

        verifyResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = verifyResponse.Headers.Location!.ToString();
        var authCode = QueryHelpers.ParseQuery(new Uri(location).Query)["code"].ToString();
        authCode.Should().NotBeNullOrWhiteSpace();

        var tokens = await ExchangeAuthorizationCodeAsync(client, authCode, HostedClientId, HostedRedirectUri, authorize.CodeVerifier);
        ReadAudience(tokens.AccessToken).Should().Be(TodoResource);

        var createResponse = await CreateTodoAsync(client, tokens.AccessToken, "Ship passwordless Todo");
        var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        createJson.RootElement.GetProperty("resourceId").GetString().Should().StartWith("todo::");
    }

    [TestMethod]
    public async Task HostedPhoneOtpSignup_DoesNotIssueTokenUntilCodeIsVerified()
    {
        await using var factory = TodoApiFixture.CreateFactory(builder =>
        {
            builder.UseSetting("TodoSample:EnablePhoneOtp", "true");
            builder.UseSetting("SqlOS:PhoneOtp:TwilioAccountSid", "AC00000000000000000000000000000000");
            builder.UseSetting("SqlOS:PhoneOtp:TwilioAuthToken", "test-token");
            builder.UseSetting("SqlOS:PhoneOtp:TwilioVerifyServiceSid", "VA00000000000000000000000000000000");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISqlOSOtpDeliveryChannel>();
                services.AddSingleton<TestOtpDeliveryChannel>();
                services.AddSingleton<ISqlOSOtpDeliveryChannel>(provider => provider.GetRequiredService<TestOtpDeliveryChannel>());
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var authorize = await StartAuthorizationAsync(client, HostedClientId, HostedRedirectUri);
        authorize.Html.Should().Contain("/sqlos/auth/signup/phone-otp/start");
        authorize.Html.Should().NotContain("name=\"password\"");
        var delivery = factory.Services.GetRequiredService<TestOtpDeliveryChannel>();
        var phoneNumber = "+12025550177";

        var startResponse = await client.PostAsync("/sqlos/auth/signup/phone-otp/start", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["requestId"] = authorize.RequestId,
            ["displayName"] = "SMS Hosted User",
            ["phoneNumber"] = phoneNumber,
            ["organizationName"] = "SMS Hosted Org"
        }));

        startResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        startResponse.Headers.Location.Should().BeNull("phone OTP signup must render code verification before issuing an authorization code");
        delivery.StartRequests.Should().ContainSingle(x => x.PhoneNumber == phoneNumber);

        var verifyHtml = await startResponse.Content.ReadAsStringAsync();
        verifyHtml.Should().Contain("Verify and create account");
        var challengeToken = ExtractHiddenInput(verifyHtml, "challengeToken");
        var signupToken = ExtractHiddenInput(verifyHtml, "signupToken");

        var verifyResponse = await client.PostAsync("/sqlos/auth/signup/phone-otp/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["requestId"] = authorize.RequestId,
            ["phoneNumber"] = phoneNumber,
            ["challengeToken"] = challengeToken,
            ["signupToken"] = signupToken,
            ["code"] = TestOtpDeliveryChannel.ApprovedCode
        }));

        verifyResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = verifyResponse.Headers.Location!.ToString();
        var authCode = QueryHelpers.ParseQuery(new Uri(location).Query)["code"].ToString();
        authCode.Should().NotBeNullOrWhiteSpace();

        var tokens = await ExchangeAuthorizationCodeAsync(client, authCode, HostedClientId, HostedRedirectUri, authorize.CodeVerifier);
        ReadAudience(tokens.AccessToken).Should().Be(TodoResource);

        var createResponse = await CreateTodoAsync(client, tokens.AccessToken, "Ship SMS Todo");
        var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        createJson.RootElement.GetProperty("resourceId").GetString().Should().StartWith("todo::");
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
    public async Task HeadlessFollowOn_EstablishesSession_ForPromptNoneAuthorize()
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

        var email = $"silent-todo-{Guid.NewGuid():N}@example.com";
        var signupResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/signup", new
        {
            requestId = authorize.RequestId,
            displayName = "Silent Todo User",
            email,
            password = "P@ssword123!",
            organizationName = "Todo Org"
        });
        signupResponse.EnsureSuccessStatusCode();

        var promptNoneVerifier = CreateCodeVerifier();
        var promptNoneChallenge = CreateCodeChallenge(promptNoneVerifier);
        var silentAuthorize = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = HostedClientId,
            ["redirect_uri"] = HostedRedirectUri,
            ["state"] = $"silent-{Guid.NewGuid():N}",
            ["scope"] = "openid profile email offline_access todos.read todos.write",
            ["code_challenge"] = promptNoneChallenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = TodoResource,
            ["prompt"] = "none",
            ["login_hint"] = email
        }));

        silentAuthorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        silentAuthorize.Headers.Location.Should().NotBeNull();
        silentAuthorize.Headers.Location!.ToString().Should().StartWith($"{HostedRedirectUri}?");
        var query = QueryHelpers.ParseQuery(silentAuthorize.Headers.Location.Query);
        query.ContainsKey("error").Should().BeFalse();
        query["code"].ToString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task ProtectedResourceMetadata_AndChallengeBehavior_AreExposed()
    {
        var configResponse = await TodoApiFixture.Client.GetAsync("/sample/config");
        configResponse.EnsureSuccessStatusCode();
        var configJson = JsonDocument.Parse(await configResponse.Content.ReadAsStringAsync());
        configJson.RootElement.GetProperty("headlessEnabled").GetBoolean().Should().BeFalse();
        configJson.RootElement.GetProperty("dcrEnabled").GetBoolean().Should().BeFalse();
        configJson.RootElement.GetProperty("localClient").GetProperty("redirectUri").GetString()
            .Should().Be("http://localhost:3100/oauth/callback");
        configJson.RootElement.GetProperty("emcyClient").GetProperty("clientId").GetString()
            .Should().Be("todo-mcp-local");
        configJson.RootElement.GetProperty("emcyClient").GetProperty("redirectUri").GetString()
            .Should().Be("http://localhost:5150/api/v1/hosted-mcp/todo-local/oauth/callback");

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
        const string redirectUri = "http://localhost:3100/oauth/callback";

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
    public async Task FgaDashboard_ShowsSeededRoles_AndTodoHierarchy()
    {
        await using var factory = TodoApiFixture.CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var initialStats = await GetJsonAsync(client, "/sqlos/admin/fga/api/stats");
        var initialResources = initialStats.RootElement.GetProperty("resources").GetInt32();
        var initialSubjects = initialStats.RootElement.GetProperty("subjects").GetInt32();
        var initialUsers = initialStats.RootElement.GetProperty("users").GetInt32();
        var initialGrants = initialStats.RootElement.GetProperty("grants").GetInt32();
        initialStats.RootElement.GetProperty("roles").GetInt32().Should().Be(1);
        initialStats.RootElement.GetProperty("permissions").GetInt32().Should().Be(3);

        var signup = await ExecuteHostedSignupAsync(client, HostedClientId, HostedRedirectUri);
        var tokens = await ExchangeAuthorizationCodeAsync(client, signup.Code, HostedClientId, HostedRedirectUri, signup.CodeVerifier);

        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var meResponse = await client.SendAsync(meRequest);
        meResponse.EnsureSuccessStatusCode();
        var meJson = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync());
        var subjectId = meJson.RootElement.GetProperty("subjectId").GetString();
        var tenantResourceId = meJson.RootElement.GetProperty("tenantResourceId").GetString();

        tenantResourceId.Should().Be($"tenant::{subjectId}");

        var createResponse = await CreateTodoAsync(client, tokens.AccessToken, "Trace FGA hierarchy");
        var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var todoId = createJson.RootElement.GetProperty("id").GetGuid();
        var todoResourceId = createJson.RootElement.GetProperty("resourceId").GetString();

        var syncedStats = await GetJsonAsync(client, "/sqlos/admin/fga/api/stats");
        syncedStats.RootElement.GetProperty("resources").GetInt32().Should().Be(initialResources + 2);
        syncedStats.RootElement.GetProperty("subjects").GetInt32().Should().Be(initialSubjects + 1);
        syncedStats.RootElement.GetProperty("users").GetInt32().Should().Be(initialUsers + 1);
        syncedStats.RootElement.GetProperty("grants").GetInt32().Should().Be(initialGrants + 1);

        var tree = await GetJsonAsync(client, "/sqlos/admin/fga/api/resources/tree?maxDepth=3");
        var nodeIds = tree.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("id").GetString())
            .ToArray();

        nodeIds.Should().Contain("root");
        nodeIds.Should().Contain(tenantResourceId);
        nodeIds.Should().Contain(todoResourceId);
        todoResourceId.Should().Be($"todo::{todoId:D}");
    }

    [TestMethod]
    public async Task TodoEndpoints_UseFgaFiltering_AndDenyCrossTenantMutations()
    {
        await using var factory = TodoApiFixture.CreateFactory();
        using var clientA = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var clientB = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var signupA = await ExecuteHostedSignupAsync(clientA, HostedClientId, HostedRedirectUri);
        var tokensA = await ExchangeAuthorizationCodeAsync(clientA, signupA.Code, HostedClientId, HostedRedirectUri, signupA.CodeVerifier);
        var signupB = await ExecuteHostedSignupAsync(clientB, HostedClientId, HostedRedirectUri);
        var tokensB = await ExchangeAuthorizationCodeAsync(clientB, signupB.Code, HostedClientId, HostedRedirectUri, signupB.CodeVerifier);

        var createA = await CreateTodoAsync(clientA, tokensA.AccessToken, "User A todo");
        var createB = await CreateTodoAsync(clientB, tokensB.AccessToken, "User B todo");
        var todoA = JsonDocument.Parse(await createA.Content.ReadAsStringAsync()).RootElement;
        var todoB = JsonDocument.Parse(await createB.Content.ReadAsStringAsync()).RootElement;
        var todoAId = todoA.GetProperty("id").GetGuid();
        var todoBId = todoB.GetProperty("id").GetGuid();

        var listA = await ListTodosAsync(clientA, tokensA.AccessToken);
        var listB = await ListTodosAsync(clientB, tokensB.AccessToken);

        listA.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("title").GetString())
            .Should().BeEquivalentTo(["User A todo"]);
        listB.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("title").GetString())
            .Should().BeEquivalentTo(["User B todo"]);

        var toggleForbidden = new HttpRequestMessage(HttpMethod.Post, $"/api/todos/{todoAId}/toggle");
        toggleForbidden.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokensB.AccessToken);
        var toggleResponse = await clientB.SendAsync(toggleForbidden);
        toggleResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var deleteForbidden = new HttpRequestMessage(HttpMethod.Delete, $"/api/todos/{todoAId}");
        deleteForbidden.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokensB.AccessToken);
        var deleteResponse = await clientB.SendAsync(deleteForbidden);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var toggleAllowed = new HttpRequestMessage(HttpMethod.Post, $"/api/todos/{todoBId}/toggle");
        toggleAllowed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokensB.AccessToken);
        var toggleAllowedResponse = await clientB.SendAsync(toggleAllowed);
        toggleAllowedResponse.EnsureSuccessStatusCode();
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

    private static async Task<AuthorizationStartResult> StartAuthorizationAsync(
        HttpClient client,
        string clientId,
        string redirectUri,
        string? state = null)
    {
        var codeVerifier = CreateCodeVerifier();
        var challenge = CreateCodeChallenge(codeVerifier);

        var authorizeResponse = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state ?? $"state-{Guid.NewGuid():N}",
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
            return new AuthorizationStartResult(requestId, codeVerifier, redirectUriResult, null);
        }

        authorizeResponse.EnsureSuccessStatusCode();
        var html = await authorizeResponse.Content.ReadAsStringAsync();
        var requestIdMatch = Regex.Match(html, "name=\"requestId\" value=\"([^\"]+)\"");
        requestIdMatch.Success.Should().BeTrue();
        return new AuthorizationStartResult(requestIdMatch.Groups[1].Value, codeVerifier, null, html);
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

    private static async Task<HttpResponseMessage> CreateTodoAsync(HttpClient client, string accessToken, string title)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/todos")
        {
            Content = JsonContent.Create(new { title })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<JsonDocument> ListTodosAsync(HttpClient client, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/todos");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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

    private static string ExtractHiddenInput(string html, string fieldName)
    {
        var match = Regex.Match(
            html,
            $"name=\"{Regex.Escape(fieldName)}\" value=\"([^\"]*)\"",
            RegexOptions.CultureInvariant);
        match.Success.Should().BeTrue($"hidden input '{fieldName}' should be rendered");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private sealed record AuthorizationStartResult(string RequestId, string CodeVerifier, Uri? HeadlessRedirect, string? Html);
    private sealed record HostedSignupResult(string Code, string CodeVerifier);
    private sealed record TokenResult(string AccessToken, string RefreshToken, string ClientId);

    private sealed class TestOtpDeliveryChannel : ISqlOSOtpDeliveryChannel
    {
        public const string ApprovedCode = "123456";
        public List<(string PhoneNumber, SqlOSOtpDeliveryContext Context)> StartRequests { get; } = [];
        public List<(string PhoneNumber, string Code, SqlOSOtpDeliveryContext Context)> CheckRequests { get; } = [];

        public Task<SqlOSOtpDeliveryStartResult> StartAsync(
            string e164PhoneNumber,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
        {
            StartRequests.Add((e164PhoneNumber, context));
            return Task.FromResult(new SqlOSOtpDeliveryStartResult(true, "test_verify", $"ve-{StartRequests.Count}", "pending"));
        }

        public Task<SqlOSOtpDeliveryCheckResult> CheckAsync(
            string e164PhoneNumber,
            string code,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
        {
            CheckRequests.Add((e164PhoneNumber, code, context));
            var approved = string.Equals(code, ApprovedCode, StringComparison.Ordinal);
            return Task.FromResult(new SqlOSOtpDeliveryCheckResult(
                approved,
                "test_verify",
                $"ve-{CheckRequests.Count}",
                approved ? "approved" : "denied",
                approved ? null : "bad_code"));
        }
    }
}
