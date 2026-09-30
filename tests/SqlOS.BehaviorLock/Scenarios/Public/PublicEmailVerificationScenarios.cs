using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// Email verification: <c>POST /sqlos/auth/email/verification-email</c> (and its alias
/// <c>/email/verification-token</c>) always answers generically and emails a link to an
/// unverified address; the link opens <c>GET /sqlos/auth/email/verify</c>, and an application with
/// its own UI posts the token to <c>POST /sqlos/auth/email/verify</c>. Unlike the sign-in proofs,
/// verification is the signup confirmation step: it marks the address verified and revokes nothing.
/// </summary>
[TestClass]
public sealed class PublicEmailVerificationScenarios
{
    private const string Client = BehaviorLockConstants.AppClientId;

    [Scenario]
    [Covers("POST /sqlos/auth/email/verification-email")]
    [Covers("GET /sqlos/auth/email/verify")]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task The_verification_link_verifies_the_address_and_keeps_the_password_and_sessions()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.PasswordLoginAsync(alice);

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email/verification-email", new { email = alice.Email }),
            "ask for a verification email; the answer is generic");
        var token = t.LatestEmailLinkToken(alice.Email);
        t.Observe(
            await t.GetAsync($"/sqlos/auth/email/verify?token={Uri.EscapeDataString(token)}"),
            "open the emailed link: the address is verified");
        t.Observe(
            await t.GetAsync($"/sqlos/auth/email/verify?token={Uri.EscapeDataString(token)}"),
            "the link works once");
        t.Observe(
            await t.GetAsync("/sqlos/auth/email/verify"),
            "no token");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "the password still signs in (#423: verification is not a claim)");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = session["tokens"]!["refreshToken"]!.GetValue<string>() }),
            "and the session from before verification still refreshes");

        await t.ObserveAuditAsync("verification events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}", "the address is verified");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/email/verification-token")]
    [Covers("POST /sqlos/auth/email/verification-email")]
    [Covers("POST /sqlos/auth/email/verify")]
    public async Task Verification_through_the_json_api_answers_generically_and_holds_back_repeat_emails()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email/verification-token", new { email = alice.Email }),
            "ask for a verification email");
        var token = t.LatestEmailLinkToken(alice.Email);
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email/verification-token", new { email = alice.Email }),
            "asking again within a minute sends nothing");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email/verify", new { token }),
            "post the token: the address is verified");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email/verify", new { token }),
            "the token works once; a second use escapes the endpoint");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email/verification-email", new { email = alice.Email }),
            "a verified address gets the generic answer and no email");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email/verification-email", new { email = t.Unique.Email("nobody") }),
            "so does an unknown address");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email/verification-email", new { email = " " }),
            "a blank address escapes the endpoint");

        await t.ObserveAuditAsync("requests record eligibility; only the first was sent");
        await t.ApproveAsync();
    }
}
