using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Interfaces;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// #423 consent path, over the exact hosted routes: an approval a squatter gave while the
/// address was unverified must not outlive the owner's claim of that address. Otherwise the
/// attacker's client, approved by the squatter, silently receives a code for the owner's session.
/// </summary>
[TestClass]
public sealed class EmailClaimConsentIntegrationTests
{
    private const string AttackerRedirect = "https://attacker.example.test/callback";

    [TestMethod]
    public async Task SquatterApprovedAttackerClient_OwnerClaimsWithEmailCode_AttackerClientGetsNoCode()
    {
        var emails = new TestAuthEmailSender { IsConfigured = true };
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync(
            "ClaimConsent",
            configure: options =>
            {
                options.AuthServer.ClientRegistration.Dcr.Enabled = true;
                options.AuthServer.SeedAuthPage(page => page.EnabledCredentialTypes = ["password", "email_otp"]);
            },
            configureServices: services =>
            {
                services.RemoveAll<ISqlOSAuthEmailSender>();
                services.RemoveAll<ISqlOSEmailSender>();
                services.AddSingleton<ISqlOSAuthEmailSender>(emails);
                services.AddSingleton<ISqlOSEmailSender>(emails);
            });

        // The fixture account stands in for the squatted one: its address is unverified and the
        // squatter chose its password. The squatter registers its own client and approves it.
        (await QueryAsync(fixture, db => db.Set<SqlOSUserEmail>().AsNoTracking().SingleAsync(x => x.UserId == fixture.UserId)))
            .IsVerified.Should().BeFalse();
        var attackerClientId = await RegisterDcrClientAsync(fixture);
        var squatterStart = await fixture.StartAuthorizeAsync("openid", clientId: attackerClientId, redirectUri: AttackerRedirect);
        var consent = await fixture.SubmitPasswordLoginExpectingConsentAsync(squatterStart);
        using (var approved = await fixture.SubmitConsentDecisionAsync(consent, approve: true))
        {
            var approvedLocation = await HostedAuthorizeTokenFixture.ReadClientRedirectAsync(approved);
            QueryHelpers.ParseQuery(approvedLocation.Query)["code"].ToString().Should().NotBeNullOrWhiteSpace();
        }

        var grantId = await QueryAsync(fixture, db => db.Set<SqlOSConsentGrant>()
            .Where(x => x.UserId == fixture.UserId && x.RevokedAt == null)
            .Select(x => x.Id)
            .SingleAsync());

        // The owner proves the mailbox with an email code in a fresh browser.
        var ownerCookie = await SignInWithEmailCodeAsync(fixture, emails);
        (await QueryAsync(fixture, db => db.Set<SqlOSUserEmail>().AsNoTracking().SingleAsync(x => x.UserId == fixture.UserId)))
            .IsVerified.Should().BeTrue();

        // The attacker sends the owner's browser to its client without and with prompt=none.
        using (var silent = await fixture.AuthorizeWithSessionAsync(
            "openid",
            ownerCookie,
            prompt: "none",
            clientId: attackerClientId,
            redirectUri: AttackerRedirect))
        {
            silent.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var location = silent.Response.Headers.Location!;
            location.AbsoluteUri.Should().StartWith(AttackerRedirect);
            var query = QueryHelpers.ParseQuery(location.Query);
            query.ContainsKey("code").Should().BeFalse(
                "the squatter's approval must not send a code for the owner's session to the attacker's client");
            query["error"].ToString().Should().Be("consent_required");
        }

        using (var interactive = await fixture.AuthorizeWithSessionAsync(
            "openid",
            ownerCookie,
            clientId: attackerClientId,
            redirectUri: AttackerRedirect))
        {
            var html = await interactive.Response.Content.ReadAsStringAsync();
            interactive.Response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the owner must see the consent screen, not be redirected to {0}",
                interactive.Response.Headers.Location);
            html.Should().Contain("/sqlos/auth/consent/approve");
        }

        var grant = await QueryAsync(fixture, db => db.Set<SqlOSConsentGrant>().AsNoTracking().SingleAsync(x => x.Id == grantId));
        grant.RevokedAt.Should().NotBeNull();
        grant.RevocationReason.Should().Be(SqlOSEmailOwnershipClaim.RevocationReason);
        var claim = await QueryAsync(fixture, db => db.Set<SqlOSAuditEvent>()
            .AsNoTracking()
            .SingleAsync(x => x.UserId == fixture.UserId && x.EventType == SqlOSEmailOwnershipClaim.AuditEventType));
        claim.DataJson.Should().Contain(grantId);
    }

    private static async Task<string> RegisterDcrClientAsync(HostedAuthorizeTokenFixture fixture)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/sqlos/auth/register", new
        {
            client_name = "Attacker DCR Client",
            redirect_uris = new[] { AttackerRedirect },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none"
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("client_id").GetString()!;
    }

    /// <summary>
    /// Signs in through the first-party client with an emailed code in a browser with no cookies
    /// and returns the issuer session cookie the sign-in set.
    /// </summary>
    private static async Task<string> SignInWithEmailCodeAsync(HostedAuthorizeTokenFixture fixture, TestAuthEmailSender emails)
    {
        var started = await fixture.StartAuthorizeAsync("openid");
        using var start = await PostHostedFormAsync(
            fixture,
            "/sqlos/auth/login/email-otp/start",
            new Dictionary<string, string>
            {
                ["requestId"] = started.RequestId,
                ["email"] = fixture.Email
            },
            started.AntiforgeryToken,
            started.AntiforgeryCookie);
        var verifyPage = await start.Content.ReadAsStringAsync();
        start.StatusCode.Should().Be(HttpStatusCode.OK, verifyPage);
        var message = emails.Messages.Last(x => string.Equals(x.To, fixture.Email, StringComparison.Ordinal));

        using var verify = await PostHostedFormAsync(
            fixture,
            "/sqlos/auth/login/email-otp/verify",
            new Dictionary<string, string>
            {
                ["requestId"] = started.RequestId,
                ["email"] = fixture.Email,
                ["challengeToken"] = ExtractInputValue(verifyPage, "challengeToken"),
                ["code"] = EmailOwnershipServer.ExtractCode(message)
            },
            ExtractInputValue(verifyPage, "__RequestVerificationToken"),
            started.AntiforgeryCookie);
        var location = await HostedAuthorizeTokenFixture.ReadClientRedirectAsync(verify);
        QueryHelpers.ParseQuery(location.IsAbsoluteUri ? location.Query : location.OriginalString)["code"].ToString()
            .Should().NotBeNullOrWhiteSpace();
        return HostedAuthorizeTokenFixture.TryExtractCookie(verify, "sqlos_auth_page=")
            ?? throw new InvalidOperationException("The email-code sign-in did not set an issuer session cookie.");
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

    private static string ExtractInputValue(string html, string name)
    {
        var match = Regex.Match(
            html,
            $@"name=""{Regex.Escape(name)}"" value=""([^""]*)""",
            RegexOptions.CultureInvariant);
        match.Success.Should().BeTrue($"the page should include a '{name}' input");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<T> QueryAsync<T>(
        HostedAuthorizeTokenFixture fixture,
        Func<TestSqlOSDbContext, Task<T>> query)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>());
    }
}
