using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Models;
using SqlOS.Extensions;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Mcp;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Real-SQL proof of the one-call single-application shape: <c>AddSqlOS</c> declares
/// <c>Api = "/api"</c> and <c>app.Mcp("/mcp", ...)</c>, application code maps nothing SqlOS-related,
/// and a Codex-shaped CIMD client (portless loopback registration, ephemeral-port authorize)
/// completes authorize → consent → token → <c>POST /mcp</c>.
/// </summary>
[TestClass]
public sealed class SingleApplicationSurfacesIntegrationTests
{
    private const string Origin = HostedAuthorizeTokenFixture.TrustedOrigin;
    private const string ApiAudience = Origin + "/api";
    private const string McpAudience = Origin + "/mcp";
    private const string ClientMetadataUrl = "https://portable.example.test/clients/codex.json";
    private const string RegisteredLoopbackRedirect = "http://127.0.0.1/callback/abc123";
    private const string EphemeralLoopbackRedirect = "http://127.0.0.1:49731/callback/abc123";

    [TestMethod]
    public async Task OneCallHost_MapsSurfacesWithoutManualMapping_AndProtectsBothSurfaces()
    {
        await using var fixture = await CreateFixtureAsync();

        (await fixture.Client.GetAsync("/sqlos/auth/.well-known/oauth-authorization-server")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await fixture.Client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK, "paths outside the surfaces stay public");

        using var api = await fixture.Client.GetAsync("/api/ping");
        api.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        api.Headers.WwwAuthenticate.ToString().Should().Contain("realm=\"PetalPal API\"")
            .And.Contain($"resource_metadata=\"{Origin}/.well-known/oauth-protected-resource\"");

        using var mcp = await fixture.Client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        mcp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        mcp.Headers.WwwAuthenticate.ToString().Should().Contain("realm=\"PetalPal MCP\"")
            .And.Contain($"resource_metadata=\"{Origin}/.well-known/oauth-protected-resource/mcp\"");

        var apiDocument = JsonDocument.Parse(await fixture.Client.GetStringAsync("/.well-known/oauth-protected-resource")).RootElement;
        apiDocument.GetProperty("resource").GetString().Should().Be(ApiAudience);
        var mcpDocument = JsonDocument.Parse(await fixture.Client.GetStringAsync("/.well-known/oauth-protected-resource/mcp")).RootElement;
        mcpDocument.GetProperty("resource").GetString().Should().Be(McpAudience);
        mcpDocument.GetProperty("authorization_servers")[0].GetString().Should().Be($"{Origin}/sqlos/auth");

        var metadata = JsonDocument.Parse(await fixture.Client.GetStringAsync("/sqlos/auth/.well-known/oauth-authorization-server")).RootElement;
        metadata.GetProperty("client_id_metadata_document_supported").GetBoolean().Should().BeTrue();
        metadata.GetProperty("resource_parameter_supported").GetBoolean().Should().BeTrue();
        metadata.TryGetProperty("registration_endpoint", out _).Should().BeFalse("DCR is not enabled by declaring an MCP surface");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FirstPartyBrowserClient_ReceivesApiAudience_AndIsRejectedAtMcp(bool explicitClients)
    {
        await using var fixture = await CreateFixtureAsync(explicitClients);

        var tokens = await AuthorizeFirstPartyAsync(fixture, "openid profile petals.read");
        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
        ReadClaim(accessToken, "aud").Should().Be(ApiAudience);

        using var api = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        api.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var apiResponse = await fixture.Client.SendAsync(api);
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await apiResponse.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("userId").GetString().Should().Be(fixture.UserId);
        body.GetProperty("audience").GetString().Should().Be(ApiAudience);

        using var mcp = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        mcp.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var mcpResponse = await fixture.Client.SendAsync(mcp);
        mcpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        mcpResponse.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CodexShapedCimdClient_CompletesFlowWithEphemeralLoopbackPort_AndCallsMcpTools(bool explicitClients)
    {
        await using var fixture = await CreateFixtureAsync(explicitClients);

        var started = await fixture.StartAuthorizeAsync(
            "openid profile petals.read",
            clientId: ClientMetadataUrl,
            redirectUri: EphemeralLoopbackRedirect,
            extraParameters: new Dictionary<string, string> { ["resource"] = McpAudience });
        var code = await fixture.SubmitPasswordLoginApprovingConsentAsync(started);
        var tokens = await fixture.ExchangeAuthorizationCodeAsync(
            code,
            started.CodeVerifier,
            clientId: ClientMetadataUrl,
            redirectUri: EphemeralLoopbackRedirect,
            resource: McpAudience);
        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
        ReadClaim(accessToken, "aud").Should().Be(McpAudience);

        // The MCP token is bound to the MCP resource: the API rejects it.
        using var api = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        api.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        (await fixture.Client.SendAsync(api)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using var mcpClient = await ConnectMcpAsync(fixture, accessToken);
        var tools = await mcpClient.ListToolsAsync();
        tools.Select(tool => tool.Name).Should().Contain("whoami");

        var result = await mcpClient.CallToolAsync("whoami", new Dictionary<string, object?>());
        result.IsError.Should().NotBe(true);
        var who = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
        who.GetProperty("userId").GetString().Should().Be(fixture.UserId);
        who.GetProperty("clientId").GetString().Should().Be(ClientMetadataUrl);
        who.GetProperty("audience").GetString().Should().Be(McpAudience);

        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var audit = await db.Set<SqlOSAuditEvent>().SingleAsync(x => x.Action == "mcp.tool.called");
        audit.Source.Should().Be("mcp");
        audit.UserId.Should().Be(fixture.UserId);
        audit.ApplicationKey.Should().Be(ClientMetadataUrl);
        audit.TargetsJson.Should().Contain("\"whoami\"");
        audit.MetadataJson.Should().Contain("\"outcome\":\"succeeded\"").And.NotContain(accessToken);
    }

    private static async Task<HostedAuthorizeTokenFixture> CreateFixtureAsync(bool explicitClients = false)
        => await HostedAuthorizeTokenFixture.CreateAsync(
            "SingleAppSurfaces",
            configure: options =>
            {
                void Describe(SqlOS.AuthServer.Configuration.SqlOSApplicationOptions app)
                {
                    app.Origin = Origin;
                    app.AllowedScopes = ["openid", "profile", "email", "offline_access", "petals.read", "petals.write"];
                    app.Api = "/api";
                    app.Mcp("/mcp", mcp => mcp.WithTools<PetalTools>());
                }
                if (explicitClients)
                {
                    options.ConfigureApplication("PetalPal", Describe);
                    options.AuthServer.SeedClient(client =>
                    {
                        client.ClientId = "petalpal";
                        client.Name = "PetalPal";
                        client.Audience = ApiAudience;
                        client.IsFirstParty = true;
                        client.AllowedScopes = ["openid", "profile", "email", "offline_access", "petals.read", "petals.write"];
                        client.RedirectUris = [Origin + "/auth/callback", HostedAuthorizeTokenFixture.RedirectUri];
                    });
                    options.AuthServer.SeedBrowserClient("second-portal", "Second Portal", "https://portal.example/callback");
                }
                else
                {
                    options.UseSingleApplication("PetalPal", app =>
                    {
                        Describe(app);
                        app.RedirectUris.Add(HostedAuthorizeTokenFixture.RedirectUri);
                    });
                }
                options.AuthServer.ClientRegistration.Cimd.TrustedHosts.Add("portable.example.test");
            },
            configureServices: services => services.AddSingleton<IHttpClientFactory>(
                new FakeCimdHttpClientFactory(new Dictionary<string, string>
                {
                    [ClientMetadataUrl] = JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["client_id"] = ClientMetadataUrl,
                        ["client_name"] = "Codex",
                        ["redirect_uris"] = new[] { RegisteredLoopbackRedirect },
                        ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                        ["response_types"] = new[] { "code" },
                        ["token_endpoint_auth_method"] = "none",
                        ["client_uri"] = "https://portable.example.test",
                        ["software_id"] = "codex",
                        ["software_version"] = "2026.9"
                    })
                })),
            configureApp: app =>
            {
                // Application code: map under the declared Api/Mcp prefixes. SqlOS validates the token.
                app.MapGet("/", () => Results.Text("home"));
                app.MapGet("/api/ping", (HttpContext http) =>
                {
                    var token = http.GetSqlOSValidatedToken()!;
                    return Results.Json(new { userId = token.UserId, audience = token.Audience });
                }).RequireAuthorization();
            },
            mapAuthServer: false,
            seedBrowserClient: false);

    private static async Task<JsonDocument> AuthorizeFirstPartyAsync(HostedAuthorizeTokenFixture fixture, string scope)
    {
        var started = await fixture.StartAuthorizeAsync(scope, clientId: "petalpal");
        var code = await fixture.SubmitPasswordLoginApprovingConsentAsync(started);
        return await fixture.ExchangeAuthorizationCodeAsync(code, started.CodeVerifier, clientId: "petalpal");
    }

    private static async Task<McpClient> ConnectMcpAsync(HostedAuthorizeTokenFixture fixture, string accessToken)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(Origin + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {accessToken}" }
            },
            fixture.Client,
            LoggerFactory.Create(_ => { }),
            ownsHttpClient: false);
        return await McpClient.CreateAsync(transport);
    }

    private static string? ReadClaim(string jwt, string claim)
    {
        var payload = jwt.Split('.')[1];
        var json = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(payload));
        var element = JsonDocument.Parse(json).RootElement;
        return element.TryGetProperty(claim, out var value)
            ? value.ValueKind == JsonValueKind.Array ? value[0].GetString() : value.GetString()
            : null;
    }

    private sealed class FakeCimdHttpClientFactory(IReadOnlyDictionary<string, string> responses) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(responses));

        private sealed class Handler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
                return Task.FromResult(responses.TryGetValue(url, out var payload)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            }
        }
    }

    [McpServerToolType]
    public sealed class PetalTools
    {
        [McpServerTool(Name = "whoami"), System.ComponentModel.Description("Describes the connecting SqlOS user.")]
        public static string WhoAmI(ISqlOSMcpUserContext user)
            => JsonSerializer.Serialize(new { userId = user.UserId, clientId = user.ClientId, audience = user.Audience });
    }
}
