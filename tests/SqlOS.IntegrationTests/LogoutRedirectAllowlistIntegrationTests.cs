using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// #414: an anonymous DCR registration must not turn <c>/sqlos/auth/logout</c> into an open
/// redirect. The browser still signs out and lands on the hosted logged-out page.
/// </summary>
[TestClass]
public sealed class LogoutRedirectAllowlistIntegrationTests
{
    private const string AttackerRedirect = "https://evil.example.test/cb";

    [TestMethod]
    public async Task Logout_DcrClientOrigin_LandsOnLoggedOut_AndRevokesIssuerSession()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync(
            "LogoutDcrOrigin",
            options => options.AuthServer.ClientRegistration.Dcr.Enabled = true);
        var dcrClientId = await RegisterDcrClientAsync(fixture);

        foreach (var clientIdParameter in new[] { null, dcrClientId })
        {
            var login = await fixture.SubmitPasswordLoginWithSessionAsync(await fixture.StartAuthorizeAsync("openid"));
            var path = "/sqlos/auth/logout?returnTo=" + Uri.EscapeDataString("https://evil.example.test/login");
            if (clientIdParameter != null)
            {
                path += "&client_id=" + Uri.EscapeDataString(clientIdParameter);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Cookie", login.IssuerSessionCookie);
            using var response = await fixture.Client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location.Should().Be(new Uri("/sqlos/auth/logged-out", UriKind.Relative));
            response.Headers.GetValues("Set-Cookie")
                .Should().Contain(cookie => cookie.StartsWith("sqlos_auth_page=;", StringComparison.Ordinal));

            using var silent = await fixture.AuthorizeWithSessionAsync("openid", login.IssuerSessionCookie, prompt: "none");
            silent.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            QueryHelpers.ParseQuery(silent.Response.Headers.Location!.Query)["error"].ToString()
                .Should().Be("login_required", "the issuer session must be revoked even when the redirect is refused");
        }
    }

    [TestMethod]
    public async Task Logout_SeededClientOrigin_IsStillHonored_WithAndWithoutClientId()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync(
            "LogoutSeededOrigin",
            options => options.AuthServer.ClientRegistration.Dcr.Enabled = true);
        await RegisterDcrClientAsync(fixture);
        const string destination = "https://client.example.test/signed-out";

        foreach (var suffix in new[] { "", "&client_id=" + HostedAuthorizeTokenFixture.ClientId })
        {
            using var response = await fixture.Client.GetAsync(
                "/sqlos/auth/logout?returnTo=" + Uri.EscapeDataString(destination) + suffix);
            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location.Should().Be(new Uri(destination));
        }
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
}
