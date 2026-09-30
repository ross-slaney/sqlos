using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.WebUtilities;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Infrastructure.Fakes;

/// <summary>
/// A SAML 2.0 identity provider for scenarios: it owns a signing certificate, reads the
/// AuthnRequest SqlOS redirects to it, and builds signed <c>SAMLResponse</c> values. Adversarial
/// variants (unsigned, signed by another key, tampered after signing, wrapped extra assertion,
/// wrong audience or recipient, expired, wrong InResponseTo) exercise SqlOS's validation.
/// The certificate is generated per instance; <see cref="RegisterWith"/> gives its encodings
/// stable names in the transcript.
/// </summary>
public sealed class TestSamlIdentityProvider : IDisposable
{
    private const string Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";
    private readonly RSA _key;
    private readonly RSA _otherKey = RSA.Create(2048);

    public TestSamlIdentityProvider(string entityId = "urn:behavior-lock:idp", string singleSignOnUrl = "https://idp.example.test/sso")
    {
        EntityId = entityId;
        SingleSignOnUrl = singleSignOnUrl;
        _key = RSA.Create(2048);
        Certificate = CreateCertificate(_key, "CN=Behavior Lock Test IdP");
        OtherCertificate = CreateCertificate(_otherKey, "CN=Behavior Lock Impostor IdP");
    }

    public string EntityId { get; }

    public string SingleSignOnUrl { get; }

    public X509Certificate2 Certificate { get; }

    /// <summary>A different key, for assertions that must fail signature validation.</summary>
    public X509Certificate2 OtherCertificate { get; }

    public string CertificatePem => Certificate.ExportCertificatePem();

    /// <summary>Registers the certificate's PEM, DER, and thumbprints so transcripts name them.</summary>
    public void RegisterWith(Transcript transcript)
    {
        var der = Convert.ToBase64String(Certificate.RawData);
        transcript.Scrub(CertificatePem, "saml-certificate");
        transcript.Scrub(CertificatePem.ReplaceLineEndings("\n"), "saml-certificate");
        transcript.Scrub(der, "saml-certificate");
        transcript.Scrub(Certificate.Thumbprint, "saml-thumbprint");
        transcript.Scrub(Certificate.Thumbprint.ToLowerInvariant(), "saml-thumbprint");
        transcript.Scrub(Convert.ToHexString(SHA256.HashData(Certificate.RawData)), "saml-thumbprint");
        transcript.Scrub(Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant(), "saml-thumbprint");
    }

    /// <summary>Reads the HTTP-Redirect binding request SqlOS sent to <see cref="SingleSignOnUrl"/>.</summary>
    public static SamlAuthnRequest ReadRedirect(string redirectUrl)
    {
        var query = QueryHelpers.ParseQuery(new Uri(redirectUrl).Query);
        var xml = Inflate(query["SAMLRequest"].ToString());
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml(xml);
        var root = document.DocumentElement!;
        var issuer = root.GetElementsByTagName("Issuer", Assertion).OfType<XmlElement>().FirstOrDefault()?.InnerText;
        return new SamlAuthnRequest(
            root.GetAttribute("ID"),
            root.GetAttribute("AssertionConsumerServiceURL"),
            issuer ?? string.Empty,
            query["RelayState"].ToString(),
            xml);
    }

