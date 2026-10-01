using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>Organization selection after sign-in, for users with more than one membership.</summary>
[TestClass]
public sealed class HeadlessOrganizationScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/headless/organization/select")]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task Picking_an_organization_signs_the_browser_in_but_answers_400_without_the_code_CurrentBehavior_KnownDefect_Unfiled()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var frank = await t.Setup.CreateUserAsync("frank");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        await t.Setup.AddMembershipAsync(acme, frank, "admin");
        await t.Setup.AddMembershipAsync(globex, frank);
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);

        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = frank.Email, password = frank.Password }),
            "sign in: two memberships, so the UI must ask which organization");
        var pendingToken = login.JsonString("viewModel.pendingToken");
        t.Note(
            "Known defect (found by this catalog, not yet filed): after the pick, SqlOS issues the code and signs the browser in, " +
            "then re-reads the now-completed authorization request and answers 400, so the code is never delivered. " +
            "With an MFA policy the pick continues to the challenge instead (HeadlessMfaScenarios).");
        t.Observe(
            await t.PostJsonAsync($"{Api}/organization/select", new { pendingToken, organizationId = globex.Id }),
            "pick the second organization: 400 invalid_request, yet the issuer session cookie is set");
        t.Observe(
            await t.PostJsonAsync($"{Api}/organization/select", new { pendingToken, organizationId = acme.Id }),
            "the selection token was consumed, so the pick cannot be retried");
        t.Observe(
            await t.GetAsync(t.Urls.Authorize().Url),
            "the app starts over: the signed-in browser is sent straight back to the organization view");

        await t.ObserveAuditAsync("organization selection events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/organization/select")]
    public async Task Picking_an_organization_the_user_does_not_belong_to_is_refused_and_burns_the_selection()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var frank = await t.Setup.CreateUserAsync("frank");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var initech = await t.Setup.CreateOrganizationAsync("initech");
        await t.Setup.AddMembershipAsync(acme, frank);
        await t.Setup.AddMembershipAsync(globex, frank);
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());

        var login = t.Discard(await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = frank.Email, password = frank.Password }));
        var pendingToken = login.JsonString("viewModel.pendingToken");
        t.Observe(
            await t.PostJsonAsync($"{Api}/organization/select", new { pendingToken, organizationId = initech.Id }),
            "pick an organization frank is not a member of: refused");
        t.Observe(
            await t.PostJsonAsync($"{Api}/organization/select", new { pendingToken, organizationId = acme.Id }),
            "the refused pick consumed the selection token, so a valid pick fails too");
        t.Observe(
            await t.PostJsonAsync($"{Api}/organization/select", new { pendingToken = "not-a-pending-token", organizationId = acme.Id }),
            "an unknown selection token");

        await t.ObserveAuditAsync("refused selection events");
        await t.ApproveAsync();
    }
}
