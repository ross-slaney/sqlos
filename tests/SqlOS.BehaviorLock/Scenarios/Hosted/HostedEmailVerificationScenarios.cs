using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// The hosted page the emailed "verify your address" link opens (<c>GET /email/verify</c>,
/// served as HTML from the public account endpoints). Unlike sign-in proofs, the explicit
/// verification link only marks the address verified: it is not a #423 claim.
/// </summary>
[TestClass]
public sealed class HostedEmailVerificationScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/email/verify")]
    [Covers("POST /sqlos/auth/login/password")]
    public async Task The_emailed_link_verifies_the_address_without_claiming_it()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        HostedFlows.EnsureStatus(
            t.Discard(await t.Api.PostJsonAsync("/sqlos/auth/email/verification-email", new { email = alice.Email })),
            200);
        var token = HostedFlows.LinkToken(t, alice.Email, "verification-token");
        await t.SkipAuditAsync();

        t.Observe(
            await t.GetAsync($"/sqlos/auth/email/verify?token={Uri.EscapeDataString(token)}"),
            "open the verification link");
        t.Observe(
            await t.GetAsync($"/sqlos/auth/email/verify?token={Uri.EscapeDataString(token)}"),
            "open it again: the link is spent");
        await t.ObserveAuditAsync("verification events");

        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        t.Observe(
            await t.SubmitAsync(begun.Page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "the password still works: verifying revoked nothing");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/email/verify")]
    public async Task Missing_and_unknown_verification_tokens_show_the_failure_page()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        t.Observe(await t.GetAsync("/sqlos/auth/email/verify"), "no token");
        t.Observe(await t.GetAsync("/sqlos/auth/email/verify?token=not-a-verification-token"), "a token SqlOS never issued");

        await t.ObserveAuditAsync("verification failures");
        await t.ApproveAsync();
    }
}
