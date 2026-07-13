using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAuthLifecycleEndpointTests
{
    [TestMethod]
    public async Task TokenEndpoint_RefreshWithoutOrganization_AfterOffboarding_ReturnsGenericInvalidGrant()
    {
        using var host = await StartHostAsync();
        string refreshToken;
        string userId;
        string organizationId;

        using (var issuanceScope = host.Services.CreateScope())
        {
            var crypto = issuanceScope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            var admin = issuanceScope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var settings = issuanceScope.ServiceProvider.GetRequiredService<SqlOSSettingsService>();
            var auth = issuanceScope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.EnsureDefaultSettingsAsync();

            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Protocol Offboard",
                $"protocol-offboard-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            var organization = await admin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest($"Protocol {Guid.NewGuid():N}", null));
            await admin.CreateMembershipAsync(
                organization.Id,
                new SqlOSCreateMembershipRequest(user.Id, "member"));
            var context = issuanceScope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var client = await context.Set<SqlOSClientApplication>()
                .SingleAsync(x => x.ClientId == "protocol-client");
            var tokens = await auth.CreateSessionTokensForUserAsync(
                user,
                client,
                organization.Id,
                "password",
                "ProtocolEndpointTest",
                "203.0.113.25");
            refreshToken = tokens.RefreshToken;
            userId = user.Id;
            organizationId = organization.Id;
        }

        using (var offboardingScope = host.Services.CreateScope())
        {
            var context = offboardingScope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var membership = await context.Set<SqlOSMembership>()
                .SingleAsync(x => x.UserId == userId && x.OrganizationId == organizationId);
            membership.IsActive = false;
            await context.SaveChangesAsync();
        }

        var response = await host.GetTestClient().PostAsync(
            "/sqlos/auth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = "protocol-client"
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().Should().Be("invalid_grant");
        json.RootElement.GetProperty("error_description").GetString()
            .Should().Be("Session is no longer active.");
        json.RootElement.ToString().Should().NotContain("membership_inactive");
        json.RootElement.ToString().Should().NotContain(organizationId);

        using var auditScope = host.Services.CreateScope();
        var auditContext = auditScope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
        (await auditContext.Set<SqlOSAuditEvent>().AnyAsync(x =>
            x.EventType == "auth.lifecycle.denied"
            && x.UserId == userId
            && x.OrganizationId == organizationId
            && x.DataJson!.Contains("membership_inactive"))).Should().BeTrue();
    }

    private static async Task<IHost> StartHostAsync()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        return await new HostBuilder()
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
                        sqlos.AuthServer.SeedBrowserClient(
                            "protocol-client",
                            "Protocol Client",
                            "https://client.example.test/callback");
                    });
                    foreach (var hostedService in services
                        .Where(x => x.ServiceType == typeof(IHostedService))
                        .ToList())
                    {
                        services.Remove(hostedService);
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
