using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.OneCall.Api;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Runs the real Notes host: one <c>AddSqlOS</c> call, plain <c>/api</c> handlers, MCP tools, and
/// nothing else in application code. Clients obtain tokens through the hosted sign-in for the
/// derived first-party client and call the two surfaces as any browser, native, or agent client would.
/// </summary>
[TestClass]
public sealed class NotesJourneyIntegrationTests
{
    private const string Origin = HostedAuthorizeTokenFixture.TrustedOrigin;

    [TestMethod]
    public async Task ApiAndMcpShareOneNotebook_AndRevocationSurvivesBothSurfaces()
    {
        await using var host = await NotesHost.StartAsync();
        var alice = await host.CreateUser("Alice");
        var bob = await host.CreateUser("Bob");

        var anonymous = await host.Api(HttpMethod.Get, token: null);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymous.Headers.WwwAuthenticate.ToString().Should().Contain("realm=\"Notes API\"")
            .And.Contain($"resource_metadata=\"{Origin}/.well-known/oauth-protected-resource\"");
        var apiDocument = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        apiDocument.GetProperty("resource").GetString().Should().Be(Origin + "/api");
        var mcpDocument = await host.Client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp");
        mcpDocument.GetProperty("resource").GetString().Should().Be(Origin + "/mcp");

        var aliceApi = await host.Token(alice, "/api");
        var aliceMcp = await host.Token(alice, "/mcp");
        var bobApi = await host.Token(bob, "/api");
        var bobMcp = await host.Token(bob, "/mcp");

        (await host.Api(HttpMethod.Post, aliceApi, "Alice's <private> note")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Api(HttpMethod.Post, aliceApi, "")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await (await host.Api(HttpMethod.Get, aliceApi)).Content.ReadAsStringAsync()).Should().Contain("Alice's <private> note");
        (await (await host.Api(HttpMethod.Get, bobApi)).Content.ReadAsStringAsync()).Should().Be("[]");
        (await host.Api(HttpMethod.Get, aliceMcp)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an MCP token is not an API token");
        (await host.McpRaw(aliceApi)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an API token is not an MCP token");

        await using var aliceTools = await host.Mcp(aliceMcp);
        await using var bobTools = await host.Mcp(bobMcp);
        var aliceNotes = await aliceTools.CallToolAsync("list_notes", new Dictionary<string, object?>());
        aliceNotes.IsError.Should().NotBe(true);
        JsonSerializer.Deserialize<string[]>(Text(aliceNotes)).Should().Contain("Alice's <private> note");
        Text(await bobTools.CallToolAsync("list_notes", new Dictionary<string, object?>())).Should().NotContain("Alice");
        var added = await aliceTools.CallToolAsync("add_note", new Dictionary<string, object?> { ["text"] = "Added through MCP" });
        added.IsError.Should().NotBe(true);
        (await (await host.Api(HttpMethod.Get, aliceApi)).Content.ReadAsStringAsync()).Should().Contain("Added through MCP");

        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotesDbContext>();
            await db.RevokeRoleAsync(alice.Id, NotesAuthorization.NotebookId(alice.Id), NotesAuthorization.OwnerRole);
            await db.SaveChangesAsync();
        }
        (await host.Api(HttpMethod.Get, aliceApi)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await host.Api(HttpMethod.Post, aliceApi, "denied")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await aliceTools.CallToolAsync("list_notes", new Dictionary<string, object?>())).IsError.Should().Be(true);
        (await aliceTools.CallToolAsync("add_note", new Dictionary<string, object?> { ["text"] = "denied" })).IsError.Should().Be(true);
        (await host.Api(HttpMethod.Get, bobApi)).StatusCode.Should().Be(HttpStatusCode.OK, "Bob's notebook is unaffected");
        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotesDbContext>();
            (await db.Set<SqlOSFgaGrant>().CountAsync(x => x.SubjectId == alice.Id)).Should().Be(0, "ordinary requests never restore a revoked grant");
            await db.GrantRoleAsync(alice.Id, NotesAuthorization.NotebookId(alice.Id), NotesAuthorization.OwnerRole);
            await db.SaveChangesAsync();
        }
        (await host.Api(HttpMethod.Get, aliceApi)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await aliceTools.CallToolAsync("list_notes", new Dictionary<string, object?>())).IsError.Should().NotBe(true);

        await using var finalScope = host.App.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NotesDbContext>();
        (await finalDb.Set<SqlOSAuditEvent>().CountAsync(x => x.Action == "mcp.tool.called" && x.UserId == alice.Id)).Should().BeGreaterThanOrEqualTo(5);
    }

