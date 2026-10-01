using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using SqlOS.BehaviorLock.Infrastructure.Fakes;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// Builds SAML responses whose shape the shared <see cref="TestSamlIdentityProvider"/> cannot vary:
/// a different issuer, missing or future conditions, another confirmation method, weak signature
/// algorithms, extra transforms, a missing ID or NameID, or a different root element. The response
/// is edited as XML and then signed with the IdP's registered certificate, so every refusal comes
/// from the rule under test rather than from a broken signature.
/// </summary>
internal sealed class SamlResponseForge
{
    private const string Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";

    private readonly TestSamlIdentityProvider _idp;

    public SamlResponseForge(TestSamlIdentityProvider idp)
    {
        _idp = idp;
    }

    /// <summary>How the forged response is signed.</summary>
    public sealed record Signing
    {
        public bool SignAssertion { get; init; }

        public string SignatureMethod { get; init; } = SignedXml.XmlDsigRSASHA256Url;

        public string DigestMethod { get; init; } = SignedXml.XmlDsigSHA256Url;

        /// <summary>Adds a third transform (inclusive C14N); SqlOS allows exactly the enveloped-signature and one canonicalization transform.</summary>
        public bool ExtraTransform { get; init; }

        /// <summary>Leaves the response unsigned.</summary>
        public bool Unsigned { get; init; }
    }

    /// <summary>
    /// A response to <paramref name="request"/> for <paramref name="email"/>; <paramref name="rewrite"/>
    /// edits the XML text and <paramref name="edit"/> the parsed document before it is signed.
    /// </summary>
    public string Build(
        SamlAuthnRequest request,
        string email,
        Action<XmlDocument, XmlNamespaceManager>? edit = null,
        Signing? signing = null,
        Func<string, string>? rewrite = null)
    {
        signing ??= new Signing();
        var now = DateTime.UtcNow;
        var issueInstant = now.ToString("o");
        var notBefore = now.AddMinutes(-1).ToString("o");
        var notOnOrAfter = now.AddMinutes(5).ToString("o");
        var xml = $"""
            <samlp:Response xmlns:samlp="{Protocol}" xmlns:saml="{Assertion}" ID="_{Guid.NewGuid():N}" Version="2.0" IssueInstant="{issueInstant}" Destination="{SecurityElement.Escape(request.AssertionConsumerServiceUrl)}" InResponseTo="{SecurityElement.Escape(request.Id)}">
              <saml:Issuer>{SecurityElement.Escape(_idp.EntityId)}</saml:Issuer>
              <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success" /></samlp:Status>
              <saml:Assertion ID="_{Guid.NewGuid():N}" Version="2.0" IssueInstant="{issueInstant}">
                <saml:Issuer>{SecurityElement.Escape(_idp.EntityId)}</saml:Issuer>
                <saml:Subject>
                  <saml:NameID>{SecurityElement.Escape(email)}</saml:NameID>
                  <saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer">
                    <saml:SubjectConfirmationData InResponseTo="{SecurityElement.Escape(request.Id)}" Recipient="{SecurityElement.Escape(request.AssertionConsumerServiceUrl)}" NotOnOrAfter="{notOnOrAfter}" />
                  </saml:SubjectConfirmation>
                </saml:Subject>
                <saml:Conditions NotBefore="{notBefore}" NotOnOrAfter="{notOnOrAfter}">
                  <saml:AudienceRestriction><saml:Audience>{SecurityElement.Escape(request.ServiceProviderEntityId)}</saml:Audience></saml:AudienceRestriction>
                </saml:Conditions>
                <saml:AttributeStatement>
                  <saml:Attribute Name="email"><saml:AttributeValue>{SecurityElement.Escape(email)}</saml:AttributeValue></saml:Attribute>
                  <saml:Attribute Name="first_name"><saml:AttributeValue>Ivan</saml:AttributeValue></saml:Attribute>
                  <saml:Attribute Name="last_name"><saml:AttributeValue>Petrov</saml:AttributeValue></saml:Attribute>
                </saml:AttributeStatement>
              </saml:Assertion>
            </samlp:Response>
            """;
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(rewrite == null ? xml : rewrite(xml));
        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("samlp", Protocol);
        ns.AddNamespace("saml", Assertion);
        edit?.Invoke(document, ns);

        if (!signing.Unsigned)
        {
            var target = signing.SignAssertion
                ? (XmlElement)document.SelectSingleNode("//saml:Assertion", ns)!
                : document.DocumentElement!;
            Sign(document, target, _idp.Certificate, signing);
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(document.OuterXml));
    }

    private static void Sign(XmlDocument document, XmlElement target, X509Certificate2 certificate, Signing signing)
    {
        var signedXml = new SignedXml(target) { SigningKey = certificate.GetRSAPrivateKey() };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = signing.SignatureMethod;
        var id = target.GetAttribute("ID");
        var reference = new Reference { Uri = string.IsNullOrEmpty(id) ? string.Empty : $"#{id}", DigestMethod = signing.DigestMethod };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        if (signing.ExtraTransform)
        {
            reference.AddTransform(new XmlDsigC14NTransform());
        }

        signedXml.AddReference(reference);
        signedXml.KeyInfo = new KeyInfo();
        signedXml.KeyInfo.AddClause(new KeyInfoX509Data(certificate));
        signedXml.ComputeSignature();
        target.InsertAfter(document.ImportNode(signedXml.GetXml(), true), target.FirstChild);
    }

    /// <summary>Sets an attribute on the first element <paramref name="xpath"/> selects.</summary>
    public static Action<XmlDocument, XmlNamespaceManager> SetAttribute(string xpath, string name, string value)
        => (document, ns) => ((XmlElement)document.SelectSingleNode(xpath, ns)!).SetAttribute(name, value);

    /// <summary>Removes an attribute from the first element <paramref name="xpath"/> selects.</summary>
    public static Action<XmlDocument, XmlNamespaceManager> RemoveAttribute(string xpath, string name)
        => (document, ns) => ((XmlElement)document.SelectSingleNode(xpath, ns)!).RemoveAttribute(name);

    /// <summary>Removes the first node <paramref name="xpath"/> selects.</summary>
    public static Action<XmlDocument, XmlNamespaceManager> Remove(string xpath)
        => (document, ns) =>
        {
            var node = document.SelectSingleNode(xpath, ns)!;
            node.ParentNode!.RemoveChild(node);
        };

    /// <summary>Replaces the text of the first element <paramref name="xpath"/> selects.</summary>
    public static Action<XmlDocument, XmlNamespaceManager> SetText(string xpath, string text)
        => (document, ns) => document.SelectSingleNode(xpath, ns)!.InnerText = text;
}
