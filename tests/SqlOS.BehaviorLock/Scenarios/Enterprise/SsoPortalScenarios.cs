using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// The hosted SSO setup portal a customer's IT admin uses: open the operator's setup link, pick a
/// provider, verify a domain through DNS, import IdP metadata, activate, test, sign existing
/// sessions out, and sign out of the portal.
/// </summary>
[TestClass]
public sealed class SsoPortalScenarios
{
    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/sso-portal/sessions")]
    [Covers("GET /sqlos/admin/auth/sso-portal/start")]
    [Covers("GET /sqlos/admin/auth/sso-portal/")]
    [Covers("GET /sqlos/admin/auth/sso-portal/api/state")]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/provider")]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/enrollment-policy")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/domain")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/domains/{domainId}/confirm")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/metadata/validate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/metadata")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/activate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/test")]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/disable")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/signout")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/sso-portal/sessions")]
    public async Task A_customer_admin_sets_up_saml_sso_through_the_hosted_portal()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        idp.RegisterWith(t);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = t.Unique.Domain("acme");
        var ivan = t.Unique.Email("ivan", domain);

        var link = t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", new { }),
            "the operator issues a setup link and sends it to Acme's IT admin");
        var admin = t.NewBrowser("customer-admin");
        t.Observe(await admin.GetAsync(new Uri(link.JsonString("setupUrl")).PathAndQuery), "the IT admin opens the link: a portal session cookie replaces the link token");
        t.Observe(await admin.GetAsync(PortalVisit.PortalPath), "the hosted portal page");
        t.Observe(await admin.GetAsync($"{PortalVisit.ApiPath}/state"), "the portal loads its state: a draft connection and the values to give the IdP");

        var portal = new PortalVisit(admin, link.JsonString("id"), link.JsonString("setupUrl"));
        t.Observe(await portal.PutAsync($"{PortalVisit.ApiPath}/provider", new { provider = "okta" }), "choose Okta");
        t.Observe(
            await portal.PutAsync($"{PortalVisit.ApiPath}/enrollment-policy", new { requireSsoForExistingMembers = true, allowJitProvisioning = true }),
            "require SSO for existing members and allow just-in-time provisioning");
        var started = t.Observe(await portal.PostAsync($"{PortalVisit.ApiPath}/domain", new { domain }), "start verifying Acme's domain");
        t.ScrubDomainVerification(started);
        var domainId = started.JsonString("domain.id");
        t.Observe(
            await portal.PostAsync($"{PortalVisit.ApiPath}/domains/{domainId}/confirm", new { }),
            "confirm before the TXT record exists: still pending, with the record to create");
        t.Setup.PublishDnsTxt(started.JsonString("domain.ownershipRecord.name"), started.JsonString("domain.ownershipRecord.value"));
        t.Note("Acme publishes the TXT record.");
        t.Observe(await portal.PostAsync($"{PortalVisit.ApiPath}/domains/{domainId}/confirm", new { }), "confirm again: the domain is verified");

        var metadata = SamlMetadata.For(idp.EntityId, idp.SingleSignOnUrl, Convert.ToBase64String(idp.Certificate.RawData));
        t.Observe(await portal.PostAsync($"{PortalVisit.ApiPath}/metadata/validate", new { metadataXml = metadata }), "check the IdP metadata");
        t.Observe(await portal.PostAsync($"{PortalVisit.ApiPath}/metadata", new { metadataXml = metadata }), "import it: the connection is ready to activate");
        t.Observe(await portal.PostAsync($"{PortalVisit.ApiPath}/activate", null), "activate the connection");
        t.Observe(await portal.PostAsync($"{PortalVisit.ApiPath}/test", null), "test without an application: ready");

        var request = t.Urls.Authorize();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)));
        var test = t.Observe(
            await portal.PostAsync($"{PortalVisit.ApiPath}/test", new
            {
                clientId = BehaviorLockConstants.AppClientId,
                redirectUri = BehaviorLockConstants.AppRedirectUri,
                state = request.State,
                codeChallenge = challenge,
                codeChallengeMethod = "S256"
            }),
            "test with the application: the portal returns an IdP redirect");
        var authnRequest = TestSamlIdentityProvider.ReadRedirect(test.JsonString("authorizationUrl"));
        t.ObserveDocument("the AuthnRequest the test sign-in sends to the IdP", authnRequest.Xml);
        var employee = t.NewBrowser("employee");
        var acs = t.Observe(
            await employee.PostFormAsync($"/sqlos/auth/saml/acs/{authnRequest.AssertionConsumerServiceUrl.Split('/').Last()}", new Dictionary<string, string>
            {
                ["SAMLResponse"] = idp.BuildResponse(authnRequest, new SamlAssertion(ivan, "Ivan", "Petrov")),
                ["RelayState"] = authnRequest.RelayState
            }),
            "the IdP posts Ivan's signed assertion: he is provisioned and sent back with a code");
        t.Observe(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(acs.NextUrlParameter("code"))), "the application redeems the code");
        t.Observe(await admin.GetAsync($"{PortalVisit.ApiPath}/state"), "the portal shows the active connection and the latest test");

        t.Observe(await portal.PostAsync($"{PortalVisit.ApiPath}/disable", null), "disable SSO");
        t.Observe(await portal.PostAsync($"{PortalVisit.ApiPath}/signout", null), "sign out of the portal");
        t.Observe(await admin.GetAsync($"{PortalVisit.ApiPath}/state"), "the portal session is gone");
        t.Observe(await admin.GetAsync(new Uri(link.JsonString("setupUrl")).PathAndQuery), "the setup link cannot be opened a second time");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", "dashboard: the link was opened and signed out");
        await t.ObserveAuditAsync("portal, domain, metadata, and sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/organization-sessions/revoke")]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task Activating_sso_can_sign_out_the_organizations_existing_sessions()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var legacyEmail = t.Unique.Email("legacy", domain);
        var legacyPassword = t.Unique.Password("legacy");
        var legacy = await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/users", new { displayName = "Legacy", email = legacyEmail, password = legacyPassword });
        var legacyUser = new ScenarioUser(legacy.JsonString("id"), legacyEmail, legacyPassword, "Legacy");
        t.Scrub(legacyUser.Id, "usr", "legacy");
        await t.Setup.AddMembershipAsync(acme, legacyUser);
        var legacySession = await t.Setup.SignInWithPasswordAsync(legacyUser, browser: t.NewBrowser("legacy"));
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var ivan = t.Unique.Email("ivan", domain);
        t.Note("Before SSO existed, Legacy (an Acme member whose address at Acme's domain was never verified) signed in with a password.");

        var employee = t.NewBrowser("employee");
        var request = t.Urls.Authorize();
        var page = t.Discard(await employee.GetAsync(request.Url));
        var toIdp = t.Discard(await employee.SubmitAsync(page.Form("/login/identify").With("email", ivan)));
        var authnRequest = TestSamlIdentityProvider.ReadRedirect(toIdp.NextUrl!);
        var acs = t.Observe(
            await employee.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", new Dictionary<string, string>
            {
                ["SAMLResponse"] = idp.BuildResponse(authnRequest, new SamlAssertion(ivan, "Ivan", "Petrov")),
                ["RelayState"] = authnRequest.RelayState
            }),
            "Ivan signs in through Acme's IdP");
        var tokens = t.Observe(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(acs.NextUrlParameter("code"))), "redeem Ivan's code");
        var silent = t.Urls.Authorize();
        t.Observe(await employee.GetAsync(silent.Url), "Ivan's browser session signs him in silently");
        await t.SkipAuditAsync();

        var portal = await t.OpenSsoPortalAsync(acme);
        t.Observe(await portal.PostAsync($"{PortalVisit.ApiPath}/organization-sessions/revoke", new { confirm = false }), "revoking without confirmation");
        t.Observe(await portal.PostAsync($"{PortalVisit.ApiPath}/organization-sessions/revoke", new { confirm = true }), "confirm: sign out everyone at Acme's verified domain");

        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.JsonString("refresh_token"),
                ["client_id"] = BehaviorLockConstants.AppClientId
            }),
            "Ivan's refresh token is revoked");
        var again = t.Urls.Authorize();
        t.Observe(await employee.GetAsync(again.Url), "his browser session no longer signs him in");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = legacySession.RefreshToken,
                ["client_id"] = BehaviorLockConstants.AppClientId
            }),
            "Legacy's session was not in scope: its unverified address is not proof of the domain");

        await t.ObserveAuditAsync("the organization sign-out");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/sso-portal/start")]
    [Covers("GET /sqlos/admin/auth/sso-portal/api/state")]
    public async Task Setup_links_open_once_and_only_while_they_are_live()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var session = await t.Setup.OperatorPostAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", new { });
        var setupPath = new Uri(session.JsonString("setupUrl")).PathAndQuery;
        var admin = t.NewBrowser("customer-admin");

        t.Observe(await admin.GetAsync("/sqlos/admin/auth/sso-portal/start"), "no token");
        t.Observe(await admin.GetAsync("/sqlos/admin/auth/sso-portal/start?token=not-a-link-this-host-issued"), "a token this host never issued");
        t.Observe(await admin.GetAsync(setupPath), "the real link");
        t.Observe(await t.NewBrowser("forwarded-to").GetAsync(setupPath), "the same link in another browser");
        t.Observe(await admin.GetAsync(setupPath), "the same link again in the first browser");
        t.Observe(await admin.GetAsync($"{PortalVisit.ApiPath}/state"), "the first browser's portal session still works");

        await t.ObserveAuditAsync("the link opened once");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/sso-portal/api/state")]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/provider")]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/enrollment-policy")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/domain")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/domains/{domainId}/confirm")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/metadata/validate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/metadata")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/activate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/disable")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/organization-sessions/revoke")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/test")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/signout")]
    [Covers("POST /sqlos/admin/auth/api/sso-portal/sessions/{sessionId}/revoke")]
    public async Task The_portal_api_requires_a_live_portal_session()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var visitor = t.NewBrowser("visitor");
        var api = PortalVisit.ApiPath;

        t.Note("A browser that never opened a setup link:");
        t.Observe(await visitor.GetAsync($"{api}/state"), "state");
        t.Observe(await visitor.PutJsonAsync($"{api}/provider", new { provider = "okta" }), "choose a provider");
        t.Observe(await visitor.PutJsonAsync($"{api}/enrollment-policy", new { requireSsoForExistingMembers = true, allowJitProvisioning = true }), "change the enrollment policy");
        t.Observe(await visitor.PostJsonAsync($"{api}/domain", new { domain = "acme.example.test" }), "start domain verification");
        t.Observe(await visitor.PostJsonAsync($"{api}/domains/dom_ffffffffffffffffffffffffffffffff/confirm", new { }), "confirm a domain");
        t.Observe(await visitor.PostJsonAsync($"{api}/metadata/validate", new { metadataXml = "<md:EntityDescriptor/>" }), "validate metadata");
        t.Observe(await visitor.PostJsonAsync($"{api}/metadata", new { metadataXml = "<md:EntityDescriptor/>" }), "import metadata");
        t.Observe(await visitor.PostJsonAsync($"{api}/activate", new { }), "activate");
        t.Observe(await visitor.PostJsonAsync($"{api}/disable", new { }), "disable");
        t.Observe(await visitor.PostJsonAsync($"{api}/organization-sessions/revoke", new { confirm = true }), "sign out the organization's sessions");
        t.Observe(await visitor.PostJsonAsync($"{api}/test", new { }), "test");
        t.Observe(await visitor.PostJsonAsync($"{api}/signout", new { }), "sign out with no session still succeeds");

        var portal = await t.OpenSsoPortalAsync(acme);
        t.Observe(await portal.Browser.GetAsync($"{api}/state"), "an opened portal session works");
        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/sso-portal/sessions/{portal.SessionId}/revoke", new { reason = "offboarded" }),
            "the operator revokes it");
        t.Observe(await portal.Browser.GetAsync($"{api}/state"), "the revoked session is refused and its cookie cleared");
        t.Observe(await portal.Browser.GetAsync($"{api}/state"), "the browser no longer sends a portal cookie");
        t.Observe(
            await visitor.GetAsync($"{api}/state", options => options.Cookie("sqlos_sso_portal=forged-session-value")),
            "a forged portal cookie is refused and cleared");

        await t.ObserveAuditAsync("only the operator's revocation is audited");
        await t.ApproveAsync();
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
