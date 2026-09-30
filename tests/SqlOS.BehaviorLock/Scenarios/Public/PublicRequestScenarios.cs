using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// How the public account API treats request shapes and the two host options that gate password
/// use. Malformed JSON is answered by ASP.NET's request binding before SqlOS runs; a cross-site
/// form post cannot reach these JSON routes because it is not JSON.
/// </summary>
[TestClass]
public sealed class PublicRequestScenarios
{
    private const string Client = BehaviorLockConstants.AppClientId;

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/logout")]
    public async Task Malformed_requests_are_answered_by_request_binding()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.Api.SendAsync(HttpMethod.Post, "/sqlos/auth/password/login", content: null),
            "no body");
        t.Observe(
            await t.Api.SendAsync(HttpMethod.Post, "/sqlos/auth/password/login", new StringContent("{\"email\":", Encoding.UTF8, "application/json")),
            "truncated JSON");
        t.Observe(
            await t.Api.SendAsync(HttpMethod.Post, "/sqlos/auth/password/login", new StringContent("[]", Encoding.UTF8, "application/json")),
            "JSON of the wrong shape");
        t.Observe(
            await t.Browser.PostFormAsync("/sqlos/auth/password/login", new Dictionary<string, string>
            {
                ["email"] = alice.Email,
                ["password"] = alice.Password,
                ["clientId"] = Client
            }, options => options.WithOrigin("https://attacker.example.test")),
            "a cross-site HTML form post is not JSON");
        t.Observe(
            await t.Api.SendAsync(HttpMethod.Post, "/sqlos/auth/signup", new StringContent("{\"displayName\":\"Mallory\"}", Encoding.UTF8, "text/plain")),
            "a text/plain body (a simple cross-site request) is not JSON either");
        t.Observe(
            await t.Api.SendAsync(HttpMethod.Post, "/sqlos/auth/logout", new StringContent("refreshToken=x", Encoding.UTF8, "application/x-www-form-urlencoded")),
            "logout reads its body by hand: a form body escapes the endpoint");

        await t.ObserveAuditAsync("nothing reached SqlOS");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/password/forgot")]
    [Covers("POST /sqlos/auth/password/reset")]
    [Covers("GET /sqlos/auth/password/reset")]
    [Covers("POST /sqlos/auth/password/reset/submit")]
    public async Task Password_routes_refuse_when_the_host_disables_local_passwords()
    {
        await using var t = await PublicHost.StartAsync(
            HostProfiles.Hosted,
            options => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.EnableLocalPasswordAuth = false);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "password login escapes the endpoint");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = "Dana", email = t.Unique.Email("dana"), password = t.Unique.Password("dana"), clientId = Client }),
            "password signup is a public error");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = alice.Email, clientId = Client }),
            "a reset request gets the generic answer and sends nothing");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/reset", new { token = "any-reset-token", newPassword = t.Unique.Password("alice-new") }),
            "a JSON reset escapes the endpoint before the token is read");
        var page = t.Observe(
            await t.GetAsync("/sqlos/auth/password/reset?token=any-reset-token"),
            "the hosted reset page still renders");
        t.Observe(
            await t.SubmitAsync(page.BrowserForm("reset/submit").With("newPassword", "Lock-Alice-New-2468!").With("confirmPassword", "Lock-Alice-New-2468!")),
            "and its form answers with the public message");

        await t.ObserveAuditAsync("only the reset request is audited, as ineligible");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/email/verification-email")]
    [Covers("GET /sqlos/auth/email/verify")]
    public async Task Unverified_addresses_cannot_use_password_login_when_the_host_requires_verification()
    {
        await using var t = await PublicHost.StartAsync(
            HostProfiles.Hosted,
            options => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.RequireVerifiedEmailForPasswordLogin = true);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "the right password for an unverified address escapes the endpoint");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = "Wrong-Password-1!", clientId = Client }),
            "a wrong password fails the same way, before it is checked or counted");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email/verification-email", new { email = alice.Email }),
            "Alice asks for a verification email");
        t.Observe(
            await t.GetAsync($"/sqlos/auth/email/verify?token={Uri.EscapeDataString(t.LatestEmailLinkToken(alice.Email))}"),
            "and verifies her address");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "now the password signs in");

        await t.ObserveAuditAsync("no failed-login events before verification");
        await t.ApproveAsync();
    }
}
