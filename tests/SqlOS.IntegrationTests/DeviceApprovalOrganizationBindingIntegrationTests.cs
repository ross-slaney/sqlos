using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
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
/// Device approval must only accept device authorization requests, and the organization an
/// approver picks is authorized before it can influence anything. Discovery owns the request's
/// organization/connection pair. Regression coverage for #418 through the real hosted form and
/// headless API routes.
/// </summary>
[TestClass]
public sealed class DeviceApprovalOrganizationBindingIntegrationTests
{
    private const string CliClientId = "binding-cli";
    private const string ThirdPartyRedirectUri = "https://third-party.example.test/callback";
    private const string InvalidDeviceRequestMessage = "Device authorization request is invalid or expired.";
    private const string UnavailableOrganizationMessage = "The selected organization is not available to this user.";

    [DataTestMethod]
    [DataRow(false, DisplayName = "hosted form")]
    [DataRow(true, DisplayName = "headless API")]
    public async Task DeviceApprove_WithInteractiveAuthorizationRequest_FailsWithoutChangingTheRequest(bool headless)
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SignInAsync(fixture);
        var otherOrganization = await CreateOrganizationAsync(fixture, "Other");
        var thirdPartyClientId = await CreateThirdPartyClientAsync(fixture);

        // A non-first-party client makes the consent gate, which saves the request, reachable.
        var interactive = await fixture.StartAuthorizeAsync(
            "openid profile",
            clientId: thirdPartyClientId,
            redirectUri: ThirdPartyRedirectUri);

