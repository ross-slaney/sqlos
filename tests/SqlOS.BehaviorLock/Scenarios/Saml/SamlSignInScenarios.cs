using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Saml;

[TestClass]
public sealed class SamlSignInScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/login/identify")]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    public async Task Home_realm_discovery_signs_in_through_the_organization_idp_and_provisions_the_user()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.Setup.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var email = t.Unique.Email("ivan", domain);
        var request = t.Urls.Authorize();

        var page = t.Discard(await t.GetAsync(request.Url));
        var toIdp = t.Observe(
            await t.SubmitAsync(page.Form("/login/identify").With("email", email)),
            "identify with an address at the organization's verified domain");
        var authnRequest = TestSamlIdentityProvider.ReadRedirect(toIdp.NextUrl!);
        t.ObserveDocument("the AuthnRequest SqlOS sent to the identity provider", authnRequest.Xml);

        var acs = t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", new Dictionary<string, string>
            {
                ["SAMLResponse"] = idp.BuildResponse(authnRequest, new SamlAssertion(email, "Ivan", "Petrov")),
                ["RelayState"] = authnRequest.RelayState
            }),
            "the identity provider posts a signed response to the ACS");

        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(acs.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("SAML sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    public async Task A_response_altered_after_signing_is_rejected()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.Setup.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var email = t.Unique.Email("ivan", domain);
        var mallory = t.Unique.Email("mallory", domain);

        var page = t.Discard(await t.GetAsync(t.Urls.Authorize().Url));
        var toIdp = t.Discard(await t.SubmitAsync(page.Form("/login/identify").With("email", email)));
        var authnRequest = TestSamlIdentityProvider.ReadRedirect(toIdp.NextUrl!);

        t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", new Dictionary<string, string>
            {
                ["SAMLResponse"] = idp.BuildResponse(authnRequest, new SamlAssertion(email, "Ivan", "Petrov") { TamperedEmail = mallory }),
                ["RelayState"] = authnRequest.RelayState
            }),
            "the email attribute was rewritten after the IdP signed the response");

        await t.ObserveAuditAsync("rejection events");
        await t.ApproveAsync();
    }
}
