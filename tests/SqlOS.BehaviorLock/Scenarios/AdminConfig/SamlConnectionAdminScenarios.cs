using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// The SAML connection admin API (<c>/sqlos/admin/auth/api/sso-connections</c> and the
/// organization-scoped list): explicit IdP fields, the draft plus federation-metadata path,
/// enable and disable with their audit records, and every validation branch of the certificate,
/// SSO URL, and metadata normalization.
/// </summary>
[TestClass]
public sealed class SamlConnectionAdminScenarios
{
    private const string ConnectionsRoute = "/sqlos/admin/auth/api/sso-connections";
    private const string EntityId = "urn:behavior-lock:acme-idp";
    private const string SingleSignOnUrl = "https://idp.acme.example.test/saml/sso";

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/sso-connections")]
    [Covers("GET /sqlos/admin/auth/api/sso-connections")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/sso-connections")]
    [Covers("POST /sqlos/admin/auth/api/sso-connections/{connectionId}/disable")]
    [Covers("POST /sqlos/admin/auth/api/sso-connections/{connectionId}/enable")]
    public async Task Operator_connects_an_organization_to_a_saml_identity_provider()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Enterprise);
        using var certificate = SamlCertificates.Create(t, "acme");
        var acme = await t.Setup.CreateOrganizationAsync("acme", t.Unique.Domain("acme"));

        t.Observe(
            await t.Operator.GetAsync(ConnectionsRoute, options => options.WithoutCredentials()),
            "without operator credentials the SAML connection list is not found");
        var created = t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, new
            {
                organizationId = acme.Id,
                displayName = " Acme Okta ",
                identityProviderEntityId = $" {EntityId} ",
                singleSignOnUrl = SingleSignOnUrl,
                x509CertificatePem = SamlCertificates.Pem(certificate),
                autoProvisionUsers = true,
                autoLinkByEmail = false,
                emailAttributeName = " mail ",
                firstNameAttributeName = (string?)null,
                lastNameAttributeName = "",
                trustUpstreamMfa = true,
                acceptedAuthnContextClassRefs = new[] { "urn:oasis:names:tc:SAML:2.0:ac:classes:MobileTwoFactorContract", " " }
            }),
            "create an enabled connection from explicit IdP fields");
        var connectionId = created.JsonString("id");

        t.Observe(await t.Operator.GetAsync(ConnectionsRoute), "the connection list shows the service-provider values to give the IdP");
        t.Observe(
            await t.Operator.GetAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-connections"),
            "the organization's connection list");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/disable", new { }),
            "disable the connection");
        t.Observe(
            await t.Operator.GetAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-connections"),
            "a disabled connection with complete metadata is ready to activate");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/enable", new { }),
            "enable it again");

        await t.ObserveAuditAsync("SAML connection changes");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/sso-connections/draft")]
    [Covers("POST /sqlos/admin/auth/api/sso-connections/{connectionId}/enable")]
    [Covers("POST /sqlos/admin/auth/api/sso-connections/{connectionId}/metadata")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/sso-connections")]
    [Covers("GET /sqlos/admin/auth/api/sso-connections")]
    public async Task Operator_completes_a_draft_saml_connection_with_federation_metadata()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Enterprise);
        using var certificate = SamlCertificates.Create(t, "acme");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = t.Unique.Domain("sso");
        t.Scrub(domain.ToUpperInvariant(), "domain", "SSO");

        var draft = t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/draft", new
            {
                organizationId = acme.Id,
                displayName = "Acme draft",
                primaryDomain = $" admin@{domain.ToUpperInvariant()}. ",
                autoProvisionUsers = false,
                autoLinkByEmail = true
            }),
            "start a draft: it is disabled and records the organization's primary domain");
        var connectionId = draft.JsonString("id");

        t.Observe(
            await t.Operator.GetAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-connections"),
            "the draft has no IdP values yet");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/enable", new { }),
            "a draft cannot be enabled before metadata is imported");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/metadata", new
            {
                metadataXml = SamlCertificates.Metadata(EntityId, SingleSignOnUrl, certificate)
            }),
            "importing federation metadata fills the IdP values and enables the connection");
        t.Observe(await t.Operator.GetAsync(ConnectionsRoute), "the connection is active");

        await t.ObserveAuditAsync("draft creation is not audited; the metadata import is");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/sso-connections")]
    [Covers("POST /sqlos/admin/auth/api/sso-connections/draft")]
    [Covers("POST /sqlos/admin/auth/api/sso-connections/{connectionId}/metadata")]
    [Covers("POST /sqlos/admin/auth/api/sso-connections/{connectionId}/enable")]
    [Covers("POST /sqlos/admin/auth/api/sso-connections/{connectionId}/disable")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/sso-connections")]
    public async Task Invalid_saml_connection_requests_are_rejected()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Enterprise);
        using var certificate = SamlCertificates.Create(t, "acme");
        using var expired = SamlCertificates.Create(t, "expired", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        using var future = SamlCertificates.Create(t, "future", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(30));
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var existing = await t.Setup.OperatorPostAsync(ConnectionsRoute, Connection(acme.Id, certificate));
        var draft = await t.Setup.OperatorPostAsync($"{ConnectionsRoute}/draft", new
        {
            organizationId = acme.Id,
            displayName = "Second IdP",
            autoProvisionUsers = false,
            autoLinkByEmail = false
        });
        var draftId = draft.JsonString("id");
        const string unknownConnection = "sso_00000000000000000000000000000000";

        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection("org_00000000000000000000000000000000", certificate)),
            "an unknown organization escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection(acme.Id, certificate, entityId: " ")),
            "a missing IdP entity ID escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection(acme.Id, certificate, entityId: "urn:other", singleSignOnUrl: "http://idp.acme.example.test/saml/sso")),
            "a plain-HTTP SSO URL escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection(acme.Id, certificate, entityId: "urn:other", pem: "-----BEGIN CERTIFICATE-----\nbm90IGEgY2VydGlmaWNhdGU=\n-----END CERTIFICATE-----")),
            "a malformed certificate escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection(acme.Id, expired, entityId: "urn:other")),
            "an expired certificate escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection(acme.Id, future, entityId: "urn:other")),
            "a certificate that is not valid yet escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection(acme.Id, certificate, entityId: "urn:other", displayName: " ")),
            "a missing display name escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync(ConnectionsRoute, Connection(acme.Id, certificate)),
            "a second connection for the same IdP entity ID escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/draft", new
            {
                organizationId = "org_00000000000000000000000000000000",
                displayName = "Nowhere",
                autoProvisionUsers = false,
                autoLinkByEmail = false
            }),
            "a draft for an unknown organization escapes as a server error");

        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{unknownConnection}/metadata", new { metadataXml = SamlCertificates.Metadata("urn:other", SingleSignOnUrl, certificate) }),
            "importing metadata into an unknown connection escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{draftId}/metadata", new { metadataXml = "<md:EntityDescriptor" }),
            "malformed XML escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{draftId}/metadata", new { metadataXml = SamlCertificates.Metadata(null, SingleSignOnUrl, certificate) }),
            "metadata without an entityID escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{draftId}/metadata", new { metadataXml = SamlCertificates.Metadata("urn:other", null, certificate) }),
            "metadata without a single sign-on service escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{draftId}/metadata", new { metadataXml = SamlCertificates.Metadata("urn:other", SingleSignOnUrl, null) }),
            "metadata without a signing certificate escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{draftId}/metadata", new { metadataXml = SamlCertificates.Metadata(EntityId, SingleSignOnUrl, certificate) }),
            "metadata for an IdP another connection already uses escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{unknownConnection}/enable", new { }),
            "enabling an unknown connection escapes as a server error");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{unknownConnection}/disable", new { }),
            "disabling an unknown connection escapes as a server error");
        t.Observe(
            await t.Operator.GetAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-connections?page=2"),
            "offset paging of an organization's connections is refused");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-connections", "only the original connection and the untouched draft exist");
        await t.ObserveAuditAsync("rejected changes write no audit events");
        await t.ApproveAsync();
    }

    private static object Connection(
        string organizationId,
        X509Certificate2 certificate,
        string entityId = EntityId,
        string singleSignOnUrl = SingleSignOnUrl,
        string? pem = null,
        string displayName = "Acme SAML")
        => new
        {
            organizationId,
            displayName,
            identityProviderEntityId = entityId,
            singleSignOnUrl,
            x509CertificatePem = pem ?? SamlCertificates.Pem(certificate),
            autoProvisionUsers = true,
            autoLinkByEmail = false,
            emailAttributeName = "email",
            firstNameAttributeName = "first_name",
            lastNameAttributeName = "last_name"
        };
}

