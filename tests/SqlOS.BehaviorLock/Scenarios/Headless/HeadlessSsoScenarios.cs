using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>
/// Home-realm discovery inside the headless API: an address at a domain an organization verified,
/// with an enabled SAML connection, is sent to that organization's identity provider instead of
/// any local credential.
/// </summary>
[TestClass]
public sealed class HeadlessSsoScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/headless/identify")]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Identify_sends_an_address_at_a_verified_sso_domain_to_the_organization_idp()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.Setup.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var email = t.Unique.Email("ivan", domain);
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);

        var identified = t.Observe(
            await t.PostJsonAsync($"{Api}/identify", new { requestId, email }),
            "identify: the domain belongs to an organization with SSO, so the UI gets a redirect to its identity provider");
        var authnRequest = TestSamlIdentityProvider.ReadRedirect(identified.JsonString("redirectUrl"));
        t.ObserveDocument("the AuthnRequest SqlOS sent to the identity provider", authnRequest.Xml);
        var acs = t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", new Dictionary<string, string>
            {
                ["SAMLResponse"] = idp.BuildResponse(authnRequest, new SamlAssertion(email, "Ivan", "Petrov")),
                ["RelayState"] = authnRequest.RelayState
            }),
            "the identity provider posts a signed response: the headless request completes with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(acs.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("SSO sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/headless/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/magic-link/start")]
    [Covers("POST /sqlos/auth/headless/signup")]
    [Covers("POST /sqlos/auth/headless/signup/email-otp/start")]
    public async Task Every_local_credential_route_sends_an_sso_domain_address_to_the_identity_provider()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.Setup.VerifyDomainAsync(acme, "acme");
        await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var email = t.Unique.Email("ivan", domain);

        t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize()), email, password = "never-checked-password" }),
            "password sign-in: redirected to the identity provider, the password is never checked");
        t.Observe(
            await t.PostJsonAsync($"{Api}/email-otp/start", new { requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize()), email }),
            "email code: redirected, no code is sent");
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/start", new { requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize()), email }),
            "magic link: redirected, no link is sent");
        t.Observe(
            await t.PostJsonAsync($"{Api}/signup", new
            {
                requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize()),
                displayName = "Ivan",
                email,
                password = "never-used-password"
            }),
            "password signup: redirected, no account is created");
        t.Observe(
            await t.PostJsonAsync($"{Api}/signup/email-otp/start", new
            {
                requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize()),
                displayName = "Ivan",
                email
            }),
            "email-code signup: redirected, no code is sent");

        await t.ObserveAuditAsync("no credential events");
        await t.ApproveAsync();
    }
}
