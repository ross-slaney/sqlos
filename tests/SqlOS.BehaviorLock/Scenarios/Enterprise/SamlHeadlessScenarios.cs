using Microsoft.AspNetCore.WebUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// SAML behind a headless sign-in UI: the application's own UI identifies the user through the
/// headless API, the IdP posts to the ACS, and an ACS refusal sends the browser back to the
/// application's UI with the error instead of answering JSON.
/// </summary>
[TestClass]
public sealed class SamlHeadlessScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/headless/identify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task An_acs_refusal_for_a_headless_request_returns_the_browser_to_the_application_ui()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var ivan = t.Unique.Email("ivan", domain);
        var request = t.Urls.Authorize();

        var authorize = t.Observe(await t.GetAsync(request.Url), "authorize sends the browser to the application's sign-in UI");
        var requestId = authorize.NextUrlParameter("request");
        var identified = t.Observe(
            await t.PostJsonAsync("/sqlos/auth/headless/identify", new { requestId, email = ivan }),
            "the UI identifies Ivan: his organization signs in with SAML");
        var authnRequest = TestSamlIdentityProvider.ReadRedirect(identified.JsonString("redirectUrl"));

        var refused = t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                idp.BuildResponse(authnRequest, new SamlAssertion(ivan, "Ivan", "Petrov") { Signing = SamlSigning.OtherKey }), authnRequest.RelayState)),
            "a response signed by the wrong key: back to the application's UI with the public error");

        // The redirect carries the request's UI context, percent-encoded, which hides its values
        // from the scrubber's patterns; name the encoded value and show it decoded instead.
        var location = refused.Location!;
        var encodedContext = location[(location.IndexOf("ui_context=", StringComparison.Ordinal) + "ui_context=".Length)..].Split('&')[0];
        t.Scrub(encodedContext, "ui-context", "saml-request-state");
        t.ObserveDocument("the ui_context in that redirect, decoded", Uri.UnescapeDataString(encodedContext));
        var acs = t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                idp.BuildResponse(authnRequest, new SamlAssertion(ivan, "Ivan", "Petrov")), authnRequest.RelayState)),
            "a valid response completes the same request");
        var code = QueryHelpers.ParseQuery(new Uri(acs.NextUrl!).Query)["code"].ToString();
        t.Observe(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(code)), "redeem the code");

        await t.ObserveAuditAsync("the refusal and the sign-in");
        await t.ApproveAsync();
    }
}
