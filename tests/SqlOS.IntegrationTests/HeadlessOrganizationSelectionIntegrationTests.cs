using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
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
/// A headless user who belongs to more than one organization picks one with
/// POST {headless}/organization/select. The selection that issues the authorization code must
/// hand the client redirect back instead of re-reading the request it just completed (#459).
/// </summary>
[TestClass]
public sealed class HeadlessOrganizationSelectionIntegrationTests
{
    private const string Password = HostedAuthorizeTokenFixture.Password;
    private const string ThirdPartyClientId = "headless-org-third-party";
    private const string ThirdPartyRedirectUri = "https://third-party.example.test/callback";

    [TestMethod]
    public async Task MultiOrganizationUser_SelectsAnOrganization_AndReceivesTheClientRedirect()
    {
        await using var host = await HeadlessHost.CreateAsync();
        var user = await host.CreateUserAsync("two-orgs");
        var first = await host.CreateOrganizationAsync("First", user.Id);
        var second = await host.CreateOrganizationAsync("Second", user.Id);
        var flow = await host.StartAsync();

        var login = await host.PostAsync("/sqlos/auth/headless/password/login", new { requestId = flow.RequestId, email = user.Email, password = Password });
        login.View.Should().Be("organization");
        login.OrganizationIds.Should().BeEquivalentTo([first, second]);

        var selected = await host.PostAsync("/sqlos/auth/headless/organization/select", new { pendingToken = login.PendingToken, organizationId = second });

        selected.Type.Should().Be("redirect");
        var redirect = new Uri(selected.RedirectUrl!);
        redirect.GetLeftPart(UriPartial.Path).Should().Be(HostedAuthorizeTokenFixture.RedirectUri);
        var query = QueryHelpers.ParseQuery(redirect.Query);
        query["state"].ToString().Should().Be(flow.State);
        using var tokens = await host.Fixture.ExchangeAuthorizationCodeAsync(query["code"].ToString(), flow.CodeVerifier);
        ReadClaim(tokens, "org_id").Should().Be(second, "the code carries the organization the user picked");
        ReadClaim(tokens, "sub").Should().Be(user.Id);
    }

    [TestMethod]
    public async Task MultiOrganizationUser_SelectingAnOrganizationThatRequiresMfa_ContinuesToTheMfaStep()
    {
        await using var host = await HeadlessHost.CreateAsync();
        var user = await host.CreateUserAsync("mfa-org");
        await host.CreateOrganizationAsync("Plain", user.Id);
        var mfaOrganization = await host.CreateOrganizationAsync("Requires MFA", user.Id, requireMfa: true);
        var secret = await host.EnrollTotpAsync(user.Id);
        var flow = await host.StartAsync();

        var login = await host.PostAsync("/sqlos/auth/headless/password/login", new { requestId = flow.RequestId, email = user.Email, password = Password });
        login.View.Should().Be("organization");

        var selected = await host.PostAsync("/sqlos/auth/headless/organization/select", new { pendingToken = login.PendingToken, organizationId = mfaOrganization });
        selected.Type.Should().Be("view", "the organization's MFA policy adds a step before the code is issued");
        selected.View.Should().Be("mfa");
        selected.MfaToken.Should().NotBeNullOrWhiteSpace();

        var verified = await host.PostAsync("/sqlos/auth/headless/mfa/verify", new
        {
            requestId = flow.RequestId,
            mfaToken = selected.MfaToken,
            code = await host.NextTotpCodeAsync(secret)
        });
        verified.Type.Should().Be("redirect");
        var query = QueryHelpers.ParseQuery(new Uri(verified.RedirectUrl!).Query);
        using var tokens = await host.Fixture.ExchangeAuthorizationCodeAsync(query["code"].ToString(), flow.CodeVerifier);
        ReadClaim(tokens, "org_id").Should().Be(mfaOrganization);
    }

    [TestMethod]
    public async Task ThirdPartyClient_ConsentThenOrganizationSelection_ReceivesTheClientRedirect()
    {
        await using var host = await HeadlessHost.CreateAsync();
        await host.CreateThirdPartyClientAsync();
        var user = await host.CreateUserAsync("consent-org");
        await host.CreateOrganizationAsync("Alpha", user.Id);
        var beta = await host.CreateOrganizationAsync("Beta", user.Id);
        var flow = await host.StartAsync(ThirdPartyClientId, ThirdPartyRedirectUri);

        var login = await host.PostAsync("/sqlos/auth/headless/password/login", new { requestId = flow.RequestId, email = user.Email, password = Password });
        login.View.Should().Be("consent");

        var consented = await host.PostAsync("/sqlos/auth/headless/consent/approve", new { requestId = flow.RequestId, consentToken = login.ConsentToken });
        consented.View.Should().Be("organization");

        var selected = await host.PostAsync("/sqlos/auth/headless/organization/select", new { pendingToken = consented.PendingToken, organizationId = beta });
        selected.Type.Should().Be("redirect");
        var redirect = new Uri(selected.RedirectUrl!);
        redirect.GetLeftPart(UriPartial.Path).Should().Be(ThirdPartyRedirectUri);
        var query = QueryHelpers.ParseQuery(redirect.Query);
        using var tokens = await host.Fixture.ExchangeAuthorizationCodeAsync(
            query["code"].ToString(),
            flow.CodeVerifier,
            ThirdPartyClientId,
            ThirdPartyRedirectUri);
        ReadClaim(tokens, "org_id").Should().Be(beta);
    }

