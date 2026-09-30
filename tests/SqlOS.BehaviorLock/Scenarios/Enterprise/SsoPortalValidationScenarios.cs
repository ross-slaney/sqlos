using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// What the SSO portal refuses: cookie-authenticated mutations without the same-origin proof,
/// unusable domains and metadata, and actions taken out of order.
/// </summary>
[TestClass]
public sealed class SsoPortalValidationScenarios
{
    private const string Api = PortalVisit.ApiPath;

    [Scenario]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/provider")]
    [Covers("GET /sqlos/admin/auth/sso-portal/api/state")]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/setup/provider")]
    public async Task Portal_mutations_need_the_same_origin_proof_the_portal_script_sends()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var portal = await t.OpenSsoPortalAsync(acme);
        var browser = portal.Browser;
        var body = new { provider = "okta" };

        t.Observe(await browser.GetAsync($"{Api}/state"), "a read needs no proof");
        t.Observe(await browser.PutJsonAsync($"{Api}/provider", body), "a same-origin mutation without X-SqlOS-Request");
        t.Observe(await browser.PutJsonAsync($"{Api}/provider", body, options => options.Header("X-SqlOS-Request", "true")), "X-SqlOS-Request with a value other than 1");
        t.Observe(
            await browser.PutJsonAsync($"{Api}/provider", body, options => options.Header("X-SqlOS-Request", "1").Header("X-SqlOS-Request", "1")),
            "X-SqlOS-Request sent twice");
        t.Observe(
            await browser.PutJsonAsync($"{Api}/provider", body, options => options.Header("X-SqlOS-Request", "1").WithOrigin("https://evil.example.test")),
            "the header from a cross-site page");
        t.Observe(
            await browser.PutJsonAsync($"{Api}/provider", body, options => options.Header("X-SqlOS-Request", "1").WithOrigin("null")),
            "the header from an opaque origin");
        t.Observe(
            await browser.PutJsonAsync($"{Api}/provider", body, options => options.Header("X-SqlOS-Request", "1").WithoutOrigin().Header("Referer", "https://evil.example.test/page")),
            "no Origin and a cross-site Referer");
        t.Observe(
            await browser.PutJsonAsync($"{Api}/provider", body, options => options.Header("X-SqlOS-Request", "1").WithoutOrigin()),
            "no Origin, Referer, or Sec-Fetch-Site");
        t.Observe(
            await browser.PutJsonAsync($"{Api}/provider", body, options => options.Header("X-SqlOS-Request", "1").WithoutOrigin().Header("Referer", $"{BehaviorLockConstants.PublicOrigin}{PortalVisit.PortalPath}")),
            "no Origin but a same-origin Referer is accepted");
        t.Observe(
            await browser.PutJsonAsync($"{Api}/provider", new { provider = "entra" }, options => options.Header("X-SqlOS-Request", "1").WithoutOrigin().Header("Sec-Fetch-Site", "same-origin")),
            "Sec-Fetch-Site: same-origin alone is accepted");
        t.Observe(await browser.PutJsonAsync($"{PortalVisit.SetupApiPath}/provider", body), "the setup API applies the same rule");
        t.Observe(await browser.PutJsonAsync($"{Api}/provider", new { provider = "generic" }, PortalVisit.SameOrigin), "the portal script's request");

