using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Calendar.Extensions;
using SqlOS.Calendar.Interfaces;
using SqlOS.Calendar.Models;
using SqlOS.Calendar.Services;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Security;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSCalendarEndpointsTests
{
    private const string ReturnUri = "https://app.example.local/settings/calendar";

    [TestMethod]
    public async Task AdminEndpoints_InDevelopment_ServeSummaryListDetailAndActions()
    {
        using var host = await StartHostAsync("Development");
        var client = host.GetTestClient();
        var connectionId = await SeedConnectionAsync(host, SqlOSCalendarIntegrationMode.ReadPull);

        var summary = await client.GetAsync("/sqlos/admin/calendar/api/summary");
        summary.StatusCode.Should().Be(HttpStatusCode.OK);
        (await summary.Content.ReadAsStringAsync()).Should().Contain("\"connections\":1");

        var list = await client.GetAsync("/sqlos/admin/calendar/api/connections?search=Google&includeRevoked=false&page=1&pageSize=10");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync()).Should().Contain(connectionId);

        var detail = await client.GetAsync($"/sqlos/admin/calendar/api/connections/{connectionId}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await detail.Content.ReadAsStringAsync()).Should().Contain("\"eventCount\"");

        var missingDetail = await client.GetAsync("/sqlos/admin/calendar/api/connections/cal_missing");
        missingDetail.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var sync = await client.PostAsync($"/sqlos/admin/calendar/api/connections/{connectionId}/sync", content: null);
        sync.StatusCode.Should().Be(HttpStatusCode.OK);
        (await sync.Content.ReadAsStringAsync()).Should().Contain("\"eventsUpserted\":2");

        var refresh = await client.PostAsync($"/sqlos/admin/calendar/api/connections/{connectionId}/refresh", content: null);
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);

        var disconnect = await client.PostAsync($"/sqlos/admin/calendar/api/connections/{connectionId}/disconnect", content: null);
        disconnect.StatusCode.Should().Be(HttpStatusCode.OK);
        (await disconnect.Content.ReadAsStringAsync()).Should().Contain("\"status\":\"Revoked\"");

        var syncAfterDisconnect = await client.PostAsync($"/sqlos/admin/calendar/api/connections/{connectionId}/sync", content: null);
        syncAfterDisconnect.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task AdminEndpoints_SyncOnConnectionOnly_ReturnsBadRequest()
    {
        using var host = await StartHostAsync("Development");
        var client = host.GetTestClient();
        var connectionId = await SeedConnectionAsync(host, SqlOSCalendarIntegrationMode.ConnectionOnly);

        var sync = await client.PostAsync($"/sqlos/admin/calendar/api/connections/{connectionId}/sync", content: null);

        sync.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sync.Content.ReadAsStringAsync()).Should().Contain("Connection-only");
    }

    [TestMethod]
    public async Task AdminEndpoints_OutsideDevelopment_AreHiddenWith404()
    {
        using var host = await StartHostAsync("Production");
        var client = host.GetTestClient();
        var connectionId = await SeedConnectionAsync(host, SqlOSCalendarIntegrationMode.ReadPull);

        (await client.GetAsync("/sqlos/admin/calendar/api/summary")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/sqlos/admin/calendar/api/connections")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/sqlos/admin/calendar/api/connections/{connectionId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/sqlos/admin/calendar/api/connections/{connectionId}/sync", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/sqlos/admin/calendar/api/connections/{connectionId}/refresh", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/sqlos/admin/calendar/api/connections/{connectionId}/disconnect", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task ConnectCallbackEndpoint_CompletesFlowAndRedirects()
    {
        using var host = await StartHostAsync("Development");
        var client = host.GetTestClient();

        string authorizationUrl;
        using (var scope = host.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var calendar = scope.ServiceProvider.GetRequiredService<SqlOSCalendarService>();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Endpoint User", "endpoint@example.com", null));
            var oidc = await CreateGoogleConnectionAsync(admin);
            var start = await calendar.StartConnectAsync(new Calendar.Contracts.SqlOSStartCalendarConnectRequest(
                oidc.Id,
                SqlOSCalendarIntegrationMode.ConnectionOnly,
                ReturnUri,
                UserId: user.Id));
            authorizationUrl = start.AuthorizationUrl;
        }

        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers
            .ParseQuery(new Uri(authorizationUrl).Query)["state"].ToString();

        var callback = await client.GetAsync(
            $"/sqlos/auth/calendar/callback?code={Uri.EscapeDataString("success:endpoint@example.com")}&state={Uri.EscapeDataString(state)}");

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.ToString().Should().StartWith(ReturnUri);
        callback.Headers.Location!.ToString().Should().Contain("calendarConnectionId=");
    }

    [TestMethod]
    public async Task ConnectCallbackEndpoint_MissingState_RendersErrorPage()
    {
        using var host = await StartHostAsync("Development");
        var client = host.GetTestClient();

        var callback = await client.GetAsync("/sqlos/auth/calendar/callback?code=success:missing@example.com");

        callback.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        callback.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("'nonce-");
        callback.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
        (await callback.Content.ReadAsStringAsync()).Should().Contain("SqlOS calendar connect error");
    }

    private static async Task<IHost> StartHostAsync(string environment)
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var sqlosOptions = new SqlOSOptions();
        sqlosOptions.AuthServer.PublicOrigin = "https://tests.example.local";
        sqlosOptions.ConfigureCalendar(calendar => calendar.ConfigureSyncScheduler(scheduler => scheduler.Enabled = false));

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .UseEnvironment(environment)
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();
                    services.AddDbContext<TestSqlOSInMemoryDbContext>(db => db.UseInMemoryDatabase(databaseName));
                    services.AddScoped<ISqlOSAuthServerDbContext>(sp => sp.GetRequiredService<TestSqlOSInMemoryDbContext>());
                    services.AddSingleton(Options.Create(sqlosOptions));
                    services.AddSingleton(Options.Create(sqlosOptions.AuthServer));
                    services.AddSingleton<SqlOSBrowserSecurityHeaders>();
                    services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                    services.AddSingleton<SqlOSDashboardSessionService>();
                    services.AddSingleton<IHttpClientFactory, FakeCalendarProviderHttpClientFactory>();
                    services.AddSingleton<ISqlOSCalendarProviderAdapter, SqlOSGoogleCalendarAdapter>();
                    services.AddSingleton<ISqlOSCalendarProviderAdapter, SqlOSMicrosoftGraphCalendarAdapter>();
                    services.AddScoped<SqlOSCryptoService>();
                    services.AddScoped<SqlOSAdminService>();
                    services.AddScoped<SqlOSCalendarService>();
                    services.AddScoped<SqlOSCalendarSyncService>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapSqlOSCalendarConnect("/sqlos/auth");
                        endpoints.MapSqlOSCalendarAdmin("/sqlos");
                    });
                }))
            .StartAsync();

        return host;
    }

    private static async Task<string> SeedConnectionAsync(IHost host, SqlOSCalendarIntegrationMode mode)
    {
        using var scope = host.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
        var calendar = scope.ServiceProvider.GetRequiredService<SqlOSCalendarService>();
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", $"cal-{Guid.NewGuid():N}@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(admin);
        var completion = await calendar.CompleteConnectAsync(
            new CalendarConnectRequestPayload(
                oidc.Id,
                mode,
                user.Id,
                null,
                null,
                ["openid", "email", "https://www.googleapis.com/auth/calendar.readonly"],
                ReturnUri,
                "verifier",
                "https://tests.example.local/sqlos/auth/calendar/callback",
                "https://oauth2.googleapis.com/token"),
            "success:cal@example.com");
        return completion.CalendarConnectionId;
    }

    private static async Task<SqlOSOidcConnection> CreateGoogleConnectionAsync(SqlOSAdminService admin)
        => await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Google, "Google", "google-client", "google-secret",
            ["https://app.example.local/callback/google"], true,
            null, null, null, null, null, null, null, null, null, null, null));
}
