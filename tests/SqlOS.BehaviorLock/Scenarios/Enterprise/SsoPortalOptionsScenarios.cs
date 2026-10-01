using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// The SSO portal's documented host options: hand setup links to the host's own setup UI
/// (<c>SsoPortal.BuildUiUrl</c>), turn the hosted portal page off (<c>UseHostedPortal</c>), or turn
/// the portal API off (<c>EnableApi</c>). Each scenario changes only that option.
/// </summary>
[TestClass]
public sealed class SsoPortalOptionsScenarios
{
    [Scenario]
    [Covers("GET /sqlos/admin/auth/sso-portal/start")]
    [Covers("GET /sqlos/admin/auth/sso-portal/")]
    [Covers("GET /sqlos/admin/auth/sso-portal/api/setup/")]
    public async Task A_host_with_its_own_setup_ui_receives_the_setup_link_handoff()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise, options => options.ConfigureSqlOS = sqlos =>
        {
            sqlos.AuthServer.SsoPortal.UseHostedPortal = false;
            sqlos.AuthServer.SsoPortal.BuildUiUrl = route => $"https://sqlos.example.test/settings/sso?session={route.SessionId}&organization={route.OrganizationId}&view={route.View}";
        });
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var session = await t.Setup.OperatorPostAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", new { });
        var admin = t.NewBrowser("customer-admin");

        t.Observe(await admin.GetAsync(new Uri(session.JsonString("setupUrl")).PathAndQuery), "the setup link opens the session and redirects to the host's UI");
        t.Observe(await admin.GetAsync(PortalVisit.PortalPath), "the hosted portal page is off");
        t.Observe(await admin.GetAsync(PortalVisit.SetupApiPath), "the host's UI drives the setup API with the session cookie");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/sso-portal/start")]
    [Covers("GET /sqlos/admin/auth/sso-portal/")]
    public async Task Without_the_hosted_portal_or_a_setup_ui_a_setup_link_has_nowhere_to_go()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise, options => options.ConfigureSqlOS = sqlos =>
            sqlos.AuthServer.SsoPortal.UseHostedPortal = false);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var session = await t.Setup.OperatorPostAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", new { });
        var admin = t.NewBrowser("customer-admin");

        t.Observe(await admin.GetAsync(new Uri(session.JsonString("setupUrl")).PathAndQuery), "opening the link still opens a session, then answers 404");
        t.Observe(await admin.GetAsync(PortalVisit.PortalPath), "the hosted portal page");
        t.Observe(await admin.GetAsync($"{PortalVisit.ApiPath}/state"), "the API itself stays on");

        await t.ObserveAuditAsync("the session was opened");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/sso-portal/start")]
    [Covers("GET /sqlos/admin/auth/sso-portal/api/state")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/signout")]
    [Covers("GET /sqlos/admin/auth/sso-portal/api/setup/")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/signout")]
    public async Task With_the_portal_api_off_every_portal_api_route_answers_404()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise, options => options.ConfigureSqlOS = sqlos =>
            sqlos.AuthServer.SsoPortal.EnableApi = false);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var session = await t.Setup.OperatorPostAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", new { });
        var admin = t.NewBrowser("customer-admin");

        t.Observe(await admin.GetAsync(new Uri(session.JsonString("setupUrl")).PathAndQuery), "the setup link still opens a session");
        t.Observe(await admin.GetAsync($"{PortalVisit.ApiPath}/state"), "the portal API state");
        t.Observe(await admin.PostJsonAsync($"{PortalVisit.ApiPath}/signout", new { }, PortalVisit.SameOrigin), "portal sign-out");
        t.Observe(await admin.GetAsync(PortalVisit.SetupApiPath), "the setup API view");
        t.Observe(await admin.PostJsonAsync($"{PortalVisit.SetupApiPath}/signout", new { }, PortalVisit.SameOrigin), "setup API sign-out");

        await t.ApproveAsync();
    }
}