    [TestMethod]
    public async Task OrganizationSelection_ReplayedAfterTheCodeWasIssued_IsRejected()
    {
        await using var host = await HeadlessHost.CreateAsync();
        var user = await host.CreateUserAsync("replay");
        var first = await host.CreateOrganizationAsync("Replay One", user.Id);
        await host.CreateOrganizationAsync("Replay Two", user.Id);
        var flow = await host.StartAsync();
        var login = await host.PostAsync("/sqlos/auth/headless/password/login", new { requestId = flow.RequestId, email = user.Email, password = Password });

        var selected = await host.PostAsync("/sqlos/auth/headless/organization/select", new { pendingToken = login.PendingToken, organizationId = first });
        selected.Type.Should().Be("redirect");

        using var replay = await host.Fixture.Client.PostAsJsonAsync(
            "/sqlos/auth/headless/organization/select",
            new { pendingToken = login.PendingToken, organizationId = first });
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the pending selection is single use");
        (await host.CountAuthorizationCodesAsync(user.Id)).Should().Be(1);
    }

    private static string? ReadClaim(JsonDocument tokens, string claim)
    {
        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
        var payload = accessToken.Split('.')[1];
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(payload)));
        return json.RootElement.TryGetProperty(claim, out var value) ? value.GetString() : null;
    }

    /// <summary>A hosted SqlOS application that sends /authorize to a headless UI.</summary>
    private sealed class HeadlessHost : IAsyncDisposable
    {
        private HeadlessHost(HostedAuthorizeTokenFixture fixture)
        {
            Fixture = fixture;
        }

        public HostedAuthorizeTokenFixture Fixture { get; }

        public static async Task<HeadlessHost> CreateAsync()
            => new(await HostedAuthorizeTokenFixture.CreateAsync(
                "HeadlessOrgSelect",
                configure: options =>
                {
                    options.AuthServer.Mfa.Enabled = true;
                    options.AuthServer.Mfa.AllowUserSelfEnrollmentByDefault = true;
                    options.AuthServer.UseHeadlessAuthPage(headless => headless.BuildUiUrl = context =>
                        $"https://app.example.test/auth?request={Uri.EscapeDataString(context.RequestId ?? string.Empty)}&view={context.View}");
                }));

        public async Task<HeadlessFlow> StartAsync(
            string clientId = HostedAuthorizeTokenFixture.ClientId,
            string redirectUri = HostedAuthorizeTokenFixture.RedirectUri)
        {
            var state = $"state-{Guid.NewGuid():N}";
            using var authorize = await Fixture.AuthorizeRawAsync(
                HttpMethod.Get,
                "openid profile email",
                state,
                new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["redirect_uri"] = redirectUri
                });
            authorize.Response.StatusCode.Should().Be(HttpStatusCode.Redirect, "a headless application renders sign-in in its own UI");
            var requestId = QueryHelpers.ParseQuery(authorize.Response.Headers.Location!.Query)["request"].ToString();
            requestId.Should().NotBeNullOrWhiteSpace();
            return new HeadlessFlow(requestId, state, authorize.CodeVerifier);
        }

        public async Task<HeadlessResult> PostAsync(string path, object body)
        {
            using var response = await Fixture.Client.PostAsJsonAsync(path, body);
            var json = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, "{0} answered: {1}", path, json);
            return new HeadlessResult(JsonDocument.Parse(json).RootElement.Clone());
        }

        public async Task<TestUser> CreateUserAsync(string label)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var email = $"{label}-{Guid.NewGuid():N}@example.test";
            var user = await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .CreateUserAsync(new SqlOSCreateUserRequest($"Headless {label}", email, Password));
            return new TestUser(user.Id, email);
        }

        public async Task<string> CreateOrganizationAsync(string name, string memberUserId, bool requireMfa = false)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"{name} {Guid.NewGuid():N}", null));
            await admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(memberUserId, "member"));
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

        public async Task CreateThirdPartyClientAsync()
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>().CreateClientAsync(
                new SqlOSCreateClientRequest(ThirdPartyClientId, "Headless Third Party", "sqlos", [ThirdPartyRedirectUri]));
        }

        public async Task<string> EnrollTotpAsync(string userId)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            var totp = scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>();
            var enrollment = await auth.StartTotpEnrollmentAsync(userId, new SqlOSTotpEnrollmentStartRequest("Headless authenticator"));
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

        public async Task<int> CountAuthorizationCodesAsync(string userId)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>()
                .Set<SqlOSAuthorizationCode>()
                .CountAsync(x => x.UserId == userId);
        }

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    private sealed record TestUser(string Id, string Email);

    private sealed record HeadlessFlow(string RequestId, string State, string CodeVerifier);

    private sealed record HeadlessResult(JsonElement Root)
    {
        public string Type => Root.GetProperty("type").GetString()!;

        public string? RedirectUrl => Root.GetProperty("redirectUrl").GetString();

        public string? View => ViewString("view");

        public string? PendingToken => ViewString("pendingToken");

        public string? ConsentToken => ViewString("consentToken");

        public string? MfaToken => ViewString("mfaToken");

        public IReadOnlyList<string> OrganizationIds
            => Root.GetProperty("viewModel").GetProperty("organizationSelection")
                .EnumerateArray()
                .Select(organization => organization.GetProperty("id").GetString()!)
                .ToList();

        private string? ViewString(string property)
            => Root.TryGetProperty("viewModel", out var viewModel)
                && viewModel.ValueKind == JsonValueKind.Object
                && viewModel.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
    }
}
