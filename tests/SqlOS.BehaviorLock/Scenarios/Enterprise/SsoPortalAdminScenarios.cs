using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// The operator side of customer-managed SSO: issuing, listing, and revoking SSO setup links
/// (portal sessions) through the admin API, and what a customer sees when a link is no longer
/// usable.
/// </summary>
[TestClass]
public sealed class SsoPortalAdminScenarios
{
    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/sso-portal/sessions")]
    [Covers("POST /sqlos/admin/auth/api/sso-portal/sessions")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/sso-portal/sessions")]
    [Covers("POST /sqlos/admin/auth/api/sso-portal/sessions/{sessionId}/revoke")]
    [Covers("GET /sqlos/admin/auth/sso-portal/start")]
    public async Task An_operator_issues_lists_and_revokes_sso_setup_links()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var owner = await t.Setup.CreateUserAsync("owner");
        var sessions = $"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions";

        var first = t.Observe(
            await t.Operator.PostJsonAsync(sessions, new { }),
            "issue a setup link for Acme: a draft SAML connection is created with it");
        var firstId = first.JsonString("id");
        t.Scrub(firstId, "ssp", "first");
        var second = t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sso-portal/sessions", new
            {
                organizationId = acme.Id,
                createdByUserId = owner.Id,
                provider = "entra",
                returnUrl = "https://sqlos.example.test/settings/sso",
                expiresAt = DateTime.UtcNow.AddDays(2)
            }),
            "issue a second link through the top-level route, preselecting Microsoft Entra");
        var secondId = second.JsonString("id");
        t.Scrub(secondId, "ssp", "second");

        var customer = t.NewBrowser("customer-admin");
        t.Observe(await customer.GetAsync(new Uri(second.JsonString("setupUrl")).PathAndQuery), "the customer opens the second link");
        t.Observe(await t.Operator.GetAsync(sessions), "list Acme's links, newest first");
        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/sso-portal/sessions/{firstId}/revoke", new { reason = "sent to the wrong person" }),
            "revoke the first link with a reason");
        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/sso-portal/sessions/{firstId}/revoke", new { reason = "again" }),
            "revoking again keeps the first revocation");
        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/sso-portal/sessions/{secondId}/revoke", new { }),
            "revoke the opened link without a reason");
        t.Observe(await t.NewBrowser("recipient").GetAsync(new Uri(first.JsonString("setupUrl")).PathAndQuery), "the revoked link no longer opens");
        t.Observe(await t.Operator.GetAsync($"{sessions}?pageSize=1"), "a one-link page");

        await t.ObserveAuditAsync("portal session events");
        await t.ApproveAsync();
    }

    /// <summary>
    /// With <c>Dashboard.AuthMode = Password</c>, the setup-link API is authenticated by the
    /// operator's dashboard session cookie, so mutations also need the same-origin proof.
    /// </summary>
    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/sso-portal/sessions")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/sso-portal/sessions")]
    [Covers("POST /sqlos/admin/auth/api/sso-portal/sessions/{sessionId}/revoke")]
    public async Task In_password_mode_setup_links_need_the_operator_session_and_same_origin_proof()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardPassword);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var sessions = $"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions";

        var link = t.Observe(await t.Operator.PostJsonAsync(sessions, new { }), "the signed-in operator issues a link, sending X-SqlOS-Request and Origin");
        var linkId = link.JsonString("id");
        t.Observe(await t.Operator.PostJsonAsync(sessions, new { }, options => options.WithoutCredentials()), "the session cookie without the same-origin proof");
        t.Observe(
            await t.Operator.PostJsonAsync(sessions, new { }, options => options.WithoutCookies().Header("X-SqlOS-Request", "1")),
            "the proof without the session cookie");
        t.Observe(await t.Operator.GetAsync(sessions), "list the links");
        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/sso-portal/sessions/{linkId}/revoke", new { reason = "rotated" }),
            "revoke it");

        await t.ObserveAuditAsync("link events and the CSRF rejection");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/sso-portal/sessions")]
    [Covers("POST /sqlos/admin/auth/api/sso-portal/sessions")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/sso-portal/sessions")]
    [Covers("POST /sqlos/admin/auth/api/sso-portal/sessions/{sessionId}/revoke")]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}")]
    public async Task Setup_link_administration_refuses_invalid_requests()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        const string UnknownOrganization = "org_ffffffffffffffffffffffffffffffff";

        t.Observe(await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/organizations/{UnknownOrganization}/sso-portal/sessions", new { }), "an unknown organization");
        t.Observe(await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sso-portal/sessions", new { organizationId = UnknownOrganization }), "an unknown organization through the top-level route");
        t.Observe(await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sso-portal/sessions", new { }), "no organization at all");
        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", new { expiresAt = DateTime.UtcNow.AddMinutes(-5) }),
            "an expiry in the past");
        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", new { provider = "adfs" }),
            "a provider the portal has no guide for");
        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", new { organizationId = globex.Id }),
            "the route's organization wins over one in the body");
        t.Observe(await t.Operator.GetAsync($"/sqlos/admin/auth/api/organizations/{globex.Id}/sso-portal/sessions"), "so Globex has no links");
        t.Observe(await t.Operator.GetAsync($"/sqlos/admin/auth/api/organizations/{UnknownOrganization}/sso-portal/sessions"), "listing an unknown organization's links");
        t.Observe(await t.Operator.GetAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions?page=2"), "offset pagination is refused");
        t.Observe(await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sso-portal/sessions/ssp_ffffffffffffffffffffffffffffffff/revoke", new { }), "revoking an unknown link");

        t.Observe(
            await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/organizations/{globex.Id}", new { name = globex.Name, slug = globex.Slug, isActive = false }),
            "deactivate Globex");
        t.Observe(await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/organizations/{globex.Id}/sso-portal/sessions", new { }), "an inactive organization gets no setup link");

        t.Observe(await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", new { }, options => options.WithoutCredentials()), "without the operator credential");
        t.Observe(await t.Operator.GetAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", options => options.WithoutCredentials()), "listing without the operator credential");

        await t.ObserveAuditAsync("portal session events");
        await t.ApproveAsync();
    }
}
