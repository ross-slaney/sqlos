using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Protocol.ProtocolForms;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// The hosted consent screen for third-party clients: approve, deny, the remembered grant, and the
/// antiforgery and token checks on the two consent forms.
/// </summary>
[TestClass]
public sealed class ConsentScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/consent/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_partner_client_asks_for_consent_once_and_the_grant_is_remembered()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");

        var request = PartnerRequest(t, view: "password");
        var page = t.Observe(await t.GetAsync(request.Url), "the partner's authorization request opens the password page");
        var consent = t.Observe(
            await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "after the password, SqlOS asks Alice to consent to the partner");
        var approved = await t.ObserveWithAuditAsync(
            await t.SubmitAsync(consent.Form("/consent/approve")),
            "she approves: the grant is remembered and the browser returns to the partner");
        t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(approved.NextUrlParameter("code"))), "the partner redeems the code");

        t.Observe(await t.GetAsync(PartnerRequest(t).Url), "the next request is answered silently: the grant covers its scopes");
        t.Observe(await t.GetAsync(Modified(PartnerRequest(t), ("prompt", "consent"))), "prompt=consent asks again");
        t.Observe(
            await t.GetAsync(PartnerRequest(t, scope: $"openid profile email offline_access {SqlOS.BehaviorLock.Host.BehaviorLockAuthorization.ReadPermission}").Url),
            "a wider scope asks again, with the scope's display name");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(AtlasClients.Portal, AtlasClients.PortalRedirectUri), ("prompt", "consent"))), "prompt=consent means nothing to a first-party client");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/grants", "Alice's remembered grants");
        await t.ObserveAuditAsync("remaining events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/consent/deny")]
    [Covers("POST /sqlos/auth/consent/approve")]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task Denying_consent_sends_access_denied_to_the_partner_and_prompt_none_cannot_ask()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = PartnerRequest(t, view: "password");
        var page = t.Discard(await t.GetAsync(request.Url));
        var consent = t.Observe(
            await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "the consent page");

        await t.ObserveWithAuditAsync(await t.SubmitAsync(consent.Form("/consent/deny")), "Alice denies: the partner receives access_denied");
        await t.ObserveWithAuditAsync(await t.SubmitAsync(consent.Form("/consent/deny")), "denying the same request again");
        await t.ObserveWithAuditAsync(await t.SubmitAsync(consent.Form("/consent/approve")), "approving the denied request");
        t.Observe(await t.GetAsync(Modified(PartnerRequest(t), ("prompt", "none"))), "prompt=none for a client she has not consented to: consent_required");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/consent/approve")]
    [Covers("POST /sqlos/auth/consent/deny")]
    public async Task Consent_forms_refuse_cross_site_tampered_misbound_and_replayed_posts()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = PartnerRequest(t, view: "password");
        var page = t.Discard(await t.GetAsync(request.Url));
        var consent = t.Discard(await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)));
        var otherPage = t.Discard(await t.GetAsync(PartnerRequest(t, view: "password").Url));
        var otherConsent = t.Discard(await t.SubmitAsync(otherPage.Form("/login/password").With("email", alice.Email).With("password", alice.Password)));
        await t.SkipAuditAsync();
        var form = consent.Form("/consent/approve");
        t.Note("Alice has two open consent pages for the partner, from two authorization requests.");

        t.Observe(await t.SubmitAsync(form.Without("__RequestVerificationToken")), "no antiforgery token");
        t.Observe(await t.SubmitAsync(form, options => options.WithOrigin("https://attacker.example")), "a cross-site Origin");
        t.Observe(
            await t.SubmitAsync(form, options => options.WithoutOrigin().Header("Referer", "https://attacker.example/page")),
            "no Origin and a cross-site Referer");
        t.Observe(
            await t.SubmitAsync(form, options => options.WithoutOrigin().Header("Sec-Fetch-Site", "cross-site")),
            "no Origin or Referer and Sec-Fetch-Site: cross-site");
        t.Observe(await t.SubmitAsync(form, options => options.WithoutCookies()), "without the antiforgery cookie");
        await t.ObserveWithAuditAsync(await t.SubmitAsync(form.With("consentToken", "not-a-consent-token")), "a forged consent token");
        await t.ObserveWithAuditAsync(
            await t.SubmitAsync(form.With("requestId", otherConsent.Form("/consent/approve")["requestId"])),
            "this page's consent token posted for the other request");
        await t.ObserveWithAuditAsync(
            await t.SubmitAsync(consent.Form("/consent/deny").With("consentToken", "not-a-consent-token")),
            "a forged token on the deny form");

        var approved = await t.ObserveWithAuditAsync(await t.SubmitAsync(form), "the untouched form is accepted");
        t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(approved.NextUrlParameter("code"))), "the partner redeems the code");
        await t.ObserveWithAuditAsync(await t.SubmitAsync(form), "replaying the accepted form");

        await t.ApproveAsync();
    }

    private static AuthorizationRequest PartnerRequest(Transcript t, string? view = null, string scope = "openid profile email offline_access")
        => t.Urls.Authorize(
            AtlasClients.Partner,
            AtlasClients.PartnerRedirectUri,
            scope,
            view == null ? null : new Dictionary<string, string?> { ["view"] = view });
}