    [TestMethod]
    public async Task ConcurrentFirstUse_CommitsOneNotebookAndGrant()
    {
        await using var host = await NotesHost.StartAsync();
        var user = await host.CreateUser("Concurrent");
        var tasks = Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var scope = host.App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<NotesService>().ListAsync(user.Id, CancellationToken.None);
        });
        (await Task.WhenAll(tasks)).Should().OnlyContain(notes => notes.Count == 0);
        await using var check = host.App.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<NotesDbContext>();
        (await db.Notebooks.CountAsync(x => x.UserId == user.Id)).Should().Be(1);
        (await db.Set<SqlOSFgaGrant>().CountAsync(x => x.SubjectId == user.Id)).Should().Be(1);
        (await db.Set<SqlOSFgaResource>().CountAsync(x => x.Id == NotesAuthorization.NotebookId(user.Id))).Should().Be(1);
    }

    private static string Text(CallToolResult result) => string.Join("", result.Content.OfType<TextContentBlock>().Select(x => x.Text));

    private sealed record User(string Id, string Email);

    private sealed class NotesHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public HttpClient Client { get; } = client;

        public static async Task<NotesHost> StartAsync()
        {
            var database = "NotesJourney_" + Guid.NewGuid().ToString("N");
            await TestDatabase.CreateDatabaseAsync(AspireFixture.SqlConnectionString, database);
            var app = await NotesApplication.BuildAsync([], builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Environment.EnvironmentName = "Development";
                builder.Logging.ClearProviders();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = TestDatabase.CreateIsolatedConnectionString(AspireFixture.SqlConnectionString, database),
                    ["Notes:Origin"] = Origin,
                    ["Notes:DatabaseProvider"] = TestDatabase.IsPostgreSql ? "PostgreSQL" : "SqlServer"
                });
            });
            await app.StartAsync();
            var client = app.GetTestClient();
            client.BaseAddress = new Uri(Origin);
            return new NotesHost(app, client);
        }

        public async Task<User> CreateUser(string name)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var email = name.ToLowerInvariant() + Guid.NewGuid().ToString("N") + "@example.test";
            var user = await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>().CreateUserAsync(
                new SqlOSCreateUserRequest(name, email, HostedAuthorizeTokenFixture.Password));
            return new User(user.Id, email);
        }

        /// <summary>
        /// Signs in through the hosted pages as the derived first-party client and exchanges the code
        /// for a token bound to one surface, the way any OIDC client library does it.
        /// </summary>
        public async Task<string> Token(User user, string resource)
        {
            using var browser = new HttpClient(new CookieHandler(App.GetTestServer().CreateHandler())) { BaseAddress = new Uri(Origin) };
            var fixture = new HostedAuthorizeTokenFixture { App = App, Client = browser, Email = user.Email, UserId = user.Id };
            var start = await fixture.StartAuthorizeAsync("openid profile email", clientId: "notes", redirectUri: Origin + "/auth/callback",
                extraParameters: new Dictionary<string, string> { ["resource"] = Origin + resource });
            var code = await fixture.SubmitPasswordLoginAsync(start);
            var result = await fixture.ExchangeAuthorizationCodeAsync(code, start.CodeVerifier, clientId: "notes", redirectUri: Origin + "/auth/callback", resource: Origin + resource);
            return result.RootElement.GetProperty("access_token").GetString()!;
        }

        public Task<HttpResponseMessage> Api(HttpMethod method, string? token, string? text = null)
        {
            var request = new HttpRequestMessage(method, "/api/notes");
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (method == HttpMethod.Post) request.Content = JsonContent.Create(new NoteRequest(text ?? "denied"));
            return Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> McpRaw(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return Client.SendAsync(request);
        }

        public Task<McpClient> Mcp(string token) => McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Origin + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
        }, Client, LoggerFactory.Create(_ => { }), ownsHttpClient: false));

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await using (var scope = App.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<NotesDbContext>().Database.EnsureDeletedAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CookieContainer _cookies = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains("Cookie")) request.Headers.TryAddWithoutValidation("Cookie", _cookies.GetCookieHeader(request.RequestUri!));
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var cookie in cookies) _cookies.SetCookies(request.RequestUri!, cookie);
            return response;
        }
    }
}
