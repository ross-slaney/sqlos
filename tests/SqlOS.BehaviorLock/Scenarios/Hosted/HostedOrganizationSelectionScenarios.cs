using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// The hosted organization chooser a member of several organizations meets after signing in, and
/// <c>POST /login/select-organization</c>'s refusals. The selection token is consumed before the
/// membership check, so a wrong choice ends the selection. SqlOS lists the organizations in no
/// particular order, so both render identically and options are picked by page position.
/// </summary>
[TestClass]
public sealed class HostedOrganizationSelectionScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/login/select-organization")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_member_of_two_organizations_chooses_which_one_to_continue_into()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await HostedFlows.AddToTwoOrganizationsAsync(t, alice);
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });

        var chooser = t.Observe(
            await t.SubmitAsync(begun.Page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "sign in: the organization chooser");
        var chosen = t.Observe(
            await t.SubmitAsync(chooser.Form("/login/select-organization").With("organizationId", HostedFlows.OrganizationOptions(chooser)[1])),
            "choose the second organization listed");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", begun.Request.TokenRequest(chosen.NextUrlParameter("code"))),
            "redeem the authorization code: the tokens carry the chosen organization");

        await t.ObserveAuditAsync("sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    [Covers("POST /sqlos/auth/login/select-organization")]
    public async Task The_chooser_also_follows_an_email_code_sign_in()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        await HostedFlows.AddToTwoOrganizationsAsync(t, alice);
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", alice.Email)));

        var chooser = t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("code", HostedFlows.EmailCode(t, alice.Email))),
            "enter the code: the organization chooser");
        t.Observe(
            await t.SubmitAsync(chooser.Form("/login/select-organization").With("organizationId", HostedFlows.OrganizationOptions(chooser)[0])),
            "choose the first organization listed");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/select-organization")]
    public async Task Choosing_an_organization_the_user_is_not_in_ends_the_selection()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await HostedFlows.AddToTwoOrganizationsAsync(t, alice);
        var other = await t.Setup.CreateOrganizationAsync("fabrikam");
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var chooser = t.Discard(await t.SubmitAsync(begun.Page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)));
        await t.SkipAuditAsync();
        var select = chooser.Form("/login/select-organization");
        var listed = HostedFlows.OrganizationOptions(chooser);

        t.Observe(
            await t.SubmitAsync(select.With("organizationId", other.Id)),
            "post an organization Alice does not belong to: refused, and the chooser comes back empty");
        t.Observe(
            await t.SubmitAsync(select.With("organizationId", listed[0])),
            "the selection token was already consumed, so a valid choice fails too");
        t.Observe(
            await t.SubmitAsync(select.With("pendingToken", "not-a-selection-token").With("organizationId", listed[0])),
            "an unknown selection token");

        await t.ObserveAuditAsync("refusals write nothing");
        await t.ApproveAsync();
    }
}
