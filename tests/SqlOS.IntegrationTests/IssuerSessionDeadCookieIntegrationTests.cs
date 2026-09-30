using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// A revoked or cleaned-up issuer session cookie counts as signed out (#434): /authorize
/// deletes it and shows the sign-in page, and a fresh credential sign-in that still presents
/// it succeeds in a new family. Silent reuse of the dead cookie still fails closed (#359).
/// </summary>
[TestClass]
public sealed class IssuerSessionDeadCookieIntegrationTests
{
    private const string CliClientId = "dead-cookie-cli";

    [DataTestMethod]
    [DataRow(false, DisplayName = "revoked row still present")]
    [DataRow(true, DisplayName = "row deleted by startup cleanup")]
    public async Task HostedSignIn_AfterPasswordReset_ClearsDeadCookieAndIssuesCodeInNewFamily(bool cleanUp)
    {
        await using var fixture = await CreateFixtureAsync();
        var deadCookie = await SignInAsync(fixture);
        var deadFamily = await FamilyOfAsync(fixture, deadCookie);
        await ResetPasswordAsync(fixture);
        if (cleanUp)
        {
            await CleanUpTemporaryTokensAsync(fixture);
        }

        // /authorize with the dead cookie shows the sign-in page and deletes the cookie.
        using var authorize = await fixture.AuthorizeWithSessionAsync("openid", deadCookie);
        var html = await authorize.Response.Content.ReadAsStringAsync();
        authorize.Response.StatusCode.Should().Be(HttpStatusCode.OK, html);
        html.Should().Contain("/login/password");
        AssertDeletesIssuerCookie(authorize.Response);

        // A browser that still presents the dead cookie signs in fresh with its password.
        using var login = await PostHostedFormAsync(
            fixture,
            "/sqlos/auth/login/password",
            new Dictionary<string, string>
            {
                ["requestId"] = ExtractInputValue(html, "requestId"),
                ["email"] = fixture.Email,
                ["password"] = HostedAuthorizeTokenFixture.Password
            },
            ExtractInputValue(html, "__RequestVerificationToken"),
            HostedAuthorizeTokenFixture.TryExtractCookie(authorize.Response, "sqlos_auth_page_csrf_")!,
            deadCookie);
        var code = await ReadCodeAsync(login);
        using var tokens = await fixture.ExchangeAuthorizationCodeAsync(code, authorize.CodeVerifier);
        tokens.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();

        var freshCookie = HostedAuthorizeTokenFixture.TryExtractCookie(login, "sqlos_auth_page=");
        freshCookie.Should().NotBeNullOrWhiteSpace();
        freshCookie.Should().NotBe(deadCookie);
        var freshFamily = await FamilyOfAsync(fixture, freshCookie!);
        freshFamily.Id.Should().NotBe(deadFamily.Id);
        freshFamily.RevokedAt.Should().BeNull();
        var revoked = await FindFamilyAsync(fixture, deadFamily.Id);
        revoked.RevokedAt.Should().NotBeNull();
        revoked.RevocationReason.Should().Be("password_reset");

        // The revoked family is not revived: its cookie still cannot be reused silently.
        using var replay = await fixture.AuthorizeWithSessionAsync("openid", deadCookie, prompt: "none");
        AssertLoginRequired(replay.Response);

        using var reuse = await fixture.AuthorizeWithSessionAsync("openid", freshCookie!, prompt: "none");
        var reuseQuery = QueryHelpers.ParseQuery(reuse.Response.Headers.Location!.Query);
        reuseQuery["code"].ToString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task HeadlessPasswordLogin_WithRevokedCookie_IssuesCodeInNewFamily()
    {
        await using var fixture = await CreateFixtureAsync();
        var deadCookie = await SignInAsync(fixture);
        var deadFamily = await FamilyOfAsync(fixture, deadCookie);
        await LogoutAllAsync(fixture);
        var started = await fixture.StartAuthorizeAsync("openid");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/headless/password/login")
        {
            Content = JsonContent.Create(new
            {
                requestId = started.RequestId,
                email = fixture.Email,
                password = HostedAuthorizeTokenFixture.Password
            })
        };
        request.Headers.TryAddWithoutValidation("Cookie", deadCookie);
        using var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var result = JsonDocument.Parse(body);
        var redirectUrl = result.RootElement.GetProperty("redirectUrl").GetString();
        redirectUrl.Should().NotBeNullOrWhiteSpace($"the headless login should redirect with a code, not {body}");
        var code = QueryHelpers.ParseQuery(new Uri(redirectUrl!).Query)["code"].ToString();
        code.Should().NotBeNullOrWhiteSpace();
        using var tokens = await fixture.ExchangeAuthorizationCodeAsync(code, started.CodeVerifier);
        tokens.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        var freshCookie = HostedAuthorizeTokenFixture.TryExtractCookie(response, "sqlos_auth_page=");
        freshCookie.Should().NotBeNullOrWhiteSpace();
        (await FamilyOfAsync(fixture, freshCookie!)).Id.Should().NotBe(deadFamily.Id);
        (await FindFamilyAsync(fixture, deadFamily.Id)).RevokedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task HostedDeviceSignIn_WithRevokedCookie_ResolvesAndApprovesTheDevice()
    {
        await using var fixture = await CreateFixtureAsync();
        var deadCookie = await SignInAsync(fixture);
        await LogoutAllAsync(fixture);
        var userCode = await StartDeviceAuthorizationAsync(fixture);

        using var devicePage = await GetAsync(
            fixture,
            QueryHelpers.AddQueryString("/sqlos/auth/device", "user_code", userCode),
            deadCookie);
        var deviceHtml = await devicePage.Content.ReadAsStringAsync();
        devicePage.StatusCode.Should().Be(HttpStatusCode.OK, deviceHtml);
        var requestId = ExtractInputValue(deviceHtml, "requestId");
        var csrfCookie = HostedAuthorizeTokenFixture.TryExtractCookie(devicePage, "sqlos_auth_page_csrf_")!;

        using var login = await PostHostedFormAsync(
            fixture,
            "/sqlos/auth/login/password",
            new Dictionary<string, string>
            {
                ["requestId"] = requestId,
                ["email"] = fixture.Email,
                ["password"] = HostedAuthorizeTokenFixture.Password
            },
            ExtractInputValue(deviceHtml, "__RequestVerificationToken"),
            csrfCookie,
            deadCookie);
        var location = await HostedAuthorizeTokenFixture.ReadClientRedirectAsync(login);
        location.OriginalString.Should().Contain("/device/approve");
        var freshCookie = HostedAuthorizeTokenFixture.TryExtractCookie(login, "sqlos_auth_page=");
        freshCookie.Should().NotBeNullOrWhiteSpace();

        using var approvalPage = await GetAsync(fixture, location.OriginalString, freshCookie!, csrfCookie);
        var approvalHtml = await approvalPage.Content.ReadAsStringAsync();
        approvalPage.StatusCode.Should().Be(HttpStatusCode.OK, approvalHtml);
        using var approved = await PostHostedFormAsync(
            fixture,
            "/sqlos/auth/device/approve",
            new Dictionary<string, string>
            {
                ["requestId"] = requestId,
                ["userCode"] = userCode
            },
            ExtractInputValue(approvalHtml, "__RequestVerificationToken"),
            csrfCookie,
            freshCookie!);
        var approvedHtml = await approved.Content.ReadAsStringAsync();
        approved.StatusCode.Should().Be(HttpStatusCode.OK, approvedHtml);
        approvedHtml.Should().Contain("CLI access approved.");

        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var deviceAuthorization = await db.Set<SqlOSDeviceAuthorization>().AsNoTracking().SingleAsync(x => x.UserCode == userCode);
        deviceAuthorization.Status.Should().Be(SqlOSDeviceAuthorizationService.ApprovedStatus);
        deviceAuthorization.ApprovedUserId.Should().Be(fixture.UserId);
    }

    [TestMethod]
    public async Task HostedSignup_WithAnotherUsersRevokedCookie_StartsTheNewUsersOwnFamily()
    {
        await using var fixture = await CreateFixtureAsync();
        var deadCookie = await SignInAsync(fixture);
        var deadFamily = await FamilyOfAsync(fixture, deadCookie);
        await LogoutAllAsync(fixture);
        var started = await fixture.StartAuthorizeAsync("openid");
        var newEmail = $"new-user-{Guid.NewGuid():N}@example.test";

        using var signup = await PostHostedFormAsync(
            fixture,
            "/sqlos/auth/signup/submit",
            new Dictionary<string, string>
            {
                ["requestId"] = started.RequestId,
                ["displayName"] = "New User",
                ["email"] = newEmail,
                ["password"] = HostedAuthorizeTokenFixture.Password
            },
            started.AntiforgeryToken,
            started.AntiforgeryCookie,
            deadCookie);
        var code = await ReadCodeAsync(signup);
        using var tokens = await fixture.ExchangeAuthorizationCodeAsync(code, started.CodeVerifier);
        tokens.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();

        var freshCookie = HostedAuthorizeTokenFixture.TryExtractCookie(signup, "sqlos_auth_page=");
        freshCookie.Should().NotBeNullOrWhiteSpace();
        var freshFamily = await FamilyOfAsync(fixture, freshCookie!);
        freshFamily.Id.Should().NotBe(deadFamily.Id);
        freshFamily.UserId.Should().NotBe(fixture.UserId);
        (await FindFamilyAsync(fixture, deadFamily.Id)).RevokedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task RevokedCookie_CannotMintACodeOrApproveADeviceWithoutACredential()
    {
        await using var fixture = await CreateFixtureAsync();
        var deadCookie = await SignInAsync(fixture);
        await LogoutAllAsync(fixture);

        using (var promptNone = await fixture.AuthorizeWithSessionAsync("openid", deadCookie, prompt: "none"))
        {
            AssertLoginRequired(promptNone.Response);
            AssertDeletesIssuerCookie(promptNone.Response);
        }

        foreach (var maxAge in new[] { "0", "3600" })
        {
            using var withMaxAge = await fixture.AuthorizeWithSessionAsync("openid", deadCookie, maxAge: maxAge);
            var html = await withMaxAge.Response.Content.ReadAsStringAsync();
            withMaxAge.Response.StatusCode.Should().Be(HttpStatusCode.OK, html);
            html.Should().Contain("/login/password");
            AssertDeletesIssuerCookie(withMaxAge.Response);
        }

        var userCode = await StartDeviceAuthorizationAsync(fixture);
        using (var devicePage = await GetAsync(
            fixture,
            QueryHelpers.AddQueryString("/sqlos/auth/device", "user_code", userCode),
            deadCookie))
        {
            var html = await devicePage.Content.ReadAsStringAsync();
            devicePage.StatusCode.Should().Be(HttpStatusCode.OK, html);
            html.Should().NotContain("/device/approve\"");
            html.Should().Contain("/login/identify");
        }

        var antiforgery = await fixture.StartAuthorizeAsync("openid");
        using (var hostedApprove = await PostHostedFormAsync(
            fixture,
            "/sqlos/auth/device/approve",
            new Dictionary<string, string> { ["userCode"] = userCode },
            antiforgery.AntiforgeryToken,
            antiforgery.AntiforgeryCookie,
            deadCookie))
        {
            hostedApprove.StatusCode.Should().Be(HttpStatusCode.Redirect);
            hostedApprove.Headers.Location!.OriginalString.Should().StartWith("/sqlos/auth/device?user_code=");
        }

        using (var headlessApprove = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/headless/device/approve")
        {
            Content = JsonContent.Create(new { userCode })
        })
        {
            headlessApprove.Headers.TryAddWithoutValidation("Cookie", deadCookie);
            using var response = await fixture.Client.SendAsync(headlessApprove);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().Contain("Sign in before approving this device request.");
        }

        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await db.Set<SqlOSDeviceAuthorization>().AsNoTracking().SingleAsync(x => x.UserCode == userCode))
            .Status.Should().Be(SqlOSDeviceAuthorizationService.PendingStatus);
        (await db.Set<SqlOSIssuerSessionFamily>().CountAsync(x => x.RevokedAt == null)).Should().Be(0);
    }

    private static Task<HostedAuthorizeTokenFixture> CreateFixtureAsync()
        => HostedAuthorizeTokenFixture.CreateAsync(
            "DeadIssuerCookie",
            configure: options => options.AuthServer.SeedCliClient(CliClientId, "Dead Cookie CLI", "https://api.example.test", "openid"));

    private static async Task<string> SignInAsync(HostedAuthorizeTokenFixture fixture)
    {
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);
        using var tokens = await fixture.ExchangeAuthorizationCodeAsync(login.Code, started.CodeVerifier);
        return login.IssuerSessionCookie;
    }

    private static async Task ResetPasswordAsync(HostedAuthorizeTokenFixture fixture)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
        var token = await auth.CreatePasswordResetTokenAsync(new SqlOSForgotPasswordRequest(fixture.Email));
        await auth.ResetPasswordAsync(new SqlOSResetPasswordRequest(token, HostedAuthorizeTokenFixture.Password));
    }

