using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSClientCredentialsEndpointTests
{
    private const string Secret = "endpoint-secret-with-at-least-256-bits-of-randomness-123456";

    [TestMethod]
    public async Task TokenEndpoint_BasicAuthentication_IssuesAccessTokenOnlyAndRejectsWrongAudience()
    {
        using var host = await StartHostAsync();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            var configuredClient = await db.Set<SqlOSClientApplication>().SingleAsync();
            configuredClient.ClientType.Should().Be("confidential");
            configuredClient.TokenEndpointAuthMethod.Should().Be("client_secret_basic");
            configuredClient.GrantTypesJson.Should().Contain("client_credentials");
            configuredClient.Audience.Should().Be("https://api.example.test/jobs");
            configuredClient.AllowedScopesJson.Should().Contain("jobs.run");
            (await db.Set<SqlOSClientCredential>().CountAsync()).Should().Be(1);
            (await db.Set<SqlOS.Fga.Models.SqlOSFgaServiceAccount>().CountAsync()).Should().Be(
                0,
                "OAuth client authentication must not create or require an FGA service account");
        }

        using var request = TokenRequest("https://api.example.test/jobs", Secret);
        var response = await host.GetTestClient().SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
        using var json = JsonDocument.Parse(responseBody);
        json.RootElement.TryGetProperty("access_token", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("refresh_token", out _).Should().BeFalse();
        json.RootElement.GetProperty("scope").GetString().Should().Be("jobs.run");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(json.RootElement.GetProperty("access_token").GetString());
        jwt.Claims.Should().ContainSingle(claim => claim.Type == "scope" && claim.Value == "jobs.run");
        jwt.Claims.Should().ContainSingle(claim => claim.Type == "token_kind" && claim.Value == "service");

        using var wrongAudience = TokenRequest("https://api.example.test/other", Secret);
        (await host.GetTestClient().SendAsync(wrongAudience)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var secretPost = await host.GetTestClient().PostAsync(
            "/sqlos/auth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "endpoint-worker",
                ["client_secret"] = Secret,
                ["resource"] = "https://api.example.test/jobs",
                ["scope"] = "jobs.run"
            }));
        secretPost.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        secretPost.Headers.WwwAuthenticate.Should().ContainSingle(x => x.Scheme == "Basic");
    }

    private static HttpRequestMessage TokenRequest(string resource, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["resource"] = resource,
                ["scope"] = "jobs.run"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"endpoint-worker:{secret}")));
        return request;
    }

    private static Task<IHost> StartHostAsync()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        return new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();
                    services.AddDbContext<TestSqlOSInMemoryDbContext>(db =>
                        db.UseInMemoryDatabase(databaseName));
                    services.AddSqlOS<TestSqlOSInMemoryDbContext>(sqlos =>
                    {
                        sqlos.AuthServer.Issuer = "https://tests.example.local/sqlos/auth";
                        sqlos.AuthServer.BasePath = "/sqlos/auth";
                        sqlos.AuthServer.ClientSeeds.Add(new()
                        {
                            ClientId = "endpoint-worker",
                            Name = "Endpoint Worker",
                            Audience = "https://api.example.test/jobs",
                            ClientType = "confidential",
                            EnableClientCredentials = true,
                            RequirePkce = false,
                            AllowedScopes = ["jobs.run"],
                            ClientSecretResolver = () => Secret
                        });
                    });
                    foreach (var service in services.Where(x => x.ServiceType == typeof(IHostedService)).ToList())
                    {
                        services.Remove(service);
                    }
                    services.AddSingleton<ISqlOSAuthEmailSender>(new TestAuthEmailSender { IsConfigured = true });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapAuthServer("/sqlos/auth"));
                }))
            .StartAsync();
    }
}
