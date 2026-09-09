using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using SqlOS.AuthServer.Authentication;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Extensions;
using SqlOS.Configuration;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// Proves the settings-plus-SDK shape: <c>app.Mcp = "/mcp"</c> is a SqlOS resource id
/// (audience, PRM, CIMD, resource indicators, scheme/policy <c>SqlOS.Mcp</c>). The host
/// registers Microsoft's MCP SDK and locks <c>MapMcp</c> with that policy. SqlOS.dll
/// takes no MCP SDK dependency.
/// </summary>
[TestClass]
public sealed class SqlOSMcpHostingTests
{
    private const string McpAudience = SingleApplicationTestHost.Origin + "/mcp";
    private const string ApiAudience = SingleApplicationTestHost.Origin + "/api";

    [TestMethod]
    public void Mcp_SetsSurfaceAndEnablesPortableClientSettings()
    {
        var options = new SqlOSAuthServerOptions();
        options.UseSingleApplication("Todo", app =>
        {
            app.Origin = "https://todo.example.com";
            app.Mcp = "/mcp";
        });

        options.SingleApplication!.Mcp.Should().Be("/mcp");
        options.SingleApplication.HostExtensions.Should().BeEmpty();
        options.ClientRegistration.Cimd.Enabled.Should().BeTrue();
        options.ResourceIndicators.Enabled.Should().BeTrue();
    }

    [TestMethod]
    public async Task McpSurface_WithoutToken_Returns401WithMcpResourceMetadataChallenge()
    {
        await using var host = await StartHostAsync();

        var response = await host.Client.PostAsync("/mcp", JsonRpc("tools/list"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.Should().Contain("realm=\"Todo MCP\"");
        challenge.Should().Contain($"resource_metadata=\"{SingleApplicationTestHost.Origin}/.well-known/oauth-protected-resource/mcp\"");
    }

    [TestMethod]
    public async Task McpSurface_WithApiAudienceToken_IsRejected()
    {
        await using var host = await StartHostAsync();
        var apiToken = await host.MintAccessTokenAsync(ApiAudience);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonRpc("tools/list") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
    }

    [TestMethod]
    public async Task McpSurface_WithMcpToken_ListsToolsAndCallsToolAsTheConnectingUser()
    {
        await using var host = await StartHostAsync();
        var token = await host.MintAccessTokenAsync(McpAudience, scope: "openid todos.read");
        await using var client = await ConnectAsync(host, token);

        var tools = await client.ListToolsAsync();
        tools.Select(tool => tool.Name).Should().BeEquivalentTo("echo", "whoami");

        var result = await client.CallToolAsync("whoami", new Dictionary<string, object?>());

        result.IsError.Should().NotBe(true);
        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
        var who = JsonDocument.Parse(text).RootElement;
        who.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        who.GetProperty("audience").GetString().Should().Be(McpAudience);
        who.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString()).Should().BeEquivalentTo("openid", "todos.read");
        who.GetProperty("userId").GetString().Should().NotBeNullOrWhiteSpace();
        who.GetProperty("clientId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task McpHost_ServesProtectedResourceDocumentAndAdvertisesCimd()
    {
        await using var host = await StartHostAsync();

        var document = JsonDocument.Parse(await host.Client.GetStringAsync("/.well-known/oauth-protected-resource/mcp")).RootElement;
        document.GetProperty("resource").GetString().Should().Be(McpAudience);
        document.GetProperty("authorization_servers").EnumerateArray().Select(x => x.GetString())
            .Should().ContainSingle().Which.Should().Be(host.AuthOptions.Issuer.TrimEnd('/'));

        var metadata = JsonDocument.Parse(await host.Client.GetStringAsync("/sqlos/auth/.well-known/oauth-authorization-server")).RootElement;
        metadata.GetProperty("client_id_metadata_document_supported").GetBoolean().Should().BeTrue();
        metadata.GetProperty("resource_parameter_supported").GetBoolean().Should().BeTrue();
        metadata.TryGetProperty("registration_endpoint", out _).Should().BeFalse("DCR is not enabled by declaring an MCP surface");
    }

    [TestMethod]
    public async Task McpHost_CoreAssemblyHasNoModelContextProtocolReference()
    {
        var references = typeof(SqlOSOptions).Assembly.GetReferencedAssemblies().Select(x => x.Name);
        references.Should().NotContain(name => name!.StartsWith("ModelContextProtocol", StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    private static Task<SingleApplicationTestHost> StartHostAsync()
        => SingleApplicationTestHost.StartAsync(
            options =>
            {
                options.AuthServer.UseSingleApplication("Todo", app =>
                {
                    app.Origin = SingleApplicationTestHost.Origin;
                    app.Api = "/api";
                    app.Mcp = "/mcp";
                });
            },
            configureServices: services =>
            {
                services.AddHttpContextAccessor();
                services.AddMcpServer()
                    .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
                    .WithTools<EchoTools>();
            },
            configureApp: app => app.MapMcp("/mcp").RequireAuthorization(SqlOSJwtDefaults.McpPolicy));

    private static async Task<McpClient> ConnectAsync(SingleApplicationTestHost host, string token)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(SingleApplicationTestHost.Origin + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" }
            },
            host.Client,
            LoggerFactory.Create(_ => { }),
            ownsHttpClient: false);
        return await McpClient.CreateAsync(transport);
    }

    private static StringContent JsonRpc(string method)
        => new(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = new { } }),
            new MediaTypeHeaderValue("application/json"));

    [McpServerToolType]
    public sealed class EchoTools
    {
        [McpServerTool(Name = "echo"), System.ComponentModel.Description("Echoes the message.")]
        public static string Echo(string message)
            => message == "fail" ? throw new McpException("echo refused") : message;

        [McpServerTool(Name = "whoami"), System.ComponentModel.Description("Describes the connecting SqlOS user.")]
        public static string WhoAmI(IHttpContextAccessor http)
        {
            var token = http.HttpContext?.GetSqlOSValidatedToken();
            var scopes = string.IsNullOrWhiteSpace(token?.Scope)
                ? Array.Empty<string>()
                : token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return JsonSerializer.Serialize(new
            {
                authenticated = token != null,
                userId = token?.UserId,
                clientId = token?.ClientId,
                audience = token?.Audience,
                scopes
            });
        }
    }
}
