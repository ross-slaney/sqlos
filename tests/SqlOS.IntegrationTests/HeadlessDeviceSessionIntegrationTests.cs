using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// A browser that already holds an issuer session reaches the device approval step in
/// headless mode without signing in again, both from the /device?user_code= link and from
/// a typed code, and still gets organization selection or MFA first when the hosted page
/// would (#324).
/// </summary>
[TestClass]
public sealed class HeadlessDeviceSessionIntegrationTests
{
    private const string CliClientId = "headless-device-cli";
    private const string SessionCookiePrefix = "sqlos_auth_page=";

    [TestMethod]
    public async Task SignedIn_DeviceLink_ShowsApproveAndApprovesWithoutSignIn()
    {
        await using var host = await HeadlessHost.CreateAsync();
        var session = await host.SignInAsync();
        var userCode = await host.StartDeviceAuthorizationAsync();

        var requestId = await host.OpenDeviceLinkAsync(userCode, session);
        var view = await host.GetRequestViewAsync(requestId, "device", session);

        view.View.Should().Be("device-approve");
        view.Email.Should().Be(host.Fixture.Email);
        view.RequestId.Should().Be(requestId);

        var approved = await host.PostAsync("/sqlos/auth/headless/device/approve", new { requestId }, session);
        approved.Root.GetProperty("type").GetString().Should().Be("view");
        new HeadlessView(approved.Root.GetProperty("viewModel")).View.Should().Be("device-approved");
        await host.AssertApprovedAsync(userCode, organizationId: null);
    }

    [TestMethod]
    public async Task SignedIn_TypedCode_ResolvesToApproveAndApprovesWithoutSignIn()
    {
        await using var host = await HeadlessHost.CreateAsync();
        var session = await host.SignInAsync();
        var userCode = await host.StartDeviceAuthorizationAsync();

        var resolved = new HeadlessView((await host.PostAsync("/sqlos/auth/headless/device/resolve", new { userCode }, session)).Root);

        resolved.View.Should().Be("device-approve");
        resolved.Email.Should().Be(host.Fixture.Email);
        resolved.RequestId.Should().NotBeNullOrWhiteSpace("the signed-in typed-code path continues on a tracked request");

        var approved = await host.PostAsync(
            "/sqlos/auth/headless/device/approve",
            new { requestId = resolved.RequestId },
            session);
        new HeadlessView(approved.Root.GetProperty("viewModel")).View.Should().Be("device-approved");
        await host.AssertApprovedAsync(userCode, organizationId: null);
    }

    [TestMethod]
    public async Task SignedOut_DeviceLinkAndTypedCode_StillShowTheDeviceStep()
    {
        await using var host = await HeadlessHost.CreateAsync();
        var userCode = await host.StartDeviceAuthorizationAsync();

        var requestId = await host.OpenDeviceLinkAsync(userCode, sessionCookie: null);
        var view = await host.GetRequestViewAsync(requestId, "device", sessionCookie: null);
        view.View.Should().Be("device");
        view.Email.Should().BeNull();

        var resolved = new HeadlessView((await host.PostAsync("/sqlos/auth/headless/device/resolve", new { userCode }, sessionCookie: null)).Root);
        resolved.View.Should().Be("device");
        resolved.RequestId.Should().BeNull("an anonymous typed code stays on the standalone device step");

        await host.AssertPendingAsync(userCode);
    }

    [DataTestMethod]
    [DataRow("expired", DisplayName = "expired session")]
    [DataRow("revoked", DisplayName = "revoked session family")]
    public async Task DeadSession_DeviceLinkAndTypedCode_PromptForSignIn(string deadness)
    {
        await using var host = await HeadlessHost.CreateAsync();
        var session = await host.SignInAsync();
        await host.KillSessionAsync(deadness);
        var userCode = await host.StartDeviceAuthorizationAsync();

        var requestId = await host.OpenDeviceLinkAsync(userCode, session);
        (await host.GetRequestViewAsync(requestId, "device", session)).View.Should().Be("device");

        var resolved = new HeadlessView((await host.PostAsync("/sqlos/auth/headless/device/resolve", new { requestId }, session)).Root);
        resolved.View.Should().Be("device");

        await host.AssertPendingAsync(userCode);
        await host.AssertNotSignedInAsync(requestId);
    }

