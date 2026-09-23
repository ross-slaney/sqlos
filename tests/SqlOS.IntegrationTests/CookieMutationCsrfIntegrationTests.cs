using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
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
using SqlOS.Security;
using SqlOS.Services;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class CookieMutationCsrfIntegrationTests
{
    private const string TrustedOrigin = "https://auth.example.test";
    private const string SiblingOrigin = "https://evil.example.test";
    private const string ProxyOrigin = "https://proxy.example.test";
    private const string DashboardPassword = "SqlOSDashboard!123";

    [TestMethod]
    public async Task DashboardPasswordSession_RejectsSiblingOriginBeforeMutationOrSuccessAudit()
    {
        await using var server = await CsrfServer.StartAsync(options =>
        {
            options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
            options.Dashboard.Password = DashboardPassword;
        });
        var cookie = await server.LoginAsync();
        var disabled = await server.CreateClientAsync("csrf-disabled");
        var active = await server.CreateClientAsync("csrf-active");
        await server.DisableClientAsync(disabled.Id);
        var activeKid = await server.ActiveSigningKeyKidAsync();
        var clientCount = await server.CountClientsAsync();

        var hostileEnable = await server.SendAsync(
            HttpMethod.Post,
            $"/sqlos/admin/auth/api/clients/{disabled.Id}/enable",
            cookie,
            SiblingOrigin,
            csrfHeader: false);
        await AssertRejectedAsync(hostileEnable, cookie);

        var hostileRotate = await server.SendAsync(
            HttpMethod.Post,
            "/sqlos/admin/auth/api/signing-keys/rotate",
            cookie,
            SiblingOrigin,
            csrfHeader: true);
        await AssertRejectedAsync(hostileRotate, cookie);

        var hostileDisable = await server.SendAsync(
            HttpMethod.Post,
            $"/sqlos/admin/auth/api/clients/{active.Id}/disable",
            cookie,
            SiblingOrigin,
            csrfHeader: true,
            JsonContent.Create(new { reason = "sibling" }));
        await AssertRejectedAsync(hostileDisable, cookie);

        var hostileLogout = await server.SendAsync(
            HttpMethod.Post,
            "/sqlos/dashboard-auth/logout",
            cookie,
            SiblingOrigin,
            csrfHeader: false);
        await AssertRejectedAsync(hostileLogout, cookie);

        (await server.IsClientActiveAsync(disabled.Id)).Should().BeFalse();
        (await server.IsClientActiveAsync(active.Id)).Should().BeTrue();
        (await server.ActiveSigningKeyKidAsync()).Should().Be(activeKid);
        (await server.CountClientsAsync()).Should().Be(clientCount);
        (await server.CountAuditAsync("client.enabled")).Should().Be(0);
        (await server.CountAuditAsync("signing_key_rotated_manual")).Should().Be(0);
        (await server.CountAuditAsync("dashboard.logout")).Should().Be(0);
        await server.AssertRejectionAuditsHideAsync(cookie);

        var stats = await server.SendAsync(HttpMethod.Get, "/sqlos/admin/auth/api/stats", cookie, origin: null, csrfHeader: false);
        stats.StatusCode.Should().Be(HttpStatusCode.OK);

        var enabled = await server.SendAsync(
            HttpMethod.Post,
            $"/sqlos/admin/auth/api/clients/{disabled.Id}/enable",
            cookie,
            TrustedOrigin,
            csrfHeader: true);
        enabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await server.IsClientActiveAsync(disabled.Id)).Should().BeTrue();
        (await server.CountAuditAsync("client.enabled")).Should().Be(1);

        var created = await server.SendAsync(
            HttpMethod.Post,
            "/sqlos/admin/auth/api/clients",
            cookie,
            TrustedOrigin,
            csrfHeader: true,
            JsonContent.Create(new
            {
                clientId = "csrf-created",
                name = "CSRF Created",
                audience = "sqlos",
                redirectUris = new[] { "https://app.example.test/callback" }
            }));
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        (await server.CountClientsAsync()).Should().Be(clientCount + 1);

        var logout = await server.SendAsync(
            HttpMethod.Post,
            "/sqlos/dashboard-auth/logout",
            cookie,
            TrustedOrigin,
            csrfHeader: true);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        logout.Headers.TryGetValues("Set-Cookie", out var cleared).Should().BeTrue();
        string.Join("\n", cleared!).Should().Contain("SqlOS.Dashboard.Session=");
        (await server.CountAuditAsync("dashboard.logout")).Should().Be(1);
    }

    [TestMethod]
    public async Task SsoPortalSession_RejectsSiblingOriginBeforeProviderChangeOrSignOut()
    {
        await using var server = await CsrfServer.StartAsync(options =>
        {
            options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
            options.Dashboard.Password = DashboardPassword;
        });
        var opened = await server.OpenPortalSessionAsync("okta");

        var hostileProvider = await server.SendAsync(
            HttpMethod.Put,
            "/sqlos/admin/auth/sso-portal/api/provider",
            opened.Cookie,
            SiblingOrigin,
            csrfHeader: true,
            JsonContent.Create(new SqlOSUpdateSsoPortalProviderRequest("google-workspace")));
        await AssertRejectedAsync(hostileProvider, opened.RawToken);

        var hostileSignOut = await server.SendAsync(
            HttpMethod.Post,
            "/sqlos/admin/auth/sso-portal/api/signout",
            opened.Cookie,
            SiblingOrigin,
            csrfHeader: false);
        await AssertRejectedAsync(hostileSignOut, opened.RawToken);

        (await server.PortalProviderAsync(opened.SessionId)).Should().Be("okta");
        (await server.PortalRevokedAtAsync(opened.SessionId)).Should().BeNull();
        (await server.CountAuditAsync("sso.portal.provider.selected")).Should().Be(0);
        (await server.CountAuditAsync("sso.portal.session.closed")).Should().Be(0);
        await server.AssertRejectionAuditsHideAsync(opened.RawToken);

        var provider = await server.SendAsync(
            HttpMethod.Put,
            "/sqlos/admin/auth/sso-portal/api/provider",
            opened.Cookie,
            TrustedOrigin,
            csrfHeader: true,
            JsonContent.Create(new SqlOSUpdateSsoPortalProviderRequest("google-workspace")));
        provider.StatusCode.Should().Be(HttpStatusCode.OK);
        (await server.PortalProviderAsync(opened.SessionId)).Should().Be("google-workspace");
        (await server.CountAuditAsync("sso.portal.provider.selected")).Should().Be(1);

        var signOut = await server.SendAsync(
            HttpMethod.Post,
            "/sqlos/admin/auth/sso-portal/api/signout",
            opened.Cookie,
            TrustedOrigin,
            csrfHeader: true);
        signOut.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await server.PortalRevokedAtAsync(opened.SessionId)).Should().NotBeNull();
        (await server.CountAuditAsync("sso.portal.session.closed")).Should().Be(1);
        (await signOut.Content.ReadAsStringAsync()).Should().NotContain(opened.RawToken);
    }

    [TestMethod]
    public async Task TrustedForwardedHost_AllowsMatchingOrigin_AndUntrustedForwardedHostDoesNot()
    {
        await using var trusted = await CsrfServer.StartAsync(
            options =>
            {
                options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
                options.Dashboard.Password = DashboardPassword;
            },
            trustForwardedHost: true);
        var trustedCookie = await trusted.LoginAsync();
        var trustedClient = await trusted.CreateClientAsync("csrf-proxy");
        await trusted.DisableClientAsync(trustedClient.Id);

        var allowed = await trusted.SendAsync(
            HttpMethod.Post,
            $"/sqlos/admin/auth/api/clients/{trustedClient.Id}/enable",
            trustedCookie,
            ProxyOrigin,
            csrfHeader: true,
            configure: request =>
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "proxy.example.test");
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            });
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await trusted.IsClientActiveAsync(trustedClient.Id)).Should().BeTrue();

        await using var untrusted = await CsrfServer.StartAsync(
            options =>
            {
                options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
                options.Dashboard.Password = DashboardPassword;
            },
            trustForwardedHost: false);
        var untrustedCookie = await untrusted.LoginAsync();
        var untrustedClient = await untrusted.CreateClientAsync("csrf-untrusted-proxy");
        await untrusted.DisableClientAsync(untrustedClient.Id);
        var rejected = await untrusted.SendAsync(
            HttpMethod.Post,
            $"/sqlos/admin/auth/api/clients/{untrustedClient.Id}/enable",
            untrustedCookie,
            ProxyOrigin,
            csrfHeader: true,
            configure: request =>
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "proxy.example.test");
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            });
        await AssertRejectedAsync(rejected, untrustedCookie);
        (await untrusted.IsClientActiveAsync(untrustedClient.Id)).Should().BeFalse();
        (await untrusted.CountAuditAsync("client.enabled")).Should().Be(0);
    }

    [TestMethod]
    public async Task BearerAdministration_WithoutAmbientCookie_DoesNotRequireCsrf()
    {
        await using var server = await CsrfServer.StartAsync(options =>
        {
            options.Dashboard.AuthorizationCallback = context =>
                Task.FromResult(string.Equals(
                    context.Request.Headers.Authorization.ToString(),
                    "Bearer operator-token",
                    StringComparison.Ordinal));
        });

        var created = await server.SendAsync(
            HttpMethod.Post,
            "/sqlos/admin/auth/api/clients",
            cookie: null,
            origin: null,
            csrfHeader: false,
            JsonContent.Create(new
            {
                clientId = "csrf-bearer",
                name = "Bearer Client",
                audience = "sqlos",
                redirectUris = new[] { "https://app.example.test/callback" }
            }),
            configure: request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer operator-token"));
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = createdBody.GetProperty("id").GetString();
        clientId.Should().NotBeNullOrWhiteSpace();
        await server.DisableClientAsync(clientId!);

        var hostile = await server.SendAsync(
            HttpMethod.Post,
            $"/sqlos/admin/auth/api/clients/{clientId}/enable",
            $"{SqlOSCookieMutationCsrf.DashboardSessionCookieName}=ambient-cookie",
            SiblingOrigin,
            csrfHeader: true,
            configure: request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer operator-token"));
        hostile.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await server.IsClientActiveAsync(clientId!)).Should().BeFalse();
        (await server.CountAuditAsync("client.enabled")).Should().Be(0);

        var enabled = await server.SendAsync(
            HttpMethod.Post,
            $"/sqlos/admin/auth/api/clients/{clientId}/enable",
            cookie: null,
            origin: SiblingOrigin,
            csrfHeader: false,
            configure: request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer operator-token"));
        enabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await server.IsClientActiveAsync(clientId!)).Should().BeTrue();
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response, string secret)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.ToString().Should().Contain("no-store");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"error\":\"csrf_rejected\"");
        body.Should().Contain("The request could not be verified.");
        body.Should().NotContain(secret);
    }

    private sealed class CsrfServer : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }

        public static async Task<CsrfServer> StartAsync(
            Action<SqlOSOptions> configure,
            bool? trustForwardedHost = null)
        {
            await using var bootstrap = await AspireFixture.CreateIsolatedAuthContextAsync("CookieCsrf");
            var connectionString = bootstrap.Database.GetConnectionString()!;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSDbContext>(database => database.UseTestProvider(connectionString));
            if (trustForwardedHost.HasValue)
            {
                var proxyAddress = IPAddress.Parse("10.0.0.10");
                var clientAddress = trustForwardedHost.Value
                    ? proxyAddress
                    : IPAddress.Parse("192.0.2.20");
                builder.Services.Configure<ForwardedHeadersOptions>(options =>
                {
                    options.ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
                    options.KnownProxies.Clear();
                    options.KnownNetworks.Clear();
                    options.KnownProxies.Add(proxyAddress);
                });
                // Registered before AddSqlOS so this middleware runs before UseForwardedHeaders.
                // TestServer does not set a remote address, and the forwarded-headers middleware
                // treats that missing address as the first hop.
                builder.Services.AddSingleton<IStartupFilter>(new FixedRemoteIpStartupFilter(clientAddress));
            }

            builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
            {
                options.AuthServer.Issuer = $"{TrustedOrigin}/sqlos/auth";
                options.AuthServer.PublicOrigin = TrustedOrigin;
                options.AuthServer.BasePath = "/sqlos/auth";
                configure(options);
            });

            builder.Services.RemoveAll<IHostedService>();
            var app = builder.Build();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<SqlOSBootstrapper>().InitializeAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            client.BaseAddress = new Uri(TrustedOrigin);
            return new CsrfServer { App = app, Client = client };
        }

        public async Task<string> LoginAsync()
        {
            var response = await Client.PostAsJsonAsync("/sqlos/dashboard-auth/login", new { password = DashboardPassword });
            response.EnsureSuccessStatusCode();
            response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
            return cookies!.Select(value => value.Split(';', 2)[0])
                .Single(value => value.StartsWith("SqlOS.Dashboard.Session=", StringComparison.Ordinal));
        }

        public async Task<SqlOSClientApplication> CreateClientAsync(string clientId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>().CreateClientAsync(
                new SqlOSCreateClientRequest(
                    clientId,
                    clientId,
                    "sqlos",
                    ["https://app.example.test/callback"]));
        }

        public async Task DisableClientAsync(string clientApplicationId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .DisableClientAsync(clientApplicationId, "csrf-setup");
        }

        public async Task<PortalSession> OpenPortalSessionAsync(string provider)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var portal = scope.ServiceProvider.GetRequiredService<SqlOSSsoPortalService>();
            var organization = await admin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest($"Portal {provider}", null, $"{provider}.csrf.test"));
            var created = await portal.CreateSessionAsync(
                new SqlOSCreateSsoPortalSessionRequest(organization.Id, Provider: provider));
            var openContext = new DefaultHttpContext();
            openContext.Request.Scheme = Uri.UriSchemeHttps;
            openContext.RequestServices = scope.ServiceProvider;
            await portal.OpenSessionAsync(ExtractToken(created.SetupUrl!), openContext);
            var cookie = openContext.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
            return new PortalSession(created.Id, cookie, cookie["sqlos_sso_portal=".Length..]);
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            string? cookie,
            string? origin,
            bool csrfHeader,
            HttpContent? content = null,
            Action<HttpRequestMessage>? configure = null)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            if (cookie != null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            if (origin != null)
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            if (csrfHeader)
            {
                request.Headers.TryAddWithoutValidation(
                    SqlOSCookieMutationCsrf.HeaderName,
                    SqlOSCookieMutationCsrf.HeaderValue);
            }

            configure?.Invoke(request);
            return await Client.SendAsync(request);
        }

        public async Task<bool> IsClientActiveAsync(string clientApplicationId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            return await context.Set<SqlOSClientApplication>().AsNoTracking()
                .Where(client => client.Id == clientApplicationId)
                .Select(client => client.IsActive)
                .SingleAsync();
        }

        public async Task<int> CountClientsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            return await context.Set<SqlOSClientApplication>().CountAsync();
        }

        public async Task<string> ActiveSigningKeyKidAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            return await context.Set<SqlOSSigningKey>().AsNoTracking()
                .Where(key => key.IsActive)
                .Select(key => key.Kid)
                .SingleAsync();
        }

        public async Task<string?> PortalProviderAsync(string sessionId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            return await context.Set<SqlOSSsoPortalSession>().AsNoTracking()
                .Where(session => session.Id == sessionId)
                .Select(session => session.Provider)
                .SingleAsync();
        }

        public async Task<DateTime?> PortalRevokedAtAsync(string sessionId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            return await context.Set<SqlOSSsoPortalSession>().AsNoTracking()
                .Where(session => session.Id == sessionId)
                .Select(session => session.RevokedAt)
                .SingleAsync();
        }

        public async Task<int> CountAuditAsync(string eventType)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            return await context.Set<SqlOSAuditEvent>().AsNoTracking()
                .CountAsync(audit => audit.EventType == eventType || audit.Action == eventType);
        }

        public async Task AssertRejectionAuditsHideAsync(string secret)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            var rows = await context.Set<SqlOSAuditEvent>().AsNoTracking()
                .Where(audit => audit.EventType == SqlOSCookieMutationCsrf.RejectionEventType
                    || audit.Action == SqlOSCookieMutationCsrf.RejectionEventType)
                .ToListAsync();
            rows.Should().NotBeEmpty();
            foreach (var row in rows)
            {
                var serialized = string.Join(
                    "\n",
                    row.MetadataJson,
                    row.DataJson,
                    row.ContextJson,
                    row.ActorId,
                    row.SessionId);
                serialized.Should().NotContain(secret);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await using (var scope = App.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.EnsureDeletedAsync();
            }

            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }

        private static string ExtractToken(string setupUrl)
        {
            var query = new Uri(setupUrl).Query;
            return Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query)["token"].ToString();
        }
    }

    private sealed record PortalSession(string SessionId, string Cookie, string RawToken);

    private sealed class FixedRemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use((context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = remoteIp;
                    return continuation(context);
                });
                next(app);
            };
    }
}
