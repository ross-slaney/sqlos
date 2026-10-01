using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>
/// Email ownership through the headless API: codes and links only ever reach the stored address
/// of an account (#422), and proving an unverified address claims it from whoever registered it
/// first (#423).
/// </summary>
[TestClass]
public sealed class HeadlessEmailOwnershipScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/headless/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/magic-link/start")]
    public async Task A_look_alike_address_never_receives_a_code_or_link_for_the_real_account()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var domain = t.Unique.Domain("business");
        var bob = t.Unique.Email("bob", domain);
        await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/users", new { displayName = "Bob", email = bob, password = t.Unique.Password("bob") });
        var lookalikeDomain = domain.Replace("ss", "ß", StringComparison.Ordinal);
        var asciiDomain = new IdnMapping().GetAscii(lookalikeDomain);
        t.Scrub(lookalikeDomain, "domain", "business-with-sharp-s");
        t.Scrub(lookalikeDomain.ToUpperInvariant(), "domain", "BUSINESS-WITH-SHARP-S");
        t.Scrub(asciiDomain, "domain", "business-with-sharp-s-ascii");
        t.Scrub(asciiDomain.ToUpperInvariant(), "domain", "BUSINESS-WITH-SHARP-S-ASCII");
        var lookalike = $"bob@{lookalikeDomain}";
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());

        t.Note("#422: on SQL Server's default collation 'BUSINEß' equals 'BUSINESS'; SqlOS must neither match bob's account nor deliver to the typed address.");
        t.Observe(
            await t.PostJsonAsync($"{Api}/email-otp/start", new { requestId, email = lookalike }),
            "an email code for bob's address with ß in place of ss: no account matches, nothing is sent");
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/start", new { requestId, email = lookalike }),
            "a magic link for the same look-alike address: nothing is sent");
        t.Note($"emails sent during the journey: {t.Emails.Count}");

        await t.ObserveAuditAsync("look-alike events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/signup")]
    [Covers("POST /sqlos/auth/headless/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/email-otp/verify")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_password_squatter_loses_the_account_when_the_owner_proves_the_address_with_an_email_code()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var victim = t.Unique.Email("victim");
        var squatterPassword = t.Unique.Password("squatter");
        var attacker = t.NewBrowser("attacker");
        var attackerRequest = t.Urls.Authorize();
        var attackerRequestId = await OpenAuthorizeAsync(t, attackerRequest, browser: attacker);

        var squatted = t.Observe(
            await attacker.PostJsonAsync($"{Api}/signup", new { requestId = attackerRequestId, displayName = "Mallory", email = victim, password = squatterPassword }),
            "the attacker signs up with the victim's address and a password of their choosing (#423 variant A)");
        var attackerTokens = t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", attackerRequest.TokenRequest(RedirectCode(squatted))),
            "the attacker redeems tokens for the unverified account");

        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);
        var started = t.Observe(
            await t.PostJsonAsync($"{Api}/email-otp/start", new { requestId, email = victim }),
            "later the victim signs in with an email code: it reaches the victim's mailbox");
        var verified = t.Observe(
            await t.PostJsonAsync($"{Api}/email-otp/verify", new { requestId, challengeToken = started.JsonString("viewModel.challengeToken"), code = EmailCode(t, victim) }),
            "the code proves the mailbox and claims the address: the squatter's password is revoked");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(verified))),
            "the victim redeems the code");

        var retryRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: t.NewBrowser("attacker-retry"));
        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/password/login", new { requestId = retryRequestId, email = victim, password = squatterPassword }),
            "the squatter's password no longer signs in");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = attackerTokens.JsonString("refresh_token"),
                ["client_id"] = AppClientId
            }),
            "the squatter's refresh token no longer works");

        await t.ObserveAuditAsync("claim events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/signup")]
    [Covers("POST /sqlos/auth/headless/magic-link/start")]
    [Covers("POST /sqlos/auth/headless/magic-link/complete")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    public async Task A_password_squatter_loses_the_account_when_the_owner_opens_a_magic_link()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var victim = t.Unique.Email("victim");
        var squatterPassword = t.Unique.Password("squatter");
        var attacker = t.NewBrowser("attacker");
        var attackerRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: attacker);
        t.Observe(
            await attacker.PostJsonAsync($"{Api}/signup", new { requestId = attackerRequestId, displayName = "Mallory", email = victim, password = squatterPassword }),
            "the attacker signs up with the victim's address");

        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/start", new { requestId, email = victim }),
            "the victim asks for a sign-in link");
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/complete", new { token = EmailLinkToken(t, victim), requestId }),
            "the link proves the mailbox and claims the address");
        var retryRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: t.NewBrowser("attacker-retry"));
        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/password/login", new { requestId = retryRequestId, email = victim, password = squatterPassword }),
            "the squatter's password no longer signs in");

        await t.ObserveAuditAsync("claim events");
        await t.ApproveAsync();
    }
}