    /// <summary>Builds a base64 <c>SAMLResponse</c> for <paramref name="request"/>.</summary>
    public string BuildResponse(SamlAuthnRequest request, SamlAssertion assertion)
    {
        var now = DateTime.UtcNow;
        var responseId = $"_{Guid.NewGuid():N}";
        var assertionId = $"_{Guid.NewGuid():N}";
        var issueInstant = now.ToString("o");
        var audience = assertion.Audience ?? request.ServiceProviderEntityId;
        var recipient = assertion.Recipient ?? request.AssertionConsumerServiceUrl;
        var inResponseTo = assertion.InResponseTo ?? request.Id;
        var notBefore = (assertion.Expired ? now.AddMinutes(-30) : now.AddMinutes(-1)).ToString("o");
        var notOnOrAfter = (assertion.Expired ? now.AddMinutes(-20) : now.AddMinutes(5)).ToString("o");
        var authnStatement = assertion.AuthnContextClassRef == null
            ? string.Empty
            : $"""
              <saml:AuthnStatement AuthnInstant="{issueInstant}">
                <saml:AuthnContext><saml:AuthnContextClassRef>{SecurityElement.Escape(assertion.AuthnContextClassRef)}</saml:AuthnContextClassRef></saml:AuthnContext>
              </saml:AuthnStatement>
              """;
        var xml = $"""
            <samlp:Response xmlns:samlp="{Protocol}" xmlns:saml="{Assertion}" ID="{responseId}" Version="2.0" IssueInstant="{issueInstant}" Destination="{SecurityElement.Escape(recipient)}" InResponseTo="{SecurityElement.Escape(inResponseTo)}">
              <saml:Issuer>{SecurityElement.Escape(EntityId)}</saml:Issuer>
              <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success" /></samlp:Status>
              <saml:Assertion ID="{assertionId}" Version="2.0" IssueInstant="{issueInstant}">
                <saml:Issuer>{SecurityElement.Escape(EntityId)}</saml:Issuer>
                <saml:Subject>
                  <saml:NameID>{SecurityElement.Escape(assertion.NameId)}</saml:NameID>
                  <saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer">
                    <saml:SubjectConfirmationData InResponseTo="{SecurityElement.Escape(inResponseTo)}" Recipient="{SecurityElement.Escape(recipient)}" NotOnOrAfter="{notOnOrAfter}" />
                  </saml:SubjectConfirmation>
                </saml:Subject>
                <saml:Conditions NotBefore="{notBefore}" NotOnOrAfter="{notOnOrAfter}">
                  <saml:AudienceRestriction><saml:Audience>{SecurityElement.Escape(audience)}</saml:Audience></saml:AudienceRestriction>
                </saml:Conditions>
                {authnStatement}
                <saml:AttributeStatement>
                  <saml:Attribute Name="email"><saml:AttributeValue>{SecurityElement.Escape(assertion.Email ?? assertion.NameId)}</saml:AttributeValue></saml:Attribute>
                  <saml:Attribute Name="first_name"><saml:AttributeValue>{SecurityElement.Escape(assertion.FirstName)}</saml:AttributeValue></saml:Attribute>
                  <saml:Attribute Name="last_name"><saml:AttributeValue>{SecurityElement.Escape(assertion.LastName)}</saml:AttributeValue></saml:Attribute>
                </saml:AttributeStatement>
              </saml:Assertion>
            </samlp:Response>
            """;

        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        var response = document.DocumentElement!;
        var assertionElement = (XmlElement)response.GetElementsByTagName("Assertion", Assertion)[0]!;
        if (assertion.WrapUnsignedAssertion is { } wrappedNameId)
        {
            // Signature wrapping: an unsigned assertion for another subject ahead of the signed one.
            var extra = document.CreateElement("saml", "Assertion", Assertion);
            extra.SetAttribute("ID", $"_{Guid.NewGuid():N}");
            extra.SetAttribute("Version", "2.0");
            extra.SetAttribute("IssueInstant", issueInstant);
            extra.InnerXml = $"""<saml:Issuer xmlns:saml="{Assertion}">{SecurityElement.Escape(EntityId)}</saml:Issuer><saml:Subject xmlns:saml="{Assertion}"><saml:NameID>{SecurityElement.Escape(wrappedNameId)}</saml:NameID></saml:Subject>""";
            response.InsertBefore(extra, assertionElement);
        }

        if (assertion.Signing != SamlSigning.None)
        {
            var signedElement = assertion.SignAssertion ? assertionElement : response;
            var certificate = assertion.Signing == SamlSigning.OtherKey ? OtherCertificate : Certificate;
            var signedXml = new SignedXml(signedElement) { SigningKey = certificate.GetRSAPrivateKey() };
            signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
            signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
            var reference = new Reference { Uri = $"#{signedElement.GetAttribute("ID")}", DigestMethod = SignedXml.XmlDsigSHA256Url };
            reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
            reference.AddTransform(new XmlDsigExcC14NTransform());
            signedXml.AddReference(reference);
            signedXml.KeyInfo = new KeyInfo();
            signedXml.KeyInfo.AddClause(new KeyInfoX509Data(certificate));
            signedXml.ComputeSignature();
            signedElement.InsertAfter(document.ImportNode(signedXml.GetXml(), true), signedElement.FirstChild);
        }

        if (assertion.TamperedEmail is { } tampered)
        {
            // Changes the signed content after signing, so the digest no longer matches.
            var emailValue = assertionElement.GetElementsByTagName("AttributeValue", Assertion).OfType<XmlElement>().First();
            emailValue.InnerText = tampered;
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(document.OuterXml));
    }

    public void Dispose()
    {
        Certificate.Dispose();
        OtherCertificate.Dispose();
        _key.Dispose();
        _otherKey.Dispose();
    }

    private static X509Certificate2 CreateCertificate(RSA key, string subject)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static string Inflate(string samlRequest)
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(samlRequest));
        using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(inflater, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>The AuthnRequest SqlOS sent, as the IdP reads it.</summary>
public sealed record SamlAuthnRequest(
    string Id,
    string AssertionConsumerServiceUrl,
    string ServiceProviderEntityId,
    string RelayState,
    string Xml);

public enum SamlSigning
{
    /// <summary>Signed with the IdP's registered certificate.</summary>
    Valid,

    /// <summary>Signed with a key SqlOS does not trust.</summary>
    OtherKey,

    /// <summary>No signature.</summary>
    None
}

/// <summary>What the assertion says, and how it deviates from a valid one.</summary>
public sealed record SamlAssertion(string NameId, string FirstName = "Test", string LastName = "User")
{
    public string? Email { get; init; }

    public SamlSigning Signing { get; init; } = SamlSigning.Valid;

    /// <summary>Signs the assertion instead of the response.</summary>
    public bool SignAssertion { get; init; }

    public string? Audience { get; init; }

    public string? Recipient { get; init; }

    public string? InResponseTo { get; init; }

    public bool Expired { get; init; }

    public string? AuthnContextClassRef { get; init; }

    /// <summary>Rewrites the email attribute after signing.</summary>
    public string? TamperedEmail { get; init; }

    /// <summary>Adds an unsigned assertion for this NameID ahead of the signed one.</summary>
    public string? WrapUnsignedAssertion { get; init; }
}