    [DataTestMethod]
    [DataRow(false, DisplayName = "device link")]
    [DataRow(true, DisplayName = "typed code")]
    public async Task SignedIn_MultiOrganizationUser_SelectsOrganizationBeforeApprove(bool typedCode)
    {
        await using var host = await HeadlessHost.CreateAsync();
        var session = await host.SignInAsync();
        var first = await host.CreateOrganizationAsync("First");
        var second = await host.CreateOrganizationAsync("Second");
        var userCode = await host.StartDeviceAuthorizationAsync();

        var view = typedCode
            ? new HeadlessView((await host.PostAsync("/sqlos/auth/headless/device/resolve", new { userCode }, session)).Root)
            : await host.GetRequestViewAsync(await host.OpenDeviceLinkAsync(userCode, session), "device", session);

        view.View.Should().Be("organization");
        view.PendingToken.Should().NotBeNullOrWhiteSpace();
        view.OrganizationIds.Should().BeEquivalentTo([first, second]);

        var selected = await host.PostAsync(
            "/sqlos/auth/headless/organization/select",
            new { pendingToken = view.PendingToken, organizationId = second },
            session);
        selected.Root.GetProperty("type").GetString().Should().Be("redirect");
        selected.Root.GetProperty("redirectUrl").GetString().Should().Contain("/device/approve?request=");
        session = selected.SessionCookie ?? session;

        var approved = await host.PostAsync("/sqlos/auth/headless/device/approve", new { requestId = view.RequestId }, session);
        new HeadlessView(approved.Root.GetProperty("viewModel")).View.Should().Be("device-approved");
        await host.AssertApprovedAsync(userCode, second);
    }

    [DataTestMethod]
    [DataRow(false, DisplayName = "device link")]
    [DataRow(true, DisplayName = "typed code")]
    public async Task SignedIn_OrganizationRequiresMfa_VerifiesMfaBeforeApprove(bool typedCode)
    {
        await using var host = await HeadlessHost.CreateAsync();
        var session = await host.SignInAsync();
        var organization = await host.CreateOrganizationAsync("Requires MFA", requireMfa: true);
        var secret = await host.EnrollTotpAsync();
        var userCode = await host.StartDeviceAuthorizationAsync();

        var view = typedCode
            ? new HeadlessView((await host.PostAsync("/sqlos/auth/headless/device/resolve", new { userCode }, session)).Root)
            : await host.GetRequestViewAsync(await host.OpenDeviceLinkAsync(userCode, session), "device", session);

        view.View.Should().Be("mfa", "the organization's MFA policy adds a step before approval");
        view.MfaToken.Should().NotBeNullOrWhiteSpace();
        await host.AssertPendingAsync(userCode);

        var verified = await host.PostAsync(
            "/sqlos/auth/headless/mfa/verify",
            new { requestId = view.RequestId, mfaToken = view.MfaToken, code = await host.NextTotpCodeAsync(secret) },
            session);
        verified.Root.GetProperty("type").GetString().Should().Be("redirect");
        verified.Root.GetProperty("redirectUrl").GetString().Should().Contain("/device/approve?request=");
        session = verified.SessionCookie ?? session;

        var approved = await host.PostAsync("/sqlos/auth/headless/device/approve", new { requestId = view.RequestId }, session);
        new HeadlessView(approved.Root.GetProperty("viewModel")).View.Should().Be("device-approved");
        await host.AssertApprovedAsync(userCode, organization);
    }

    /// <summary>A hosted SqlOS application that sends /device and /authorize to a headless UI.</summary>
    private sealed class HeadlessHost : IAsyncDisposable
    {
        private HeadlessHost(HostedAuthorizeTokenFixture fixture)
        {
            Fixture = fixture;
        }

        public HostedAuthorizeTokenFixture Fixture { get; }