        using var response = headless
            ? await PostHeadlessApproveAsync(fixture, session.IssuerSessionCookie, interactive.RequestId, userCode: null, otherOrganization)
            : await PostHostedApproveAsync(
                fixture,
                interactive.AntiforgeryToken,
                interactive.AntiforgeryCookie,
                session.IssuerSessionCookie,
                interactive.RequestId,
                userCode: null,
                otherOrganization);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(InvalidDeviceRequestMessage);
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var stored = await db.Set<SqlOSAuthorizationRequest>().AsNoTracking().SingleAsync(x => x.Id == interactive.RequestId);
        stored.OrganizationId.Should().BeNull();
        stored.ResolvedOrganizationId.Should().BeNull();
        stored.PendingConsentUserId.Should().BeNull();
        stored.ResolvedAuthMethod.Should().BeNull();
        stored.CompletedAt.Should().BeNull();
        stored.CancelledAt.Should().BeNull();
        (await db.Set<SqlOSTemporaryToken>()
                .AnyAsync(x => x.Purpose == SqlOSTemporaryTokenKinds.AuthPageConsent.Purpose))
            .Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow(false, false, DisplayName = "hosted form, before sign-in completion")]
    [DataRow(false, true, DisplayName = "hosted form, after sign-in completion")]
    [DataRow(true, false, DisplayName = "headless API, before sign-in completion")]
    [DataRow(true, true, DisplayName = "headless API, after sign-in completion")]
    public async Task DeviceApprove_WithOrganizationTheUserDoesNotBelongTo_FailsAndLeavesRequestUnchanged(
        bool headless,
        bool signInCompleted)
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SignInAsync(fixture);
        var memberOrganization = await CreateOrganizationAsync(fixture, "Member", fixture.UserId);
        var otherOrganization = await CreateOrganizationAsync(fixture, "Other");
        var antiforgery = await fixture.StartAuthorizeAsync("openid");
        var userCode = await StartDeviceAuthorizationAsync(fixture);
        string requestId;
        if (signInCompleted)
        {
            // The approving browser completes sign-in for the device request first, which
            // resolves the user's only organization onto it.
            using var device = await GetAsync(
                fixture,
                QueryHelpers.AddQueryString("/sqlos/auth/device", "user_code", userCode),
                session.IssuerSessionCookie);
            requestId = await ReadDeviceApproveRequestIdAsync(device);
        }
        else
        {
            requestId = await CreateDeviceAuthorizationRequestAsync(fixture, userCode);
        }

        using var response = headless
            ? await PostHeadlessApproveAsync(fixture, session.IssuerSessionCookie, requestId, userCode, otherOrganization)
            : await PostHostedApproveAsync(
                fixture,
                antiforgery.AntiforgeryToken,
                antiforgery.AntiforgeryCookie,
                session.IssuerSessionCookie,
                requestId,
                userCode,
                otherOrganization);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(UnavailableOrganizationMessage);
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var stored = await db.Set<SqlOSAuthorizationRequest>().AsNoTracking().SingleAsync(x => x.Id == requestId);
        stored.OrganizationId.Should().BeNull();
        stored.CompletedAt.Should().BeNull();
        stored.ResolvedOrganizationId.Should().Be(signInCompleted ? memberOrganization : null);
        var deviceAuthorization = await db.Set<SqlOSDeviceAuthorization>().AsNoTracking()
            .SingleAsync(x => x.Id == stored.DeviceAuthorizationId);
        deviceAuthorization.Status.Should().Be(SqlOSDeviceAuthorizationService.PendingStatus);
        deviceAuthorization.ApprovedOrganizationId.Should().BeNull();
        deviceAuthorization.ApprovedUserId.Should().BeNull();
    }

    [DataTestMethod]
    [DataRow("hosted", DisplayName = "hosted form")]
    [DataRow("headless", DisplayName = "headless API")]
    [DataRow("headless-user-code", DisplayName = "headless API with only the user code")]
    public async Task DeviceApprove_MultiOrganizationUser_ApprovesIntoChosenOrganization(string surface)
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SignInAsync(fixture);
        await CreateOrganizationAsync(fixture, "First", fixture.UserId);
        var chosenOrganization = await CreateOrganizationAsync(fixture, "Chosen", fixture.UserId);
        var antiforgery = await fixture.StartAuthorizeAsync("openid");
        var userCode = await StartDeviceAuthorizationAsync(fixture);
        var requestId = surface == "headless-user-code"
            ? null
            : await CreateDeviceAuthorizationRequestAsync(fixture, userCode);

        using var response = surface == "hosted"
            ? await PostHostedApproveAsync(
                fixture,
                antiforgery.AntiforgeryToken,
                antiforgery.AntiforgeryCookie,
                session.IssuerSessionCookie,
                requestId,
                userCode,
                chosenOrganization)
            : await PostHeadlessApproveAsync(fixture, session.IssuerSessionCookie, requestId, userCode, chosenOrganization);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain(surface == "hosted" ? "CLI access approved." : "\"device-approved\"");
        await AssertApprovedIntoAsync(fixture, userCode, chosenOrganization);
    }

    [TestMethod]
    public async Task HostedDeviceApprove_AfterOrganizationSelection_ApprovesIntoOrganizationPickedOnApprovalPage()
    {
        await using var fixture = await CreateFixtureAsync();
        var session = await SignInAsync(fixture);
        var selectedAtSignIn = await CreateOrganizationAsync(fixture, "Selected at sign-in", fixture.UserId);
        var pickedOnApproval = await CreateOrganizationAsync(fixture, "Picked on approval", fixture.UserId);
        var antiforgery = await fixture.StartAuthorizeAsync("openid");
        var userCode = await StartDeviceAuthorizationAsync(fixture);

        using var chooser = await GetAsync(
            fixture,
            QueryHelpers.AddQueryString("/sqlos/auth/device", "user_code", userCode),
            session.IssuerSessionCookie,
            antiforgery.AntiforgeryCookie);
        var chooserHtml = await chooser.Content.ReadAsStringAsync();
        chooser.StatusCode.Should().Be(HttpStatusCode.OK, chooserHtml);
        chooserHtml.Should().Contain("/login/select-organization");
        var requestId = ExtractInputValue(chooserHtml, "requestId");

        using var selected = await PostHostedFormAsync(
            fixture,
            "/sqlos/auth/login/select-organization",
            new Dictionary<string, string>
            {
                ["requestId"] = requestId,
                ["pendingToken"] = ExtractInputValue(chooserHtml, "pendingToken"),
                ["organizationId"] = selectedAtSignIn
            },
            ExtractInputValue(chooserHtml, "__RequestVerificationToken"),
            antiforgery.AntiforgeryCookie,
            session.IssuerSessionCookie);
        (await ReadDeviceApproveRequestIdAsync(selected)).Should().Be(requestId);
        var sessionCookie = HostedAuthorizeTokenFixture.TryExtractCookie(selected, "sqlos_auth_page=")
            ?? session.IssuerSessionCookie;

        using var approvalPage = await GetAsync(
            fixture,
            QueryHelpers.AddQueryString("/sqlos/auth/device/approve", "request", requestId),
            sessionCookie,
            antiforgery.AntiforgeryCookie);
        var approvalHtml = await approvalPage.Content.ReadAsStringAsync();
        approvalPage.StatusCode.Should().Be(HttpStatusCode.OK, approvalHtml);
        approvalHtml.Should().Contain($"name=\"organizationId\" value=\"{pickedOnApproval}\"");

        using var approved = await PostHostedApproveAsync(
            fixture,
            ExtractInputValue(approvalHtml, "__RequestVerificationToken"),
            antiforgery.AntiforgeryCookie,
            sessionCookie,
            requestId,
            userCode,
            pickedOnApproval);
        var approvedHtml = await approved.Content.ReadAsStringAsync();

        approved.StatusCode.Should().Be(HttpStatusCode.OK, approvedHtml);
        approvedHtml.Should().Contain("CLI access approved.");
        await AssertApprovedIntoAsync(fixture, userCode, pickedOnApproval);
    }

    [TestMethod]
    public async Task HostedIdentify_ReidentifyAcrossOrganizations_KeepsConnectionAndOrganizationPaired()
    {
        await using var fixture = await CreateFixtureAsync();
        var firstDomain = $"first-{Guid.NewGuid():N}.test";
        var secondDomain = $"second-{Guid.NewGuid():N}.test";
        var (firstOrganization, firstConnection) = await CreateSsoOrganizationAsync(fixture, firstDomain);
        var (secondOrganization, secondConnection) = await CreateSsoOrganizationAsync(fixture, secondDomain);
        var started = await fixture.StartAuthorizeAsync("openid");

        using (var first = await PostIdentifyAsync(fixture, started, $"alice@{firstDomain}"))
        {
            first.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        await AssertRequestBindingAsync(fixture, started.RequestId, firstOrganization, firstConnection);

        using (var second = await PostIdentifyAsync(fixture, started, $"bob@{secondDomain}"))
        {
            second.StatusCode.Should().Be(HttpStatusCode.Redirect);
            second.Headers.Location!.AbsoluteUri.Should().StartWith($"https://idp-{secondDomain}/sso");
        }

        await AssertRequestBindingAsync(fixture, started.RequestId, secondOrganization, secondConnection);

        // Re-identifying with an address no SSO connection claims clears the pair instead of
        // leaving the second organization's binding behind for the next sign-in.
        using (var third = await PostIdentifyAsync(fixture, started, $"carol-{Guid.NewGuid():N}@example.test"))
        {
            var html = await third.Content.ReadAsStringAsync();
            third.StatusCode.Should().Be(HttpStatusCode.OK, html);
            html.Should().Contain("/login/password");
        }

        await AssertRequestBindingAsync(fixture, started.RequestId, organizationId: null, connectionId: null);
    }

    private static Task<HostedAuthorizeTokenFixture> CreateFixtureAsync()
        => HostedAuthorizeTokenFixture.CreateAsync(
            "DeviceOrgBinding",
            configure: options => options.AuthServer.SeedCliClient(CliClientId, "Binding CLI", "https://api.example.test", "openid"));

    private static async Task<HostedLoginResult> SignInAsync(HostedAuthorizeTokenFixture fixture)
        => await fixture.SubmitPasswordLoginWithSessionAsync(await fixture.StartAuthorizeAsync("openid"));

    private static async Task<string> CreateOrganizationAsync(
        HostedAuthorizeTokenFixture fixture,
        string name,
        string? memberUserId = null)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
        var organization = await admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"{name} {Guid.NewGuid():N}", null));
        if (memberUserId != null)
        {
            await admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(memberUserId, "member"));
        }

        return organization.Id;
    }

    private static async Task<string> CreateThirdPartyClientAsync(HostedAuthorizeTokenFixture fixture)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
        var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"third-party-{Guid.NewGuid():N}"[..24],
            "Third party",
            "sqlos-tests",
            [ThirdPartyRedirectUri],
            AllowedScopes: ["openid", "profile"],
            IsFirstParty: false));
        return client.ClientId;
    }

    private static async Task<(string OrganizationId, string ConnectionId)> CreateSsoOrganizationAsync(
        HostedAuthorizeTokenFixture fixture,
        string domain)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
        var organization = await admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"SSO {Guid.NewGuid():N}", null, domain));
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest($"CN={domain}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            organization.Id,
            $"{domain} SSO",
            $"urn:{domain}:idp",
            $"https://idp-{domain}/sso",
            certificate.ExportCertificatePem(),
            AutoProvisionUsers: true,
            AutoLinkByEmail: true,
            "email",
            "first_name",
            "last_name"));
        return (organization.Id, connection.Id);
    }

    private static async Task<string> StartDeviceAuthorizationAsync(HostedAuthorizeTokenFixture fixture)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var device = scope.ServiceProvider.GetRequiredService<SqlOSDeviceAuthorizationService>();
        var started = await device.StartAsync(
            new SqlOSDeviceAuthorizationStartRequest(CliClientId, "openid"),
            new DefaultHttpContext());
        return started.UserCode;
    }

    private static async Task<string> CreateDeviceAuthorizationRequestAsync(
        HostedAuthorizeTokenFixture fixture,
        string userCode)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var device = scope.ServiceProvider.GetRequiredService<SqlOSDeviceAuthorizationService>();
        return (await device.CreateOrGetAuthorizationRequestAsync(userCode, "hosted")).Id;
    }

    private static async Task AssertApprovedIntoAsync(
        HostedAuthorizeTokenFixture fixture,
        string userCode,
        string organizationId)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var deviceAuthorization = await db.Set<SqlOSDeviceAuthorization>().AsNoTracking()
            .SingleAsync(x => x.UserCode == userCode);
        deviceAuthorization.Status.Should().Be(SqlOSDeviceAuthorizationService.ApprovedStatus);
        deviceAuthorization.ApprovedUserId.Should().Be(fixture.UserId);
        deviceAuthorization.ApprovedOrganizationId.Should().Be(organizationId);
        var request = await db.Set<SqlOSAuthorizationRequest>().AsNoTracking()
            .SingleAsync(x => x.DeviceAuthorizationId == deviceAuthorization.Id);
        request.CompletedAt.Should().NotBeNull();
        request.OrganizationId.Should().BeNull();
    }

    private static async Task AssertRequestBindingAsync(
        HostedAuthorizeTokenFixture fixture,
        string requestId,
        string? organizationId,
        string? connectionId)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var request = await db.Set<SqlOSAuthorizationRequest>().AsNoTracking().SingleAsync(x => x.Id == requestId);
        request.OrganizationId.Should().Be(organizationId);
        request.ResolvedOrganizationId.Should().Be(organizationId);
        request.ConnectionId.Should().Be(connectionId);
        request.ResolvedConnectionId.Should().Be(connectionId);
    }

    private static Task<HttpResponseMessage> PostIdentifyAsync(
        HostedAuthorizeTokenFixture fixture,
        HostedAuthorizeStart started,
        string email)
        => PostHostedFormAsync(
            fixture,
            "/sqlos/auth/login/identify",
            new Dictionary<string, string>
            {
                ["requestId"] = started.RequestId,
                ["email"] = email
            },
            started.AntiforgeryToken,
            started.AntiforgeryCookie);

    private static Task<HttpResponseMessage> PostHostedApproveAsync(
        HostedAuthorizeTokenFixture fixture,
        string antiforgeryToken,
        string antiforgeryCookie,
        string sessionCookie,
        string? requestId,
        string? userCode,
        string organizationId)
        => PostHostedFormAsync(
            fixture,
            "/sqlos/auth/device/approve",
            new Dictionary<string, string>
            {
                ["requestId"] = requestId ?? string.Empty,
                ["userCode"] = userCode ?? string.Empty,
                ["organizationId"] = organizationId
            },
            antiforgeryToken,
            antiforgeryCookie,
            sessionCookie);

    private static async Task<HttpResponseMessage> PostHostedFormAsync(
        HostedAuthorizeTokenFixture fixture,
        string path,
        Dictionary<string, string> fields,
        string antiforgeryToken,
        params string[] cookies)
    {
        fields["__RequestVerificationToken"] = antiforgeryToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        request.Headers.TryAddWithoutValidation("Origin", HostedAuthorizeTokenFixture.TrustedOrigin);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostHeadlessApproveAsync(
        HostedAuthorizeTokenFixture fixture,
        string sessionCookie,
        string? requestId,
        string? userCode,
        string organizationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/headless/device/approve")
        {
            Content = JsonContent.Create(new { userCode, organizationId, requestId })
        };
        request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
        request.Headers.TryAddWithoutValidation("Origin", HostedAuthorizeTokenFixture.TrustedOrigin);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HostedAuthorizeTokenFixture fixture,
        string path,
        params string[] cookies)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<string> ReadDeviceApproveRequestIdAsync(HttpResponseMessage response)
    {
        var location = await HostedAuthorizeTokenFixture.ReadClientRedirectAsync(response);
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?', 2)[0];
        path.Should().EndWith("/device/approve");
        var query = QueryHelpers.ParseQuery(location.IsAbsoluteUri
            ? location.Query
            : "?" + location.OriginalString.Split('?', 2)[1]);
        return query["request"].ToString();
    }

    private static string ExtractInputValue(string html, string name)
    {
        var match = Regex.Match(
            html,
            $@"name=""{Regex.Escape(name)}"" value=""([^""]*)""",
            RegexOptions.CultureInvariant);
        match.Success.Should().BeTrue($"the page should include a '{name}' input");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