/// <summary>
/// Self-signed IdP signing certificates and federation metadata for the SAML admin scenarios.
/// Every encoding of a certificate that can appear in a transcript (PEM, PEM as a JSON string,
/// base64 DER) is registered under one stable name, so a certificate reads
/// <c>{saml-certificate:name}</c> rather than random base64.
/// </summary>
internal static class SamlCertificates
{
    public static X509Certificate2 Create(Transcript transcript, string name)
        => Create(transcript, name, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    public static X509Certificate2 Create(Transcript transcript, string name, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Behavior Lock {name} IdP", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(notBefore, notAfter);
        var pem = Pem(certificate);
        transcript.Scrub(pem, "saml-certificate", name);
        transcript.Scrub(pem.Replace("\n", "\\n", StringComparison.Ordinal), "saml-certificate", name);
        transcript.Scrub(Convert.ToBase64String(certificate.RawData), "saml-certificate", name);
        return certificate;
    }

    public static string Pem(X509Certificate2 certificate)
        => certificate.ExportCertificatePem().ReplaceLineEndings("\n");

    /// <summary>IdP federation metadata; a null part is left out, for the adversarial imports.</summary>
    public static string Metadata(string? entityId, string? singleSignOnUrl, X509Certificate2? certificate)
    {
        var entity = entityId == null ? string.Empty : $" entityID=\"{entityId}\"";
        var key = certificate == null
            ? string.Empty
            : "<md:KeyDescriptor use=\"signing\"><ds:KeyInfo xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#\"><ds:X509Data><ds:X509Certificate>"
              + Convert.ToBase64String(certificate.RawData)
              + "</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>";
        var sso = singleSignOnUrl == null
            ? string.Empty
            : $"<md:SingleSignOnService Binding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect\" Location=\"{singleSignOnUrl}\"/>";
        return $"<md:EntityDescriptor xmlns:md=\"urn:oasis:names:tc:SAML:2.0:metadata\"{entity}>"
               + "<md:IDPSSODescriptor protocolSupportEnumeration=\"urn:oasis:names:tc:SAML:2.0:protocol\">"
               + key
               + sso
               + "</md:IDPSSODescriptor></md:EntityDescriptor>";
    }
}