        await t.ObserveAuditAsync("each rejection is audited with its reason");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("PUT /sqlos/admin/auth/sso-portal/api/provider")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/domain")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/domains/{domainId}/confirm")]
    public async Task The_portal_refuses_unsupported_providers_and_unverifiable_domains()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var taken = await t.VerifyDomainAsync(globex, "taken");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var portal = await t.OpenSsoPortalAsync(acme);
        var fromUrl = t.Unique.Domain("acme");
        t.Scrub(fromUrl.ToUpperInvariant(), "domain", "ACME");
        t.Note("Globex has already verified {domain:taken}.");

        t.Observe(await portal.PutAsync($"{Api}/provider", new { provider = "adfs" }), "a provider the portal has no guide for");
        t.Observe(await portal.PutAsync($"{Api}/provider", new { provider = " " }), "a blank provider");
        foreach (var (domain, caption) in new[]
        {
            (" ", "a blank domain"),
            ("*.acme.example.test", "a wildcard domain"),
            ("203.0.113.5", "an IP address"),
            ("localhost", "localhost"),
            ("intranet", "a name without a public suffix"),
            ("acme_corp.example.test", "an underscore in a label"),
            ("-acme.example.test", "a label that starts with a hyphen"),
            (new string('a', 64) + ".example.test", "a label longer than 63 characters"),
            (taken, "a domain another organization has verified")
        })
        {
            t.Observe(await portal.PostAsync($"{Api}/domain", new { domain }), caption);
        }

        var started = t.Observe(
            await portal.PostAsync($"{Api}/domain", new { domain = $"https://{fromUrl.ToUpperInvariant()}/login" }),
            "a pasted URL is reduced to its lower-case host");
        t.ScrubDomainVerification(started);
        t.Observe(await portal.PostAsync($"{Api}/domain", new { domain = $"it-admin@{fromUrl}" }), "an email address is reduced to its domain: the same pending claim");
        t.Observe(await portal.PostAsync($"{Api}/domains/dom_ffffffffffffffffffffffffffffffff/confirm", new { }), "confirming a domain that was never started");

        await t.ObserveAuditAsync("provider and domain events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/metadata/validate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/metadata")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/activate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/test")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/organization-sessions/revoke")]
    public async Task The_portal_refuses_unusable_metadata_and_out_of_order_actions()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise, EnterpriseHosting.AnswerUnhandledExceptionsLikeKestrel);
        using var idp = new TestSamlIdentityProvider();
        using var other = new TestSamlIdentityProvider("urn:behavior-lock:other-idp", "https://other-idp.example.test/sso");
        idp.RegisterWith(t);
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        await t.Setup.CreateSamlConnectionAsync(globex, other);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var portal = await t.OpenSsoPortalAsync(acme);
        var certificate = Convert.ToBase64String(idp.Certificate.RawData);
        var metadata = SamlMetadata.For(idp.EntityId, idp.SingleSignOnUrl, certificate);
        using var expiredKey = RSA.Create(2048);
        using var expiredCertificate = new CertificateRequest("CN=Expired IdP", expiredKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        t.Scrub(Convert.ToBase64String(expiredCertificate.RawData), "expired-certificate");
        t.Note("The host answers an exception that escapes an endpoint as Kestrel would: 500 with no body. Globex's connection already uses the IdP entity ID urn:behavior-lock:other-idp.");

        foreach (var (xml, caption) in new[]
        {
            ("not xml at all", "text that is not XML"),
            ("<md:EntityDescriptor xmlns:md=\"urn:oasis:names:tc:SAML:2.0:metadata\" />", "no entityID"),
            (metadata.Replace("<md:SingleSignOnService", "<md:ArtifactResolutionService", StringComparison.Ordinal), "no SingleSignOnService"),
            (metadata.Replace("https://idp.example.test/sso", "http://idp.example.test/sso", StringComparison.Ordinal), "an SSO URL that is not HTTPS"),
            (metadata.Replace("use=\"signing\"", "use=\"encryption\"", StringComparison.Ordinal), "only an encryption certificate"),
            (metadata.Replace(certificate, "%%%not-base64%%%", StringComparison.Ordinal), "a certificate that is not base64"),
            (SamlMetadata.For(idp.EntityId, idp.SingleSignOnUrl, Convert.ToBase64String(expiredCertificate.RawData)), "an expired certificate"),
            (metadata, "valid metadata")
        })
        {
            t.Observe(await portal.PostAsync($"{Api}/metadata/validate", new { metadataXml = xml }), $"validate {caption}");
        }

        t.Observe(await portal.PostAsync($"{Api}/activate", null), "activate before importing metadata");
        t.Observe(await portal.PostAsync($"{Api}/test", null), "test before activation: recorded as blocked");
        t.Observe(await portal.PostAsync($"{Api}/organization-sessions/revoke", new { confirm = true }), "sign out sessions before activation");
        t.Observe(await portal.PostAsync($"{Api}/metadata", new { metadataXml = "<md:EntityDescriptor xmlns:md=\"urn:oasis:names:tc:SAML:2.0:metadata\" />" }), "import metadata without an entityID");
        t.Observe(await portal.PostAsync($"{Api}/metadata", new { metadataXml = "not xml at all" }), "import text that is not XML");
        t.Observe(
            await portal.PostAsync($"{Api}/metadata", new { metadataXml = SamlMetadata.For(other.EntityId, other.SingleSignOnUrl, Convert.ToBase64String(other.Certificate.RawData)) }),
            "import metadata whose entity ID Globex's connection already uses");
        t.Observe(await portal.PostAsync($"{Api}/metadata", new { metadataXml = metadata }), "import valid metadata");
        t.Observe(await portal.PostAsync($"{Api}/activate", null), "activate without a verified domain");

        await t.ObserveAuditAsync("the import and the blocked test");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/activate")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/test")]
    [Covers("POST /sqlos/admin/auth/sso-portal/api/organization-sessions/revoke")]
    public async Task An_organization_with_only_an_unverified_primary_domain_can_activate_but_not_sign_sessions_out()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        idp.RegisterWith(t);
        var primaryDomain = t.Unique.Domain("legacy");
        var acme = await t.Setup.CreateOrganizationAsync("acme", primaryDomain);
        var portal = await t.OpenSsoPortalAsync(acme);
        t.Note("Acme was created with PrimaryDomain {domain:legacy}, which it never verified.");

        t.Observe(
            await portal.PostAsync($"{Api}/metadata", new { metadataXml = SamlMetadata.For(idp.EntityId, idp.SingleSignOnUrl, Convert.ToBase64String(idp.Certificate.RawData)) }),
            "import metadata");
        t.Observe(await portal.PostAsync($"{Api}/activate", null), "activation accepts the unverified primary domain");
        t.Observe(
            await portal.PostAsync($"{Api}/test", new { clientId = BehaviorLockConstants.AppClientId, redirectUri = BehaviorLockConstants.AppRedirectUri, state = "portal-test-state" }),
            "a test sign-in without a PKCE challenge");
        t.Observe(await portal.PostAsync($"{Api}/organization-sessions/revoke", new { confirm = true }), "signing out sessions needs a verified domain");

        await t.ObserveAuditAsync("metadata import and activation");
        await t.ApproveAsync();
    }
}
