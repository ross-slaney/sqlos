using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Interfaces;
using SqlOS.Extensions;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Services;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class PublicOriginSecurityIntegrationTests
{
    private const string TrustedOrigin = "https://auth.example.test";
    private const string AttackerHost = "attacker.example.test";
    private const string CodeChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task HostileHost_CannotPoisonInvitationOidcDeviceMetadataOrPortalUrls()
    {
        await using var server = await PublicOriginServer.CreateAsync();
        using var client = server.App.GetTestClient();

        var metadataResponse = await SendWithHostAsync(client, HttpMethod.Get, "/sqlos/auth/.well-known/oauth-authorization-server");
        metadataResponse.EnsureSuccessStatusCode();
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        metadata.RootElement.GetProperty("authorization_endpoint").GetString()
            .Should().Be($"{TrustedOrigin}/sqlos/auth/authorize");
        metadata.RootElement.GetProperty("token_endpoint").GetString()
            .Should().Be($"{TrustedOrigin}/sqlos/auth/token");

        var deviceResponse = await SendWithHostAsync(
            client,
            HttpMethod.Post,
            "/sqlos/auth/device_authorization",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = "device-client",
                ["scope"] = "openid"
            }));
        deviceResponse.EnsureSuccessStatusCode();
        using var device = JsonDocument.Parse(await deviceResponse.Content.ReadAsStringAsync());
        device.RootElement.GetProperty("verification_uri").GetString()
            .Should().Be($"{TrustedOrigin}/sqlos/auth/device");
        device.RootElement.GetProperty("verification_uri_complete").GetString()
            .Should().StartWith($"{TrustedOrigin}/sqlos/auth/device?");

        string connectionId;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            connectionId = await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>()
                .Set<SqlOSOidcConnection>()
                .Where(connection => connection.DisplayName == "Test OIDC")
                .Select(connection => connection.Id)
                .SingleAsync();
        }

        var oidcResponse = await SendWithHostAsync(
            client,
            HttpMethod.Post,
            "/sqlos/auth/oidc/authorization-url",
            JsonContent.Create(new SqlOSOidcAuthorizationUrlRequest(
                connectionId,
                "browser-client",
                "https://client.example.test/callback",
                "browser-state",
                CodeChallenge,
                "S256",
                "ada@example.test")));
        oidcResponse.EnsureSuccessStatusCode();
        using var oidc = JsonDocument.Parse(await oidcResponse.Content.ReadAsStringAsync());
        var providerUrl = new Uri(oidc.RootElement.GetProperty("authorizationUrl").GetString()!);
        var providerQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(providerUrl.Query);
        providerQuery["redirect_uri"].ToString()
            .Should().Be($"{TrustedOrigin}/sqlos/auth/oidc/callback");

        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var invitations = scope.ServiceProvider.GetRequiredService<SqlOSInvitationService>();
            var portal = scope.ServiceProvider.GetRequiredService<SqlOSSsoPortalService>();
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Hostile Host Org", null));
            var hostileContext = CreateHostileContext();
            var invitation = await invitations.CreateEmailInvitationAsync(
                new SqlOSCreateEmailInvitationRequest(
                    organization.Id,
                    $"invite-{Guid.NewGuid():N}@example.test",
                    "member"),
                hostileContext);

            invitation.InviteUrl.Should().StartWith($"{TrustedOrigin}/sqlos/auth/invitations/accept?token=");
            invitation.InviteUrl.Should().NotContain(AttackerHost);
            server.EmailSender.Messages.Should().ContainSingle();
            server.EmailSender.Messages.Single().HtmlBody.Should().Contain(TrustedOrigin).And.NotContain(AttackerHost);
            portal.BuildPortalUrl(hostileContext).Should().StartWith(TrustedOrigin).And.NotContain(AttackerHost);
        }
    }

    [TestMethod]
    public async Task DefaultLocalhostIssuer_RemainsZeroConfigurationAndHostIndependent()
    {
        await using var server = await PublicOriginServer.CreateAsync(
            issuer: "https://localhost/sqlos/auth",
            trustedOrigin: "https://localhost");
        using var client = server.App.GetTestClient();

        var response = await SendWithHostAsync(client, HttpMethod.Get, "/sqlos/auth/.well-known/oauth-authorization-server");
        response.EnsureSuccessStatusCode();
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        metadata.RootElement.GetProperty("authorization_endpoint").GetString()
            .Should().Be("https://localhost/sqlos/auth/authorize");
    }

    [TestMethod]
    public async Task ExplicitPublicOrigin_RemainsAuthoritativeUnderHostileHost()
    {
        const string proxyOrigin = "https://proxy.example.test";
        await using var server = await PublicOriginServer.CreateAsync(
            issuer: $"{proxyOrigin}/sqlos/auth",
            trustedOrigin: proxyOrigin,
            publicOrigin: proxyOrigin);
        using var client = server.App.GetTestClient();

        var response = await SendWithHostAsync(client, HttpMethod.Get, "/sqlos/auth/.well-known/oauth-authorization-server");
        response.EnsureSuccessStatusCode();
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        metadata.RootElement.GetProperty("authorization_endpoint").GetString()
            .Should().Be($"{proxyOrigin}/sqlos/auth/authorize");
        metadata.RootElement.GetProperty("token_endpoint").GetString()
            .Should().Be($"{proxyOrigin}/sqlos/auth/token");
    }

    private static async Task<HttpResponseMessage> SendWithHostAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Host = AttackerHost;
        return await client.SendAsync(request);
    }

    private static DefaultHttpContext CreateHostileContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(AttackerHost);
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.140");
        return context;
    }

    private sealed class PublicOriginServer : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required TestAuthEmailSender EmailSender { get; init; }

        public static async Task<PublicOriginServer> CreateAsync(
            string issuer = $"{TrustedOrigin}/sqlos/auth",
            string trustedOrigin = TrustedOrigin,
            string? publicOrigin = null)
        {
            await using var bootstrapContext = await AspireFixture.CreateIsolatedAuthContextAsync("PublicOrigin");
            var connectionString = bootstrapContext.Database.GetConnectionString()!;
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSDbContext>(db => db.UseSqlServer(connectionString));
            builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
            {
                options.AuthServer.Issuer = issuer;
                options.AuthServer.BasePath = "/sqlos/auth";
                options.AuthServer.PublicOrigin = publicOrigin;
                options.AuthServer.DeviceAuthorization.Enabled = true;
                options.AuthServer.SeedBrowserClient("browser-client", "Browser Client", "https://client.example.test/callback");
                options.AuthServer.SeedCliClient("device-client", "Device Client", "openid");
                options.AuthServer.SeedOidcConnection(connection =>
                {
                    connection.ProviderType = SqlOSOidcProviderType.Custom;
                    connection.DisplayName = "Test OIDC";
                    connection.ClientId = "test-provider-client";
                    connection.ClientSecret = "test-provider-secret";
                    connection.UseDiscovery = false;
                    connection.Issuer = "https://provider.example.test";
                    connection.AuthorizationEndpoint = "https://provider.example.test/authorize";
                    connection.TokenEndpoint = "https://provider.example.test/token";
                    connection.JwksUri = "https://provider.example.test/jwks";
                    connection.AllowedCallbackUris = [$"{trustedOrigin}/sqlos/auth/oidc/callback"];
                });
            });
            builder.Services.RemoveAll<IHostedService>();
            builder.Services.RemoveAll<ISqlOSAuthEmailSender>();
            builder.Services.RemoveAll<ISqlOSEmailSender>();
            builder.Services.AddSingleton<ISqlOSAuthEmailSender>(emailSender);
            builder.Services.AddSingleton<ISqlOSEmailSender>(emailSender);

            var app = builder.Build();
            app.MapAuthServer("/sqlos/auth");
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<SqlOSBootstrapper>().InitializeAsync();
            }
            await app.StartAsync();
            return new PublicOriginServer { App = app, EmailSender = emailSender };
        }

        public async ValueTask DisposeAsync()
        {
            await using (var scope = App.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.EnsureDeletedAsync();
            }
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
