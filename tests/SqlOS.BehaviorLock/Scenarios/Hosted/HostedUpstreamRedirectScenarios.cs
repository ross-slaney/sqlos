using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// Hosted routes that hand the browser to an upstream identity provider: the provider buttons
/// (<c>GET /login/oidc/{connectionId}</c>, whose Google happy path the social pilot locks) and the
/// home-realm SSO redirect that the password and sign-up forms take for an address at an
/// organization's verified domain.
/// </summary>
[TestClass]
public sealed partial class HostedUpstreamRedirectScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/login/oidc/{connectionId}")]
    public async Task A_provider_button_needs_an_authorization_request()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var begun = await HostedFlows.BeginAsync(t);
        var custom = ProviderLink(begun.Page, "Example OIDC");

        t.Observe(
            await t.GetAsync($"{custom}?email=someone%40example.test"),
            "a provider link without a request: the sign-in page with an error");
        t.Observe(
            await t.GetAsync($"{custom}?request={begun.RequestId}&email=someone%40example.test"),
            "the custom OIDC provider: discovery is fetched, then the browser goes to its authorization endpoint");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/oidc/{connectionId}")]
    public async Task Unknown_connections_and_requests_are_unhandled_errors()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, HostedFlows.AnswerUnhandledExceptionsLikeKestrel);
        var begun = await HostedFlows.BeginAsync(t);
        var google = ProviderLink(begun.Page, "Google");

        t.Observe(
            await t.GetAsync($"/sqlos/auth/login/oidc/not-a-connection?request={begun.RequestId}&email="),
            "a connection SqlOS does not have");
        t.Observe(
            await t.GetAsync($"{google}?request=not-a-request&email="),
            "a request SqlOS does not know");
        t.Observe(
            await t.GetAsync($"{google}?request={begun.RequestId}&email=&invitationToken=not-an-invitation"),
            "an invitation token SqlOS cannot resolve");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/oidc/{connectionId}")]
    public async Task An_invitation_carried_by_a_provider_button_replaces_the_typed_address()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var invited = t.Unique.Email("carol");
        var token = await HostedFlows.InviteAsync(t, acme, invited);
        var begun = await HostedFlows.BeginAsync(t);
        var google = ProviderLink(begun.Page, "Google");

        t.Observe(
            await t.GetAsync($"{google}?request={begun.RequestId}&email=someone-else%40example.test&invitationToken={Uri.EscapeDataString(token)}"),
            "start Google sign-in with an invitation: the invited address becomes the login hint");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/signup/submit")]
    public async Task Password_and_sign_up_forms_send_an_sso_domain_to_the_organizations_identity_provider()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.Setup.VerifyDomainAsync(acme, "acme");
        await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var ivan = t.Unique.Email("ivan", domain);

        var passwordRequest = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var toIdp = t.Observe(
            await t.SubmitAsync(passwordRequest.Page.Form("/login/password").With("email", ivan).With("password", "Not-Checked-1")),
            "a password for an address at the verified SSO domain: redirected to the IdP, the password is never checked");
        t.ObserveDocument("the AuthnRequest", TestSamlIdentityProvider.ReadRedirect(toIdp.NextUrl!).Xml);

        var signupRequest = await HostedFlows.BeginAsync(t);
        var signup = t.Discard(await t.GetAsync($"/sqlos/auth/signup?request={signupRequest.RequestId}"));
        t.Observe(
            await t.SubmitAsync(signup.Form("/signup/submit")
                .With("displayName", "Ivan")
                .With("email", ivan)
                .With("password", t.Unique.Password("ivan"))),
            "a sign-up at the SSO domain: redirected to the IdP, no account is created");

        await t.ObserveAuditAsync("nothing is audited before the IdP answers");
        await t.ApproveAsync();
    }

    private static string ProviderLink(HttpExchange page, string provider)
        => ProviderHref().Matches(page.ResponseBody)
               .FirstOrDefault(match => match.Groups["label"].Value == provider)?.Groups["href"].Value
           ?? throw new InvalidOperationException($"The page has no '{provider}' provider link.");

    [GeneratedRegex("""href="(?<href>/sqlos/auth/login/oidc/[^"?]+)\?[^"]*"[^>]*data-loading-label="Connecting to (?<label>[^"]+)""")]
    private static partial Regex ProviderHref();
}
