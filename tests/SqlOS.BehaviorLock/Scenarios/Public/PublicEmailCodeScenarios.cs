using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// Email sign-in codes through the public account API: <c>POST /sqlos/auth/email-otp/start</c>
/// emails a six-digit code and returns a challenge token; <c>POST /sqlos/auth/email-otp/verify</c>
/// trades both for tokens. Failures other than the direct-login client gate escape the endpoint.
/// </summary>
[TestClass]
public sealed class PublicEmailCodeScenarios
{
    private const string Client = BehaviorLockConstants.AppClientId;

    [Scenario]
    [Covers("POST /sqlos/auth/email-otp/start")]
    [Covers("POST /sqlos/auth/email-otp/verify")]
    public async Task Email_code_sign_in_verifies_the_address_and_returns_tokens()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        var started = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            "request a sign-in code; it is emailed to the account's address");
        var challengeToken = started.JsonString("challengeToken");
        var code = t.LatestEmailCode(alice.Email);
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken, code }),
            "verify the code: tokens come back in the body");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken, code }),
            "the consumed code cannot be replayed");

        await t.ObserveAuditAsync("email-code events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}", "the code proved the mailbox: the email is now verified");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/email-otp/start")]
    [Covers("POST /sqlos/auth/email-otp/verify")]
    public async Task Wrong_codes_exhaust_the_challenge_and_the_right_code_is_then_refused()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        var started = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            "request a sign-in code");
        var challengeToken = started.JsonString("challengeToken");
        var code = t.LatestEmailCode(alice.Email);

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken = "not-a-challenge-token", code }),
            "an unknown challenge token fails without touching the challenge");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken, code = "no digits" }),
            "a code without digits fails before the challenge is read");
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            t.Observe(
                await t.Api.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken, code = PublicSetup.WrongCode(code, attempt) }),
                $"wrong code, attempt {attempt.ToString(CultureInfo.InvariantCulture)}");
        }

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken, code }),
            "five wrong codes invalidated the challenge: the right code is refused");

        await t.ObserveAuditAsync("failed attempts, the last one invalidating the challenge");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/email-otp/start")]
    public async Task An_unknown_address_gets_the_same_answer_and_no_email()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            "a known address: a code is emailed");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = t.Unique.Email("nobody"), clientId = Client }),
            "an unknown address: the same answer, and nothing is sent");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = "not an address", clientId = Client }),
            "an invalid address escapes the endpoint");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = "unknown-client" }),
            "an unknown client id escapes the endpoint");

        await t.ObserveAuditAsync("challenge events record whether a code was sent");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/email-otp/start")]
    public async Task Code_requests_are_throttled_per_context_and_per_address()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            "first request");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            "the same request inside the 30-second resend cooldown escapes the endpoint");
        for (var context = 2; context <= 5; context++)
        {
            t.Observe(
                await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client, organizationId = $"workspace-{context.ToString(CultureInfo.InvariantCulture)}" }),
                $"another requested organization is another context, so no cooldown (request {context.ToString(CultureInfo.InvariantCulture)} this hour)");
        }

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client, organizationId = "workspace-6" }),
            "the sixth code for the address this hour is refused");

        await t.ObserveAuditAsync("five challenges and the rate-limit rejection");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/email-otp/start")]
    public async Task Codes_go_to_the_stored_address_and_look_alike_addresses_match_no_account()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        // A domain with "ss", which SQL Server's default collation equates with "ß" (#422).
        var domain = t.Unique.Domain("business");
        t.Scrub(domain.ToUpperInvariant(), "domain", "BUSINESS");
        var lookalikeDomain = domain.Replace("business", "busineß", StringComparison.Ordinal);
        t.Scrub(lookalikeDomain, "domain", "busineß");
        t.Scrub(lookalikeDomain.ToUpperInvariant(), "domain", "BUSINEß");
        t.Scrub(new IdnMapping().GetAscii(lookalikeDomain), "domain", "busineß-punycode");
        var bob = await t.CreateUserWithEmailAsync("bob", t.Unique.Email("bob", domain));
        var lookalike = "bob@" + lookalikeDomain;

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = "  " + bob.Email.ToUpperInvariant() + "  ", clientId = Client }),
            "a differently cased and padded spelling finds Bob; the code goes to the stored address");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = lookalike, clientId = Client }),
            "a look-alike domain (ß for ss) matches no account and sends nothing");

        await t.ObserveAuditAsync("the look-alike challenge was not sent");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/email-otp/start")]
    [Covers("POST /sqlos/auth/email-otp/verify")]
    [Covers("POST /sqlos/auth/magic-link/start")]
    [Covers("POST /sqlos/auth/magic-link/complete")]
    public async Task Code_and_link_sign_in_are_unavailable_when_only_passwords_are_enabled()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            "email codes are not an enabled credential type");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken = "any-challenge-token", code = "123456" }),
            "verification is refused before any lookup");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = Client }),
            "sign-in links are not an enabled credential type");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/complete", new { token = "any-link-token" }),
            "completion is refused before any lookup");

        await t.ObserveAuditAsync("nothing is audited");
        await t.ApproveAsync();
    }
}