    private static async Task LogoutAllAsync(HostedAuthorizeTokenFixture fixture)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SqlOSAuthService>().LogoutAllAsync(fixture.UserId);
    }

    private static async Task CleanUpTemporaryTokensAsync(HostedAuthorizeTokenFixture fixture)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>().CleanupExpiredTemporaryTokensAsync();
    }

    private static async Task<string> StartDeviceAuthorizationAsync(HostedAuthorizeTokenFixture fixture)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<SqlOSDeviceAuthorizationService>().StartAsync(
            new SqlOSDeviceAuthorizationStartRequest(CliClientId, "openid"),
            new DefaultHttpContext());
        return started.UserCode;
    }

    private static async Task<SqlOSIssuerSessionFamily> FamilyOfAsync(HostedAuthorizeTokenFixture fixture, string cookie)
    {
        var inspection = await fixture.InspectAuthPageAsync(cookie);
        var familyId = inspection.Tokens.Single()?.IssuerSessionFamilyId
            ?? throw new InvalidOperationException("The issuer session cookie has no stored family.");
        return await FindFamilyAsync(fixture, familyId);
    }

    private static async Task<SqlOSIssuerSessionFamily> FindFamilyAsync(HostedAuthorizeTokenFixture fixture, string familyId)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        return await db.Set<SqlOSIssuerSessionFamily>().AsNoTracking().SingleAsync(x => x.Id == familyId);
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        var location = await HostedAuthorizeTokenFixture.ReadClientRedirectAsync(response);
        var query = QueryHelpers.ParseQuery(location.IsAbsoluteUri ? location.Query : location.OriginalString);
        var code = query["code"].ToString();
        code.Should().NotBeNullOrWhiteSpace($"the sign-in should redirect with a code, not {location}");
        return code;
    }

    private static void AssertLoginRequired(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        query["error"].ToString().Should().Be("login_required");
        query.ContainsKey("code").Should().BeFalse();
    }

    private static void AssertDeletesIssuerCookie(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
        values!.Should().Contain(value =>
            value.StartsWith("sqlos_auth_page=;", StringComparison.Ordinal)
            && value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        values!.Should().NotContain(value =>
            value.StartsWith("sqlos_auth_page=", StringComparison.Ordinal)
            && !value.StartsWith("sqlos_auth_page=;", StringComparison.Ordinal));
    }

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

    private static async Task<HttpResponseMessage> GetAsync(
        HostedAuthorizeTokenFixture fixture,
        string path,
        params string[] cookies)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        return await fixture.Client.SendAsync(request);
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