        public static async Task<HeadlessHost> CreateAsync()
            => new(await HostedAuthorizeTokenFixture.CreateAsync(
                "HeadlessDeviceSession",
                configure: options =>
                {
                    options.AuthServer.Mfa.Enabled = true;
                    options.AuthServer.Mfa.AllowUserSelfEnrollmentByDefault = true;
                    options.AuthServer.SeedCliClient(CliClientId, "Headless CLI", "https://api.example.test", "openid");
                    options.AuthServer.UseHeadlessAuthPage(headless => headless.BuildUiUrl = context =>
                        $"https://app.example.test/auth?request={Uri.EscapeDataString(context.RequestId ?? string.Empty)}&view={context.View}");
                }));

        /// <summary>Signs the fixture user in through a headless browser flow and returns the issuer session cookie.</summary>
        public async Task<string> SignInAsync()
        {
            using var authorize = await Fixture.AuthorizeRawAsync(
                HttpMethod.Get,
                "openid",
                $"state-{Guid.NewGuid():N}",
                new Dictionary<string, string>
                {
                    ["client_id"] = HostedAuthorizeTokenFixture.ClientId,
                    ["redirect_uri"] = HostedAuthorizeTokenFixture.RedirectUri
                });
            authorize.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var requestId = QueryHelpers.ParseQuery(authorize.Response.Headers.Location!.Query)["request"].ToString();

            var login = await PostAsync(
                "/sqlos/auth/headless/password/login",
                new { requestId, email = Fixture.Email, password = HostedAuthorizeTokenFixture.Password },
                sessionCookie: null);
            login.Root.GetProperty("type").GetString().Should().Be("redirect");
            return login.SessionCookie ?? throw new InvalidOperationException("Headless sign-in did not set an issuer session cookie.");
        }

        public async Task<string> StartDeviceAuthorizationAsync()
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var started = await scope.ServiceProvider.GetRequiredService<SqlOSDeviceAuthorizationService>().StartAsync(
                new SqlOSDeviceAuthorizationStartRequest(CliClientId, "openid"),
                new DefaultHttpContext());
            return started.UserCode;
        }

