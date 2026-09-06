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
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class SilentSsoSecurityIntegrationTests
{
    private const string CodeChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task ExistingCookie_ReevaluatesMfaAndHandsChallengeToHeadlessUi()
    {
        await using var server = await SilentSsoServer.CreateAsync();
        string cookie;
        string userId;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            var totp = scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>();
            var authPageSession = scope.ServiceProvider.GetRequiredService<SqlOSAuthPageSessionService>();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Silent MFA User",
                $"silent-mfa-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            userId = user.Id;
            var enrollment = await auth.StartTotpEnrollmentAsync(user.Id, new SqlOSTotpEnrollmentStartRequest());
            await auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
                enrollment.EnrollmentToken,
                totp.GenerateCodeForTesting(enrollment.Secret)));
            cookie = await CreateSessionCookieAsync(authPageSession, user, organizationId: null, "password");
        }

        using var client = server.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var authorize = await client.GetAsync(BuildAuthorizeUrl("owned-client", "state-mfa"));
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var handoff = authorize.Headers.Location
            ?? throw new AssertFailedException("Authorize response did not include a headless UI location.");
        handoff.Host.Should().Be("app.example.test");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(handoff.Query);
        query["view"].ToString().Should().Be("mfa");
        var requestId = query["request"].ToString();
        var mfaToken = query["mfaToken"].ToString();
        mfaToken.Should().NotBeNullOrWhiteSpace();

        var view = await client.GetAsync(
            $"/sqlos/auth/headless/requests/{Uri.EscapeDataString(requestId)}?view=mfa&mfaToken={Uri.EscapeDataString(mfaToken)}");
        view.EnsureSuccessStatusCode();
        using var viewJson = JsonDocument.Parse(await view.Content.ReadAsStringAsync());
        viewJson.RootElement.GetProperty("view").GetString().Should().Be("mfa");
        viewJson.RootElement.GetProperty("mfaToken").GetString().Should().Be(mfaToken);

        var silent = await client.GetAsync(BuildAuthorizeUrl("owned-client", "state-mfa-none", "none"));
        silent.StatusCode.Should().Be(HttpStatusCode.Redirect);
        silent.Headers.Location!.Host.Should().Be("owned.example.test");
        var silentQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(silent.Headers.Location.Query);
        silentQuery["error"].ToString().Should().Be("interaction_required");
        silentQuery["state"].ToString().Should().Be("state-mfa-none");

        await using var verifyScope = server.App.Services.CreateAsyncScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSAuthorizationCode>().CountAsync(x => x.UserId == userId)).Should().Be(0);
        (await context.Set<SqlOSTemporaryToken>().CountAsync(x =>
            x.UserId == userId && x.Purpose == SqlOSAuthService.MfaChallengePurpose && x.ConsumedAt == null)).Should().Be(1);
        (await context.Set<SqlOSTemporaryToken>().CountAsync(x =>
            x.UserId == userId && x.Purpose == SqlOSAuthService.MfaChallengePurpose && x.ConsumedAt != null)).Should().Be(1);
    }

    [TestMethod]
    public async Task HostedExistingCookie_ReevaluatesMfaAndRendersChallengeWithoutCode()
    {
        await using var server = await SilentSsoServer.CreateAsync(headless: false);
        string cookie;
        string userId;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            var totp = scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>();
            var authPageSession = scope.ServiceProvider.GetRequiredService<SqlOSAuthPageSessionService>();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Hosted Silent MFA User",
                $"hosted-silent-mfa-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            userId = user.Id;
            var enrollment = await auth.StartTotpEnrollmentAsync(user.Id, new SqlOSTotpEnrollmentStartRequest());
            await auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
                enrollment.EnrollmentToken,
                totp.GenerateCodeForTesting(enrollment.Secret)));
            cookie = await CreateSessionCookieAsync(authPageSession, user, organizationId: null, "password");
        }

        using var client = server.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var authorize = await client.GetAsync(BuildAuthorizeUrl("owned-client", "state-hosted-mfa"));

        authorize.StatusCode.Should().Be(HttpStatusCode.OK);
        authorize.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var html = await authorize.Content.ReadAsStringAsync();
        html.Should().Contain("action=\"/sqlos/auth/mfa/verify\"");
        html.Should().Contain("name=\"mfaToken\"");

        await using var verifyScope = server.App.Services.CreateAsyncScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSAuthorizationCode>().CountAsync(x => x.UserId == userId)).Should().Be(0);
        (await context.Set<SqlOSTemporaryToken>().CountAsync(x =>
            x.UserId == userId && x.Purpose == SqlOSAuthService.MfaChallengePurpose && x.ConsumedAt == null)).Should().Be(1);
    }

    [TestMethod]
    public async Task ThirdPartyCookieReuse_RequiresInteractionAndPromptNoneReturnsConsentRequired()
    {
        await using var server = await SilentSsoServer.CreateAsync(requireMfa: false);
        string cookie;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var authPageSession = scope.ServiceProvider.GetRequiredService<SqlOSAuthPageSessionService>();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Third Party Session User",
                $"third-party-session-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            cookie = await CreateSessionCookieAsync(authPageSession, user, organizationId: null, "password");
        }

        using var client = server.App.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        // A live session no longer forces a full re-login for third-party
        // clients; the interaction is now the consent approval itself.
        var interactive = await client.GetAsync(BuildAuthorizeUrl("third-party-client", "state-interactive"));
        interactive.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var interactionQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(interactive.Headers.Location!.Query);
        interactionQuery["view"].ToString().Should().Be("consent");
        interactionQuery.ContainsKey("mfaToken").Should().BeFalse();

        var silent = await client.GetAsync(BuildAuthorizeUrl("third-party-client", "state-none", "none"));
        silent.StatusCode.Should().Be(HttpStatusCode.Redirect);
        silent.Headers.Location!.Host.Should().Be("third-party.example.test");
        var errorQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(silent.Headers.Location.Query);
        errorQuery["error"].ToString().Should().Be("consent_required");
        errorQuery["state"].ToString().Should().Be("state-none");

        await using var verifyScope = server.App.Services.CreateAsyncScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task ExistingCookie_UsesAuthorizationRequestOrganizationInsteadOfSessionOrganization()
    {
        await using var server = await SilentSsoServer.CreateAsync(requireMfa: false);
        await using var scope = server.App.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
        var authorization = scope.ServiceProvider.GetRequiredService<SqlOSAuthorizationServerService>();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Organization Bound Session",
            $"organization-bound-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var sessionOrganization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Session Organization", null));
        var requestedOrganization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Requested Organization", null));
        await admin.CreateMembershipAsync(sessionOrganization.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        await admin.CreateMembershipAsync(requestedOrganization.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        var request = await authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
            "code",
            "owned-client",
            "https://owned.example.test/callback",
            "state-org",
            "openid",
            CodeChallenge,
            "S256",
            null,
            user.DefaultEmail,
            null,
            null,
            "hosted",
            null));
        request.OrganizationId = requestedOrganization.Id;
        await context.SaveChangesAsync();

        var completion = await authorization.CompleteAuthorizationRequestLoginAsync(
            request,
            user,
            "password",
            CreateHttpContext());

        completion.RedirectUrl.Should().NotBeNull();
        var code = await context.Set<SqlOSAuthorizationCode>().SingleAsync(x => x.AuthorizationRequestId == request.Id);
        code.OrganizationId.Should().Be(requestedOrganization.Id);
        code.OrganizationId.Should().NotBe(sessionOrganization.Id);
    }

    private static string BuildAuthorizeUrl(string clientId, string state, string? prompt = null)
    {
        var redirectUri = clientId == "owned-client"
            ? "https://owned.example.test/callback"
            : "https://third-party.example.test/callback";
        var values = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["scope"] = "openid profile",
            ["code_challenge"] = CodeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = prompt
        };
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/sqlos/auth/authorize", values);
    }

    private static async Task<string> CreateSessionCookieAsync(
        SqlOSAuthPageSessionService sessionService,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod)
    {
        var context = CreateHttpContext();
        await sessionService.SignInAsync(context, user, organizationId, authenticationMethod);
        var setCookie = context.Response.Headers.SetCookie.ToString();
        return setCookie.Split(';', 2)[0];
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("tests");
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.250");
        return context;
    }

    private sealed class SilentSsoServer : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public static async Task<SilentSsoServer> CreateAsync(bool requireMfa = true, bool headless = true)
        {
            await using var bootstrapContext = await AspireFixture.CreateIsolatedAuthContextAsync("SilentSso");
            var connectionString = bootstrapContext.Database.GetConnectionString()!;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSDbContext>(db => db.UseTestProvider(connectionString));
            builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
            {
                options.AuthServer.Issuer = "https://tests/sqlos/auth";
                options.AuthServer.BasePath = "/sqlos/auth";
                options.AuthServer.SeedBrowserClient("owned-client", "Owned Client", "https://owned.example.test/callback");
                options.AuthServer.SeedClient(client =>
                {
                    client.ClientId = "third-party-client";
                    client.Name = "Third Party Client";
                    client.RedirectUris = ["https://third-party.example.test/callback"];
                    client.ClientType = "public_pkce";
                    client.RequirePkce = true;
                    client.IsFirstParty = false;
                });
                options.AuthServer.Mfa.Enabled = true;
                options.AuthServer.Mfa.RequireForAllUsersByDefault = requireMfa;
                options.AuthServer.Mfa.AllowUserSelfEnrollmentByDefault = true;
                options.AuthServer.Mfa.RecoveryCodesEnabledByDefault = true;
                if (headless)
                {
                    options.AuthServer.UseHeadlessAuthPage(headlessOptions =>
                    {
                        headlessOptions.BuildUiUrl = route => Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                            "https://app.example.test/authorize",
                            new Dictionary<string, string?>
                            {
                                ["request"] = route.RequestId,
                                ["view"] = route.View,
                                ["error"] = route.Error,
                                ["pendingToken"] = route.PendingToken,
                                ["email"] = route.Email,
                                ["mfaToken"] = route.MfaToken
                            });
                    });
                }
            });
            builder.Services.RemoveAll<IHostedService>();

            var app = builder.Build();
            app.MapAuthServer("/sqlos/auth");
            await app.StartAsync();

            await using var scope = app.Services.CreateAsyncScope();
            var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var settings = scope.ServiceProvider.GetRequiredService<SqlOSSettingsService>();
            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.UpsertSeededAuthPageSettingsAsync();
            await settings.UpsertSeededMfaSettingsAsync();
            return new SilentSsoServer { App = app };
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
