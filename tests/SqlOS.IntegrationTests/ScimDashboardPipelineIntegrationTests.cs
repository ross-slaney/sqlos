using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Configuration;
using SqlOS.Extensions;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Services;

namespace SqlOS.IntegrationTests;

/// <summary>
/// SCIM at its default path sits under the dashboard root, so it must survive the real pipeline:
/// AddSqlOS's startup filter installs the dashboard middleware in front of the endpoints it maps
/// (#447). These hosts keep that filter and map nothing themselves, unlike the SCIM protocol
/// tests, and exercise every dashboard authentication mode.
/// </summary>
[TestClass]
public sealed class ScimDashboardPipelineIntegrationTests
{
    private const string Origin = "https://scim-pipeline.integration.test";
    private const string DefaultScimBasePath = "/sqlos/scim/v2";
    private const string DashboardPassword = "dashboard-password-for-tests";
    private const string OperatorHeader = "X-Test-Operator";

    [TestMethod]
    public async Task DevelopmentOnlyInProduction_ScimReachesItsEndpoints_AndTheDashboardStaysHidden()
    {
        await using var host = await ScimPipelineHost.CreateAsync(Environments.Production, _ => { });

        await host.AssertScimReachableAsync();
        using var dashboard = await host.Client.GetAsync("/sqlos/");
        dashboard.StatusCode.Should().Be(HttpStatusCode.NotFound, "DevelopmentOnly hides the dashboard outside Development");
        using var adminPage = await host.Client.GetAsync("/sqlos/admin/auth/users");
        adminPage.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task DevelopmentOnlyInDevelopment_ScimReachesItsEndpoints_AndTheDashboardStaysOpen()
    {
        await using var host = await ScimPipelineHost.CreateAsync(Environments.Development, _ => { });

        await host.AssertScimReachableAsync();
        using var dashboard = await host.Client.GetAsync("/sqlos/");
        dashboard.StatusCode.Should().Be(HttpStatusCode.OK);
        (await dashboard.Content.ReadAsStringAsync()).Should().Contain("\"scimEnabled\":true");
    }

    [TestMethod]
    public async Task PasswordMode_ScimReachesItsEndpoints_AndTheDashboardStillRequiresItsLogin()
    {
        await using var host = await ScimPipelineHost.CreateAsync(Environments.Production, dashboard =>
        {
            dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
            dashboard.Password = DashboardPassword;
        });

        await host.AssertScimReachableAsync();
        using var adminPage = await host.Client.GetAsync("/sqlos/admin/auth/users");
        adminPage.StatusCode.Should().Be(HttpStatusCode.Redirect);
        adminPage.Headers.Location!.OriginalString.Should().StartWith("/sqlos/login?next=");
        using var adminPost = await host.Client.PostAsync("/sqlos/admin/auth/users", new StringContent("{}", Encoding.UTF8, "application/json"));
        adminPost.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task AuthorizationCallbackMode_ScimReachesItsEndpoints_AndTheCallbackStillGuardsTheDashboard()
    {
        await using var host = await ScimPipelineHost.CreateAsync(Environments.Production, dashboard =>
            dashboard.AuthorizationCallback = context => Task.FromResult(context.Request.Headers.ContainsKey(OperatorHeader)));

        await host.AssertScimReachableAsync();
        using var anonymous = await host.Client.GetAsync("/sqlos/");
        anonymous.StatusCode.Should().Be(HttpStatusCode.NotFound, "the callback refuses a request that is not an operator's");
        using var operatorRequest = new HttpRequestMessage(HttpMethod.Get, "/sqlos/");
        operatorRequest.Headers.Add(OperatorHeader, "1");
        using var operatorResponse = await host.Client.SendAsync(operatorRequest);
        operatorResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task CustomScimBasePathUnderTheDashboard_ReachesItsEndpoints_AndTheDefaultPathIsNotPassedThrough()
    {
        const string customBasePath = "/sqlos/directory/scim";
        await using var host = await ScimPipelineHost.CreateAsync(
            Environments.Production,
            dashboard =>
            {
                dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
                dashboard.Password = DashboardPassword;
            },
            scimBasePath: customBasePath + "/");

        await host.AssertScimReachableAsync();
        using var defaultPath = await host.SendScimAsync(HttpMethod.Get, "/Users", host.Token, scimBasePath: DefaultScimBasePath);
        defaultPath.StatusCode.Should().Be(HttpStatusCode.Redirect, "only the configured SCIM path passes through the dashboard");
    }

    [TestMethod]
    public async Task ScimDisabled_LeavesTheDefaultScimPathToTheDashboard()
    {
        await using var host = await ScimPipelineHost.CreateAsync(Environments.Production, _ => { }, enableScim: false);

        using var response = await host.SendScimAsync(HttpMethod.Get, "/ServiceProviderConfig", token: null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("application/scim+json");
    }

    /// <summary>
    /// A SqlOS host built the way applications build one: AddSqlOS with its startup filter, no
    /// manual endpoint mapping, on an isolated SQL database with one SCIM connection.
    /// </summary>
    private sealed class ScimPipelineHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ScimPipelineHost(WebApplication app, HttpClient client, string scimBasePath)
        {
            _app = app;
            Client = client;
            ScimBasePath = scimBasePath;
        }

        public HttpClient Client { get; }
        public string ScimBasePath { get; }
        public string Token { get; private set; } = string.Empty;

        public static async Task<ScimPipelineHost> CreateAsync(
            string environmentName,
            Action<SqlOSDashboardOptions> configureDashboard,
            string scimBasePath = DefaultScimBasePath,
            bool enableScim = true)
        {
            string connectionString;
            await using (var bootstrap = await AspireFixture.CreateIsolatedAuthContextAsync("ScimPipeline"))
            {
                connectionString = bootstrap.Database.GetConnectionString()!;
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSDbContext>(options => options.UseTestProvider(connectionString));
            builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
            {
                options.AuthServer.PublicOrigin = Origin;
                options.AuthServer.Issuer = $"{Origin}/sqlos/auth";
                options.AuthServer.EnableScim = enableScim;
                options.AuthServer.ScimBasePath = scimBasePath;
                configureDashboard(options.Dashboard);
            });
            builder.Services.AddSingleton(AspireFixture.DataProtectionProvider);
            // Schema bootstrap runs below instead of in a hosted service; the startup filter stays.
            builder.Services.RemoveAll<IHostedService>();

            var app = builder.Build();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<SqlOSBootstrapper>().InitializeAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            client.BaseAddress = new Uri(Origin);
            var host = new ScimPipelineHost(app, client, scimBasePath.TrimEnd('/'));
            if (enableScim)
            {
                await host.CreateScimConnectionAsync();
            }

            return host;
        }

        public async Task<HttpResponseMessage> SendScimAsync(
            HttpMethod method,
            string relativePath,
            string? token,
            JsonObject? body = null,
            string? scimBasePath = null)
        {
            var request = new HttpRequestMessage(method, $"{scimBasePath ?? ScimBasePath}{relativePath}");
            if (token != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (body != null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/scim+json");
            }

            return await Client.SendAsync(request);
        }

        /// <summary>
        /// Reads and writes through the SCIM endpoints with the connection's token, and gets the
        /// SCIM 401 (not a dashboard 404, login redirect, or 401) without a valid one.
        /// </summary>
        public async Task AssertScimReachableAsync()
        {
            using (var configuration = await SendScimAsync(HttpMethod.Get, "/ServiceProviderConfig", Token))
            {
                await AssertScimJsonAsync(configuration, HttpStatusCode.OK, "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig");
            }

            using (var users = await SendScimAsync(HttpMethod.Get, "/Users", Token))
            {
                await AssertScimJsonAsync(users, HttpStatusCode.OK, "urn:ietf:params:scim:api:messages:2.0:ListResponse");
            }

            using (var groups = await SendScimAsync(HttpMethod.Get, "/Groups", Token))
            {
                await AssertScimJsonAsync(groups, HttpStatusCode.OK, "urn:ietf:params:scim:api:messages:2.0:ListResponse");
            }

            var userName = $"directory-{Guid.NewGuid():N}@pipeline.example.test";
            using (var created = await SendScimAsync(HttpMethod.Post, "/Users", Token, new JsonObject
            {
                ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:User"),
                ["externalId"] = $"external-{Guid.NewGuid():N}",
                ["userName"] = userName,
                ["displayName"] = "Directory User",
                ["active"] = true,
                ["emails"] = new JsonArray(new JsonObject { ["value"] = userName, ["type"] = "work", ["primary"] = true })
            }))
            {
                await AssertScimJsonAsync(created, HttpStatusCode.Created, "urn:ietf:params:scim:schemas:core:2.0:User");
            }

            using (var created = await SendScimAsync(HttpMethod.Post, "/Groups", Token, new JsonObject
            {
                ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Group"),
                ["externalId"] = $"external-group-{Guid.NewGuid():N}",
                ["displayName"] = $"Directory Group {Guid.NewGuid():N}"
            }))
            {
                await AssertScimJsonAsync(created, HttpStatusCode.Created, "urn:ietf:params:scim:schemas:core:2.0:Group");
            }

            using (var missing = await SendScimAsync(HttpMethod.Get, "/Users", token: null))
            {
                await AssertScimUnauthorizedAsync(missing);
            }

            using (var invalid = await SendScimAsync(HttpMethod.Post, "/Users", "not-a-scim-token", new JsonObject()))
            {
                await AssertScimUnauthorizedAsync(invalid);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await using (var scope = _app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.EnsureDeletedAsync();
            }

            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private async Task CreateScimConnectionAsync()
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"SCIM Pipeline {Guid.NewGuid():N}", null));
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            // SCIM only creates users whose email is inside one of the organization's verified domains.
            context.Set<SqlOSOrganizationDomain>().Add(new SqlOSOrganizationDomain
            {
                Id = $"dom_{Guid.NewGuid():N}"[..28],
                OrganizationId = organization.Id,
                Domain = "pipeline.example.test",
                Status = SqlOSOrganizationDomainStatuses.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                VerifiedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            Token = (await admin.CreateScimConnectionAsync(new SqlOSCreateScimConnectionRequest(
                organization.Id,
                "Pipeline Directory",
                Enabled: true))).Token;
        }

        private static async Task AssertScimJsonAsync(HttpResponseMessage response, HttpStatusCode status, string schema)
        {
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(status, "{0} {1} answered: {2}", response.RequestMessage!.Method, response.RequestMessage.RequestUri, body);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/scim+json");
            JsonNode.Parse(body)!["schemas"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Contain(schema);
        }

        private static async Task AssertScimUnauthorizedAsync(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "SCIM answers a bad token itself: {0}", body);
            response.Headers.WwwAuthenticate.ToString().Should().Contain("SqlOS SCIM");
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/scim+json");
            var error = JsonNode.Parse(body)!;
            error["schemas"]!.AsArray().Select(node => node!.GetValue<string>())
                .Should().Contain("urn:ietf:params:scim:api:messages:2.0:Error");
            error["status"]!.ToString().Should().Be("401");
        }
    }
}