        /// <summary>Follows GET /device?user_code= to the headless UI and returns the request it hands over.</summary>
        public async Task<string> OpenDeviceLinkAsync(string userCode, string? sessionCookie)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                QueryHelpers.AddQueryString("/sqlos/auth/device", "user_code", userCode));
            AddCookie(request, sessionCookie);
            using var response = await Fixture.Client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
            var location = response.Headers.Location!;
            location.GetLeftPart(UriPartial.Path).Should().Be("https://app.example.test/auth");
            var query = QueryHelpers.ParseQuery(location.Query);
            query["view"].ToString().Should().Be("device");
            return query["request"].ToString();
        }

        public async Task<HeadlessView> GetRequestViewAsync(string requestId, string view, string? sessionCookie)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                QueryHelpers.AddQueryString($"/sqlos/auth/headless/requests/{Uri.EscapeDataString(requestId)}", "view", view));
            AddCookie(request, sessionCookie);
            using var response = await Fixture.Client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, json);
            return new HeadlessView(JsonDocument.Parse(json).RootElement.Clone());
        }

        public async Task<HeadlessResponse> PostAsync(string path, object body, string? sessionCookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body)
            };
            AddCookie(request, sessionCookie);
            request.Headers.TryAddWithoutValidation("Origin", HostedAuthorizeTokenFixture.TrustedOrigin);
            using var response = await Fixture.Client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, "{0} answered: {1}", path, json);
            return new HeadlessResponse(
                JsonDocument.Parse(json).RootElement.Clone(),
                HostedAuthorizeTokenFixture.TryExtractCookie(response, SessionCookiePrefix));
        }

        public async Task KillSessionAsync(string deadness)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            var now = DateTime.UtcNow;
            if (deadness == "expired")
            {
                var tokens = await db.Set<SqlOSTemporaryToken>()
                    .Where(x => x.UserId == Fixture.UserId && x.Purpose == SqlOSAuthLifecyclePolicy.IssuerSessionPurpose)
                    .ToListAsync();
                tokens.Should().NotBeEmpty();
                tokens.ForEach(token => token.ExpiresAt = now.AddMinutes(-1));
            }
            else
            {
                var families = await db.Set<SqlOSIssuerSessionFamily>()
                    .Where(x => x.UserId == Fixture.UserId)
                    .ToListAsync();
                families.Should().NotBeEmpty();
                families.ForEach(family =>
                {
                    family.RevokedAt = now;
                    family.RevocationReason = "test";
                });
            }

            await db.SaveChangesAsync();
        }

        public async Task<string> CreateOrganizationAsync(string name, bool requireMfa = false)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"{name} {Guid.NewGuid():N}", null));
            await admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(Fixture.UserId, "member"));
            if (requireMfa)
            {
                await scope.ServiceProvider.GetRequiredService<SqlOSSettingsService>().UpdateOrganizationMfaPolicyAsync(
                    organization.Id,
                    new SqlOSUpdateOrganizationMfaPolicyRequest(
                        IsEnabled: true,
                        RequireMfaForAllUsers: true,
                        RequireMfaForOwnersAndAdmins: true,
                        UserSelfEnrollmentEnabled: true,
                        RecoveryCodesEnabled: true,
                        RequiredRoles: null,
                        AvailableFactors: null));
            }

            return organization.Id;
        }

        public async Task<string> EnrollTotpAsync()
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            var totp = scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>();
            var enrollment = await auth.StartTotpEnrollmentAsync(Fixture.UserId, new SqlOSTotpEnrollmentStartRequest("Headless authenticator"));
            await auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
                enrollment.EnrollmentToken,
                totp.GenerateCodeForTesting(enrollment.Secret)));
            return enrollment.Secret;
        }

        public async Task<string> NextTotpCodeAsync(string secret)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            // The enrollment consumed the current time step; the next one is still inside the window.
            return scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>()
                .GenerateCodeForTesting(secret, DateTimeOffset.UtcNow.AddSeconds(30));
        }

        public async Task AssertApprovedAsync(string userCode, string? organizationId)
        {
            var deviceAuthorization = await ReadDeviceAuthorizationAsync(userCode);
            deviceAuthorization.Status.Should().Be(SqlOSDeviceAuthorizationService.ApprovedStatus);
            deviceAuthorization.ApprovedUserId.Should().Be(Fixture.UserId);
            deviceAuthorization.ApprovedOrganizationId.Should().Be(organizationId);
        }

        public async Task AssertPendingAsync(string userCode)
        {
            var deviceAuthorization = await ReadDeviceAuthorizationAsync(userCode);
            deviceAuthorization.Status.Should().Be(SqlOSDeviceAuthorizationService.PendingStatus);
            deviceAuthorization.ApprovedUserId.Should().BeNull();
        }

        public async Task AssertNotSignedInAsync(string requestId)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var request = await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>()
                .Set<SqlOSAuthorizationRequest>()
                .AsNoTracking()
                .SingleAsync(x => x.Id == requestId);
            request.ResolvedAuthMethod.Should().BeNull("a dead session must not complete sign-in for the device request");
        }

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();

        private async Task<SqlOSDeviceAuthorization> ReadDeviceAuthorizationAsync(string userCode)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>()
                .Set<SqlOSDeviceAuthorization>()
                .AsNoTracking()
                .SingleAsync(x => x.UserCode == userCode);
        }

        private static void AddCookie(HttpRequestMessage request, string? sessionCookie)
        {
            if (sessionCookie != null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            }
        }
    }

    private sealed record HeadlessResponse(JsonElement Root, string? SessionCookie);

    private sealed record HeadlessView(JsonElement Root)
    {
        public string? View => String("view");

        public string? RequestId => String("requestId");

        public string? Email => String("email");

        public string? PendingToken => String("pendingToken");

        public string? MfaToken => String("mfaToken");

        public IReadOnlyList<string> OrganizationIds
            => Root.GetProperty("organizationSelection")
                .EnumerateArray()
                .Select(organization => organization.GetProperty("id").GetString()!)
                .ToList();

        private string? String(string property)
            => Root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
