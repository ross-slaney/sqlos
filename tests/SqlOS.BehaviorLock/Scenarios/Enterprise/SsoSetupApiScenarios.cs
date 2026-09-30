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
/// The SSO setup action API (<c>/sqlos/admin/auth/sso-portal/api/setup</c>) a host's own setup UI
/// drives. Every action answers with the next view and a complete view model; a refused action is
/// a 200 that carries the error and field errors rather than a 400.
/// </summary>
[TestClass]
public sealed class SsoSetupApiScenarios
{
    private const string Setup = PortalVisit.SetupApiPath;

    [Scenario]
    [Covers("GET /sqlos/admin/auth/sso-portal/api/setup/")]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/setup/provider")]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/setup/enrollment-policy")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/domain")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/domains/{domainId}/confirm")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/metadata/validate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/metadata")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/activate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/test")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/organization-sessions/revoke")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/disable")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/signout")]
    public async Task A_custom_setup_ui_walks_the_setup_actions_from_provider_to_sign_out()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        idp.RegisterWith(t);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = t.Unique.Domain("acme");
        var portal = await t.OpenSsoPortalAsync(acme);
        var ui = portal.Browser;

        t.Observe(await ui.GetAsync($"{Setup}"), "the first view");
        t.Observe(await ui.GetAsync($"{Setup}?view=domains"), "a named view");
        t.Observe(await ui.GetAsync($"{Setup}?view=billing"), "an unknown view falls back to the provider view");
        t.Observe(await portal.PutAsync($"{Setup}/provider", new { provider = "google" }), "choose Google Workspace: next is the domain view");
        t.Observe(
            await portal.PutAsync($"{Setup}/enrollment-policy", new { requireSsoForExistingMembers = false, allowJitProvisioning = true }),
            "set the enrollment policy: the answer returns to the provider view");

        var started = t.Observe(await portal.PostAsync($"{Setup}/domain", new { domain }), "start domain verification");
        t.ScrubDomainVerification(started, "viewModel.domain.ownershipRecord.value");
        var domainId = started.JsonString("viewModel.domain.id");
        t.Observe(await portal.PostAsync($"{Setup}/domains/{domainId}/confirm", new { }), "confirm too early: the domain view shows the missing record");
        t.Setup.PublishDnsTxt(started.JsonString("viewModel.domain.ownershipRecord.name"), started.JsonString("viewModel.domain.ownershipRecord.value"));
        t.Note("Acme publishes the TXT record.");
        t.Observe(await portal.PostAsync($"{Setup}/domains/{domainId}/confirm", new { }), "confirm: next is the metadata view");

        var metadata = SamlMetadata.For(idp.EntityId, idp.SingleSignOnUrl, Convert.ToBase64String(idp.Certificate.RawData), "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST");
        t.Observe(await portal.PostAsync($"{Setup}/metadata/validate", new { metadataXml = metadata }), "validate metadata that offers only an HTTP-POST endpoint");
        t.Observe(await portal.PostAsync($"{Setup}/metadata", new { metadataXml = metadata }), "import it: next is the activation view");
        t.Observe(await portal.PostAsync($"{Setup}/activate", null), "activate: next is the test view");
        t.Observe(await portal.PostAsync($"{Setup}/test", null), "test without an application");

        var request = t.Urls.Authorize();
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        t.Observe(
            await portal.PostAsync($"{Setup}/test", new
            {
                clientId = BehaviorLockConstants.AppClientId,
                redirectUri = BehaviorLockConstants.AppRedirectUri,
                state = request.State,
                codeChallenge = challenge,
                codeChallengeMethod = "S256"
            }),
            "test with the application: the view records a started test (the IdP URL is not returned here)");
        t.Observe(await portal.PostAsync($"{Setup}/organization-sessions/revoke", new { confirm = true }), "sign out the organization's existing sessions");
        t.Observe(await portal.PostAsync($"{Setup}/disable", null), "disable: back to the activation view");
        t.Observe(await portal.PostAsync($"{Setup}/signout", null), "sign out: the UI is told to redirect");
        t.Observe(await ui.GetAsync($"{Setup}"), "the session is gone");

        await t.ObserveAuditAsync("setup events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/setup/provider")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/domain")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/domains/{domainId}/confirm")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/metadata/validate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/metadata")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/activate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/test")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/organization-sessions/revoke")]
    public async Task Refused_setup_actions_return_the_view_with_the_error()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var portal = await t.OpenSsoPortalAsync(acme);

        t.Observe(await portal.PutAsync($"{Setup}/provider", new { provider = "adfs" }), "an unsupported provider: the provider view with a field error");
        t.Observe(await portal.PostAsync($"{Setup}/domain", new { domain = "*.acme.example.test" }), "a wildcard domain: the domain view with a field error");
        t.Observe(await portal.PostAsync($"{Setup}/domains/dom_ffffffffffffffffffffffffffffffff/confirm", new { }), "confirming a domain that was never started");
        t.Observe(await portal.PostAsync($"{Setup}/metadata/validate", new { metadataXml = "<md:EntityDescriptor xmlns:md=\"urn:oasis:names:tc:SAML:2.0:metadata\" />" }), "validating metadata without an entityID");
        t.Observe(await portal.PostAsync($"{Setup}/metadata", new { metadataXml = "<md:EntityDescriptor xmlns:md=\"urn:oasis:names:tc:SAML:2.0:metadata\" />" }), "importing it: the metadata view with a field error");
        t.Observe(await portal.PostAsync($"{Setup}/activate", null), "activating without metadata: the activation view with the error");
        t.Observe(await portal.PostAsync($"{Setup}/test", null), "testing a disabled connection records a blocked test");
        t.Observe(await portal.PostAsync($"{Setup}/organization-sessions/revoke", new { confirm = false }), "signing out sessions without confirmation is a 400");
        t.Observe(await portal.PostAsync($"{Setup}/organization-sessions/revoke", new { confirm = true }), "and before activation also a 400");

        await t.ObserveAuditAsync("only the blocked test is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/sso-portal/api/setup/")]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/setup/provider")]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/setup/enrollment-policy")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/domain")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/domains/{domainId}/confirm")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/metadata/validate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/metadata")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/activate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/disable")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/organization-sessions/revoke")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/test")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/setup/signout")]
    public async Task The_setup_api_requires_a_portal_session()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        var visitor = t.NewBrowser("visitor");

        t.Observe(await visitor.GetAsync($"{Setup}"), "the view");
        t.Observe(await visitor.PutJsonAsync($"{Setup}/provider", new { provider = "okta" }), "choose a provider");
        t.Observe(await visitor.PutJsonAsync($"{Setup}/enrollment-policy", new { requireSsoForExistingMembers = true, allowJitProvisioning = false }), "change the enrollment policy");
        t.Observe(await visitor.PostJsonAsync($"{Setup}/domain", new { domain = "acme.example.test" }), "start domain verification");
        t.Observe(await visitor.PostJsonAsync($"{Setup}/domains/dom_ffffffffffffffffffffffffffffffff/confirm", new { }), "confirm a domain");
        t.Observe(await visitor.PostJsonAsync($"{Setup}/metadata/validate", new { metadataXml = "<x/>" }), "validate metadata");
        t.Observe(await visitor.PostJsonAsync($"{Setup}/metadata", new { metadataXml = "<x/>" }), "import metadata");
        t.Observe(await visitor.PostJsonAsync($"{Setup}/activate", new { }), "activate");
        t.Observe(await visitor.PostJsonAsync($"{Setup}/disable", new { }), "disable");
        t.Observe(await visitor.PostJsonAsync($"{Setup}/organization-sessions/revoke", new { confirm = true }), "sign out the organization's sessions");
        t.Observe(await visitor.PostJsonAsync($"{Setup}/test", new { }), "test");
        t.Observe(await visitor.PostJsonAsync($"{Setup}/signout", new { }), "sign out with no session still answers the redirect action");

        await t.ApproveAsync();
    }
}
