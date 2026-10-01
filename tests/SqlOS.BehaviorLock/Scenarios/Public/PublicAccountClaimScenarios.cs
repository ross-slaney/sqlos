using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// #423, pre-account takeover through the public account API. With default settings a squatter can
/// sign up with an address they do not own and gets a working password and session. When the real
/// owner later proves the mailbox by a sign-in code, a sign-in link, or a password reset, SqlOS
/// claims the address: it revokes every credential and session attached before the proof, so the
/// squatter's password and refresh token stop working. (The verification link is not a claim; see
/// <see cref="PublicEmailVerificationScenarios"/>.)
/// </summary>
[TestClass]
public sealed class PublicAccountClaimScenarios
{
    private const string Client = BehaviorLockConstants.AppClientId;

    [Scenario]
    [Covers("POST /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/email-otp/start")]
    [Covers("POST /sqlos/auth/email-otp/verify")]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task Proving_the_address_with_an_email_code_evicts_a_squatters_password_and_sessions()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var (victim, squatterPassword, squatterRefresh) = await SquatAsync(t);
        var owner = t.NewClient("owner");

        var started = t.Observe(
            await owner.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = victim, clientId = Client }),
            "the owner asks for an email code");
        t.Observe(
            await owner.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken = started.JsonString("challengeToken"), code = t.LatestEmailCode(victim) }),
            "the owner proves the mailbox and is signed in");

        await AssertSquatterEvictedAsync(t, victim, squatterPassword, squatterRefresh);
        await t.ObserveAuditAsync("the claim lists what it revoked");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/magic-link/start")]
    [Covers("POST /sqlos/auth/magic-link/complete")]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task Proving_the_address_with_a_sign_in_link_evicts_a_squatters_password_and_sessions()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var (victim, squatterPassword, squatterRefresh) = await SquatAsync(t);
        var owner = t.NewClient("owner");

        t.Observe(
            await owner.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = victim, clientId = Client }),
            "the owner asks for a sign-in link");
        t.Observe(
            await owner.PostJsonAsync("/sqlos/auth/magic-link/complete", new { token = t.LatestEmailLinkToken(victim) }),
            "the owner completes the link and is signed in");

        await AssertSquatterEvictedAsync(t, victim, squatterPassword, squatterRefresh);
        await t.ObserveAuditAsync("the claim lists what it revoked");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/password/forgot")]
    [Covers("POST /sqlos/auth/password/reset")]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/token/refresh")]
    public async Task Proving_the_address_with_a_password_reset_evicts_a_squatters_sessions()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var (victim, squatterPassword, squatterRefresh) = await SquatAsync(t);
        var owner = t.NewClient("owner");
        var ownerPassword = t.Unique.Password("owner");

        t.Observe(
            await owner.PostJsonAsync("/sqlos/auth/password/forgot", new { email = victim, clientId = Client }),
            "the owner asks for a password reset");
        t.Observe(
            await owner.PostJsonAsync("/sqlos/auth/password/reset", new { token = t.LatestEmailLinkToken(victim), newPassword = ownerPassword }),
            "the owner sets a password from the emailed link");

        await AssertSquatterEvictedAsync(t, victim, squatterPassword, squatterRefresh);
        t.Observe(
            await owner.PostJsonAsync("/sqlos/auth/password/login", new { email = victim, password = ownerPassword, clientId = Client }),
            "the owner's new password signs in");
        await t.ObserveAuditAsync("the reset claimed the address");
        await t.ApproveAsync();
    }

    /// <summary>The squatter signs up with the victim's address and keeps the session it returns.</summary>
    private static async Task<(string Victim, string Password, string RefreshToken)> SquatAsync(Transcript t)
    {
        var victim = t.Unique.Email("victim");
        var password = t.Unique.Password("squatter");
        var squatter = t.NewClient("squatter");
        var signup = t.Observe(
            await squatter.PostJsonAsync("/sqlos/auth/signup", new { displayName = "Squatter", email = victim, password, clientId = Client }),
            "a squatter signs up with the victim's address and gets tokens at once");
        return (victim, password, signup.JsonString("tokens.refreshToken"));
    }

    private static async Task AssertSquatterEvictedAsync(Transcript t, string victim, string password, string refreshToken)
    {
        var squatter = t.NewClient("squatter");
        t.Observe(
            await squatter.PostJsonAsync("/sqlos/auth/password/login", new { email = victim, password, clientId = Client }),
            "the squatter's password no longer signs in");
        t.Observe(
            await squatter.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken }),
            "the squatter's refresh token no longer works");
    }
}
