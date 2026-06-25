using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security;
using System.IO.Compression;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSSamlService
{
    private const string SamlProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string SamlAssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string SamlBearerConfirmationMethod = "urn:oasis:names:tc:SAML:2.0:cm:bearer";
    private const string SamlStatePropertyName = "__sqlosSaml";
    private static readonly TimeSpan SamlClockSkew = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> AllowedSignatureMethods = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigRSASHA256Url,
        SignedXml.XmlDsigRSASHA384Url,
        SignedXml.XmlDsigRSASHA512Url,
        "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256",
        "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha384",
        "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha512"
    };
    private static readonly HashSet<string> AllowedDigestMethods = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigSHA256Url,
        SignedXml.XmlDsigSHA384Url,
        SignedXml.XmlDsigSHA512Url
    };
    private static readonly HashSet<string> AllowedReferenceTransforms = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigEnvelopedSignatureTransformUrl,
        SignedXml.XmlDsigC14NTransformUrl,
        SignedXml.XmlDsigExcC14NTransformUrl
    };

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSAuthorizationServerService? _authorizationServerService;

    public SqlOSSamlService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        SqlOSAuthorizationServerService? authorizationServerService = null)
    {
        _context = context;
        _options = options.Value;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _authorizationServerService = authorizationServerService;
    }

    public async Task<string> CreateAuthorizationUrlAsync(SqlOSAuthorizationUrlRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSSsoConnection>()
            .FirstOrDefaultAsync(x => x.Id == request.ConnectionId && x.IsEnabled, cancellationToken)
            ?? throw new InvalidOperationException("SAML connection not found or disabled.");
        var client = await _adminService.RequireClientAsync(request.ClientId, request.RedirectUri, cancellationToken);

        var requestToken = await _cryptoService.CreateTemporaryTokenAsync(
            "sso_request",
            null,
            client.Id,
            connection.OrganizationId,
            new SsoRequestPayload(client.ClientId, request.RedirectUri, connection.Id, null, null),
            TimeSpan.FromMinutes(10),
            cancellationToken);

        return $"{_options.BasePath.TrimEnd('/')}/saml/login/{connection.Id}?requestToken={Uri.EscapeDataString(requestToken)}";
    }

    public async Task<string> BuildIdentityProviderRedirectAsync(string connectionId, string requestToken, CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSSsoConnection>().FirstOrDefaultAsync(x => x.Id == connectionId && x.IsEnabled, cancellationToken)
            ?? throw new InvalidOperationException("SAML connection not found or disabled.");
        var requestState = await _cryptoService.FindTemporaryTokenAsync("sso_request", requestToken, cancellationToken)
            ?? throw new InvalidOperationException("SSO request token is invalid or expired.");
        var requestPayload = _cryptoService.DeserializePayload<SsoRequestPayload>(requestState)
            ?? throw new InvalidOperationException("SSO request payload is invalid.");
        var samlRequest = BuildAuthnRequest(connection);

        requestState.PayloadJson = JsonSerializer.Serialize(requestPayload with
        {
            SamlRequestId = samlRequest.Id,
            AssertionConsumerServiceUrl = samlRequest.AssertionConsumerServiceUrl
        });
        await _context.SaveChangesAsync(cancellationToken);

        return BuildIdentityProviderRedirectUrl(connection, requestToken, samlRequest.EncodedRequest);
    }

    public async Task<string> BuildIdentityProviderRedirectForAuthorizationRequestAsync(string authorizationRequestId, CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _context.Set<SqlOSAuthorizationRequest>()
            .Include(x => x.Connection)
            .FirstOrDefaultAsync(x => x.Id == authorizationRequestId, cancellationToken)
            ?? throw new InvalidOperationException("Authorization request was not found.");

        if (authorizationRequest.CancelledAt != null || authorizationRequest.CompletedAt != null || authorizationRequest.ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Authorization request is no longer active.");
        }

        var connection = authorizationRequest.Connection;
        if (connection == null || !connection.IsEnabled)
        {
            throw new InvalidOperationException("SAML connection not found or disabled.");
        }

        var samlRequest = BuildAuthnRequest(connection);
        authorizationRequest.UiContextJson = StoreSamlRequestState(
            authorizationRequest.UiContextJson,
            new SamlRequestState(samlRequest.Id, samlRequest.AssertionConsumerServiceUrl));
        await _context.SaveChangesAsync(cancellationToken);

        return BuildIdentityProviderRedirectUrl(connection, authorizationRequest.Id, samlRequest.EncodedRequest);
    }

    public async Task<string> HandleAcsAsync(string connectionId, string samlResponse, string relayState, HttpContext? httpContext = null, CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSSsoConnection>().FirstOrDefaultAsync(x => x.Id == connectionId && x.IsEnabled, cancellationToken)
            ?? throw new InvalidOperationException("SAML connection not found or disabled.");
        var authorizationRequest = await _context.Set<SqlOSAuthorizationRequest>()
            .FirstOrDefaultAsync(x => x.Id == relayState && x.ConnectionId == connectionId, cancellationToken);

        if (authorizationRequest != null)
        {
            if (authorizationRequest.CancelledAt != null || authorizationRequest.CompletedAt != null || authorizationRequest.ExpiresAt <= DateTime.UtcNow)
            {
                throw new InvalidOperationException("Authorization request is no longer active.");
            }

            var samlState = ReadSamlRequestState(authorizationRequest.UiContextJson)
                ?? throw new InvalidOperationException("SAML request state is missing.");
            var principal = ParseAndValidateAssertion(
                samlResponse,
                connection,
                new SamlValidationContext(samlState.RequestId, samlState.AssertionConsumerServiceUrl, _adminService.GetServiceProviderEntityId()));
            return await HandleAuthorizationRequestAcsAsync(connection, authorizationRequest, principal, httpContext, cancellationToken);
        }

        var requestToken = await _cryptoService.ConsumeTemporaryTokenAsync("sso_request", relayState, cancellationToken)
            ?? throw new InvalidOperationException("SSO request token is invalid or expired.");
        var requestPayload = _cryptoService.DeserializePayload<SsoRequestPayload>(requestToken)
            ?? throw new InvalidOperationException("SSO request payload is invalid.");
        if (string.IsNullOrWhiteSpace(requestPayload.SamlRequestId) || string.IsNullOrWhiteSpace(requestPayload.AssertionConsumerServiceUrl))
        {
            throw new InvalidOperationException("SAML request state is missing.");
        }

        var requestPrincipal = ParseAndValidateAssertion(
            samlResponse,
            connection,
            new SamlValidationContext(requestPayload.SamlRequestId, requestPayload.AssertionConsumerServiceUrl, _adminService.GetServiceProviderEntityId()));

        return await HandleRequestTokenAcsAsync(connection, requestToken, requestPayload, requestPrincipal, cancellationToken);
    }

    private async Task<string> HandleAuthorizationRequestAcsAsync(
        SqlOSSsoConnection connection,
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSSamlPrincipal principal,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        if (authorizationRequest.CancelledAt != null || authorizationRequest.CompletedAt != null || authorizationRequest.ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Authorization request is no longer active.");
        }

        var email = principal.Attributes.TryGetValue(connection.EmailAttributeName, out var emailValue) ? emailValue : authorizationRequest.LoginHintEmail;
        var organizationId = authorizationRequest.OrganizationId ?? connection.OrganizationId;
        var user = await ResolveUserAsync(connection, principal, email, organizationId, cancellationToken)
            ?? throw new InvalidOperationException("No user could be resolved from the SAML assertion.");

        if (_authorizationServerService != null)
        {
            authorizationRequest.ResolvedConnectionId = connection.Id;
            return await _authorizationServerService.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                user,
                organizationId,
                "saml",
                httpContext ?? new DefaultHttpContext(),
                cancellationToken);
        }

        var client = authorizationRequest.ClientApplication
            ?? await _context.Set<SqlOSClientApplication>()
                .FirstAsync(x => x.Id == authorizationRequest.ClientApplicationId, cancellationToken);
        await _adminService.EnsureApplicationAccessAsync(
            client,
            user.Id,
            organizationId,
            "application.access.authorization_denied",
            httpContext?.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        var rawCode = _cryptoService.GenerateOpaqueToken();
        _context.Set<SqlOSAuthorizationCode>().Add(new SqlOSAuthorizationCode
        {
            Id = _cryptoService.GenerateId("acd"),
            AuthorizationRequestId = authorizationRequest.Id,
            UserId = user.Id,
            ClientApplicationId = authorizationRequest.ClientApplicationId,
            OrganizationId = organizationId,
            RedirectUri = authorizationRequest.RedirectUri,
            State = authorizationRequest.State,
            Scope = authorizationRequest.Scope,
            Resource = authorizationRequest.Resource,
            CodeHash = _cryptoService.HashToken(rawCode),
            CodeChallenge = authorizationRequest.CodeChallenge,
            CodeChallengeMethod = authorizationRequest.CodeChallengeMethod,
            AuthenticationMethod = "saml",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        authorizationRequest.CompletedAt = DateTime.UtcNow;
        authorizationRequest.ResolvedAuthMethod = "saml";
        authorizationRequest.ResolvedOrganizationId = organizationId;
        authorizationRequest.ResolvedConnectionId = connection.Id;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException("Authorization request is no longer active.", ex);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new InvalidOperationException("Authorization request is no longer active.", ex);
        }

        await _adminService.RecordAuditAsync("user.login.saml", "user", user.Id, userId: user.Id, organizationId: organizationId, cancellationToken: cancellationToken);
        var separator = authorizationRequest.RedirectUri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{authorizationRequest.RedirectUri}{separator}code={Uri.EscapeDataString(rawCode)}&state={Uri.EscapeDataString(authorizationRequest.State)}";
    }

    private async Task<string> HandleRequestTokenAcsAsync(
        SqlOSSsoConnection connection,
        SqlOSTemporaryToken requestToken,
        SsoRequestPayload requestPayload,
        SqlOSSamlPrincipal principal,
        CancellationToken cancellationToken)
    {
        var email = principal.Attributes.TryGetValue(connection.EmailAttributeName, out var emailValue) ? emailValue : null;
        var user = await ResolveUserAsync(connection, principal, email, connection.OrganizationId, cancellationToken)
            ?? throw new InvalidOperationException("No user could be resolved from the SAML assertion.");

        var code = await _cryptoService.CreateTemporaryTokenAsync(
            "auth_code",
            user.Id,
            requestToken.ClientApplicationId,
            connection.OrganizationId,
            new AuthCodePayload(requestPayload.ClientId, requestPayload.RedirectUri, "saml"),
            TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken);

        await _adminService.RecordAuditAsync("user.login.saml", "user", user.Id, userId: user.Id, organizationId: connection.OrganizationId, cancellationToken: cancellationToken);
        var separator = requestPayload.RedirectUri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{requestPayload.RedirectUri}{separator}code={Uri.EscapeDataString(code)}";
    }

    private SamlAuthnRequest BuildAuthnRequest(SqlOSSsoConnection connection)
    {
        var acsUrl = _adminService.GetAssertionConsumerServiceUrl(connection.Id);
        var requestId = $"_{Guid.NewGuid():N}";
        var issueInstant = DateTime.UtcNow.ToString("o");

        var xml = $"""
        <samlp:AuthnRequest xmlns:samlp="{SamlProtocolNs}" xmlns:saml="{SamlAssertionNs}" ID="{requestId}" Version="2.0" IssueInstant="{issueInstant}" Destination="{connection.SingleSignOnUrl}" AssertionConsumerServiceURL="{acsUrl}">
          <saml:Issuer>{SecurityElement.Escape(_options.Issuer)}</saml:Issuer>
        </samlp:AuthnRequest>
        """;

        using var output = new MemoryStream();
        using (var deflater = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(xml);
            deflater.Write(bytes, 0, bytes.Length);
        }

        return new SamlAuthnRequest(requestId, acsUrl, Convert.ToBase64String(output.ToArray()));
    }

    private static string BuildIdentityProviderRedirectUrl(SqlOSSsoConnection connection, string relayState, string encodedAuthnRequest)
    {
        var query = $"SAMLRequest={Uri.EscapeDataString(encodedAuthnRequest)}&RelayState={Uri.EscapeDataString(relayState)}";
        var separator = connection.SingleSignOnUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{connection.SingleSignOnUrl}{separator}{query}";
    }

    private SqlOSSamlPrincipal ParseAndValidateAssertion(
        string base64Response,
        SqlOSSsoConnection connection,
        SamlValidationContext validationContext)
    {
        var xml = Encoding.UTF8.GetString(Convert.FromBase64String(base64Response));
        var xmlDoc = LoadSamlDocument(xml);

        var ns = new XmlNamespaceManager(xmlDoc.NameTable);
        ns.AddNamespace("samlp", SamlProtocolNs);
        ns.AddNamespace("saml", SamlAssertionNs);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);

        var responseElement = xmlDoc.DocumentElement;
        if (responseElement == null
            || responseElement.LocalName != "Response"
            || responseElement.NamespaceURI != SamlProtocolNs)
        {
            throw new InvalidOperationException("SAML response is invalid.");
        }

        var assertionNodes = responseElement.SelectNodes("saml:Assertion", ns);
        if (assertionNodes == null || assertionNodes.Count != 1 || assertionNodes[0] is not XmlElement assertionElement)
        {
            throw new InvalidOperationException("SAML response must contain exactly one assertion.");
        }

        var signedElement = ValidateSignature(xmlDoc, connection.X509CertificatePem, ns);
        if (!ReferenceEquals(signedElement, responseElement) && !ReferenceEquals(signedElement, assertionElement))
        {
            throw new InvalidOperationException("SAML signature does not cover the consumed assertion.");
        }

        if (ReferenceEquals(signedElement, responseElement) && !ReferenceEquals(assertionElement.ParentNode, responseElement))
        {
            throw new InvalidOperationException("SAML signature does not cover the consumed assertion.");
        }

        var responseIssuer = responseElement.SelectSingleNode("saml:Issuer", ns)?.InnerText;
        if (!string.IsNullOrWhiteSpace(responseIssuer)
            && !string.Equals(responseIssuer, connection.IdentityProviderEntityId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SAML response issuer mismatch.");
        }

        var issuer = assertionElement.SelectSingleNode("saml:Issuer", ns)?.InnerText
            ?? throw new InvalidOperationException("SAML assertion issuer missing.");
        if (!string.Equals(issuer, connection.IdentityProviderEntityId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SAML response issuer mismatch.");
        }

        ValidateResponseCorrelation(responseElement, validationContext);
        ValidateAssertionConditions(assertionElement, validationContext, ns);

        var nameId = assertionElement.SelectSingleNode("saml:Subject/saml:NameID", ns)?.InnerText
            ?? throw new InvalidOperationException("SAML assertion NameID missing.");

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var attributeNodes = assertionElement.SelectNodes("saml:AttributeStatement/saml:Attribute", ns);
        if (attributeNodes != null)
        {
            foreach (XmlNode attribute in attributeNodes)
            {
                var name = attribute.Attributes?["Name"]?.Value;
                var value = attribute.SelectSingleNode("saml:AttributeValue", ns)?.InnerText;
                if (!string.IsNullOrWhiteSpace(name) && value != null)
                {
                    attributes[name] = value;
                }
            }
        }

        return new SqlOSSamlPrincipal(issuer, nameId, attributes);
    }

    private static XmlDocument LoadSamlDocument(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        var xmlDocument = new XmlDocument
        {
            PreserveWhitespace = true,
            XmlResolver = null
        };

        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, settings);
        xmlDocument.Load(reader);
        return xmlDocument;
    }

    private static XmlElement ValidateSignature(XmlDocument xmlDocument, string certificatePem, XmlNamespaceManager ns)
    {
        var signatureNodes = xmlDocument.SelectNodes("/samlp:Response/ds:Signature | /samlp:Response/saml:Assertion/ds:Signature", ns);
        if (signatureNodes == null || signatureNodes.Count != 1 || signatureNodes[0] is not XmlElement signatureElement)
        {
            throw new InvalidOperationException("SAML response signature missing.");
        }

        var signedElement = signatureElement.ParentNode as XmlElement
            ?? throw new InvalidOperationException("Signed SAML element missing.");
        var signedXml = new SignedXmlWithIdResolution(signedElement);
        signedXml.LoadXml(signatureElement);
        ValidateSignedInfo(signedXml);

        var certificate = X509Certificate2.CreateFromPem(certificatePem);
        if (!signedXml.CheckSignature(certificate, true))
        {
            throw new InvalidOperationException("SAML response signature is invalid.");
        }

        var reference = (Reference)signedXml.SignedInfo!.References[0]!;
        var referenceUri = reference.Uri
            ?? throw new InvalidOperationException("SAML signature reference must identify a signed element.");
        var referencedElement = ResolveSignedReference(xmlDocument, referenceUri);
        if (!ReferenceEquals(referencedElement, signedElement))
        {
            throw new InvalidOperationException("SAML signature reference does not match the signed element.");
        }

        return referencedElement;
    }

    private static void ValidateSignedInfo(SignedXml signedXml)
    {
        if (signedXml.SignedInfo == null
            || string.IsNullOrWhiteSpace(signedXml.SignedInfo.SignatureMethod)
            || !AllowedSignatureMethods.Contains(signedXml.SignedInfo.SignatureMethod))
        {
            throw new InvalidOperationException("SAML signature algorithm is not allowed.");
        }

        if (signedXml.SignedInfo.References.Count != 1 || signedXml.SignedInfo.References[0] is not Reference reference)
        {
            throw new InvalidOperationException("SAML signature must contain exactly one reference.");
        }

        if (string.IsNullOrWhiteSpace(reference.Uri) || !reference.Uri.StartsWith("#", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SAML signature reference must identify a signed element.");
        }

        if (string.IsNullOrWhiteSpace(reference.DigestMethod) || !AllowedDigestMethods.Contains(reference.DigestMethod))
        {
            throw new InvalidOperationException("SAML signature digest algorithm is not allowed.");
        }

        var transforms = new List<Transform>();
        for (var i = 0; i < reference.TransformChain.Count; i++)
        {
            transforms.Add((Transform)reference.TransformChain[i]);
        }
        if (transforms.Count != 2
            || !transforms.Any(t => string.Equals(t.Algorithm, SignedXml.XmlDsigEnvelopedSignatureTransformUrl, StringComparison.Ordinal))
            || !transforms.Any(t => string.Equals(t.Algorithm, SignedXml.XmlDsigC14NTransformUrl, StringComparison.Ordinal)
                || string.Equals(t.Algorithm, SignedXml.XmlDsigExcC14NTransformUrl, StringComparison.Ordinal))
            || transforms.Any(t => string.IsNullOrWhiteSpace(t.Algorithm) || !AllowedReferenceTransforms.Contains(t.Algorithm)))
        {
            throw new InvalidOperationException("SAML signature transform is not allowed.");
        }
    }

    private static XmlElement ResolveSignedReference(XmlDocument document, string uri)
    {
        var id = Uri.UnescapeDataString(uri[1..]);
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("SAML signature reference is invalid.");
        }

        XmlElement? match = null;
        foreach (var element in EnumerateElements(document.DocumentElement))
        {
            if (ElementHasId(element, id))
            {
                if (match != null)
                {
                    throw new InvalidOperationException("SAML signature reference ID is ambiguous.");
                }

                match = element;
            }
        }

        return match ?? throw new InvalidOperationException("SAML signature reference target is missing.");
    }

    private static IEnumerable<XmlElement> EnumerateElements(XmlElement? root)
    {
        if (root == null)
        {
            yield break;
        }

        yield return root;
        foreach (XmlNode child in root.ChildNodes)
        {
            if (child is XmlElement childElement)
            {
                foreach (var descendant in EnumerateElements(childElement))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static bool ElementHasId(XmlElement element, string id)
        => string.Equals(element.GetAttribute("ID"), id, StringComparison.Ordinal)
           || string.Equals(element.GetAttribute("Id"), id, StringComparison.Ordinal)
           || string.Equals(element.GetAttribute("id"), id, StringComparison.Ordinal)
           || string.Equals(element.GetAttribute("xml:id"), id, StringComparison.Ordinal);

    private static void ValidateResponseCorrelation(XmlElement responseElement, SamlValidationContext validationContext)
    {
        var responseInResponseTo = responseElement.GetAttribute("InResponseTo");
        if (!string.IsNullOrWhiteSpace(responseInResponseTo)
            && !string.Equals(responseInResponseTo, validationContext.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SAML response InResponseTo mismatch.");
        }

        var destination = responseElement.GetAttribute("Destination");
        if (!string.IsNullOrWhiteSpace(destination)
            && !string.Equals(destination, validationContext.AssertionConsumerServiceUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SAML response destination mismatch.");
        }
    }

    private static void ValidateAssertionConditions(XmlElement assertionElement, SamlValidationContext validationContext, XmlNamespaceManager ns)
    {
        var now = DateTime.UtcNow;
        var conditions = assertionElement.SelectSingleNode("saml:Conditions", ns) as XmlElement
            ?? throw new InvalidOperationException("SAML assertion conditions missing.");

        var notBefore = ParseSamlDateTimeOrNull(conditions.GetAttribute("NotBefore"));
        if (notBefore.HasValue && now + SamlClockSkew < notBefore.Value)
        {
            throw new InvalidOperationException("SAML assertion is not yet valid.");
        }

        var notOnOrAfter = ParseSamlDateTimeOrNull(conditions.GetAttribute("NotOnOrAfter"))
            ?? throw new InvalidOperationException("SAML assertion expiration missing.");
        if (now - SamlClockSkew >= notOnOrAfter)
        {
            throw new InvalidOperationException("SAML assertion has expired.");
        }

        var audiences = conditions.SelectNodes("saml:AudienceRestriction/saml:Audience", ns);
        if (audiences == null
            || audiences.Count == 0
            || !audiences.Cast<XmlNode>().Any(a => string.Equals(a.InnerText, validationContext.Audience, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("SAML assertion audience mismatch.");
        }

        var subjectConfirmationData = assertionElement.SelectNodes(
            "saml:Subject/saml:SubjectConfirmation/saml:SubjectConfirmationData",
            ns);
        if (subjectConfirmationData == null
            || !subjectConfirmationData.Cast<XmlNode>().OfType<XmlElement>().Any(data => IsValidSubjectConfirmation(data, validationContext, now)))
        {
            throw new InvalidOperationException("SAML subject confirmation mismatch.");
        }
    }

    private static bool IsValidSubjectConfirmation(XmlElement subjectConfirmationData, SamlValidationContext validationContext, DateTime now)
    {
        if (subjectConfirmationData.ParentNode is XmlElement confirmation)
        {
            var method = confirmation.GetAttribute("Method");
            if (!string.Equals(method, SamlBearerConfirmationMethod, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!string.Equals(subjectConfirmationData.GetAttribute("Recipient"), validationContext.AssertionConsumerServiceUrl, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(subjectConfirmationData.GetAttribute("InResponseTo"), validationContext.RequestId, StringComparison.Ordinal))
        {
            return false;
        }

        var notOnOrAfter = ParseSamlDateTimeOrNull(subjectConfirmationData.GetAttribute("NotOnOrAfter"));
        return !notOnOrAfter.HasValue || now - SamlClockSkew < notOnOrAfter.Value;
    }

    private static DateTime? ParseSamlDateTimeOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return XmlConvert.ToDateTime(value, XmlDateTimeSerializationMode.Utc);
    }

    private static string StoreSamlRequestState(string? uiContextJson, SamlRequestState state)
    {
        var uiContext = SqlOSHeadlessAuthService.ParseUiContext(uiContextJson) ?? new JsonObject();
        uiContext[SamlStatePropertyName] = JsonSerializer.SerializeToNode(state);
        return uiContext.ToJsonString();
    }

    private static SamlRequestState? ReadSamlRequestState(string? uiContextJson)
    {
        var uiContext = SqlOSHeadlessAuthService.ParseUiContext(uiContextJson);
        if (uiContext == null || !uiContext.TryGetPropertyValue(SamlStatePropertyName, out var value) || value == null)
        {
            return null;
        }

        return value.Deserialize<SamlRequestState>();
    }

    private sealed class SignedXmlWithIdResolution : SignedXml
    {
        public SignedXmlWithIdResolution(XmlElement element) : base(element)
        {
        }

        public override XmlElement? GetIdElement(XmlDocument? document, string idValue)
        {
            if (document?.DocumentElement == null)
            {
                return null;
            }

            return EnumerateElements(document.DocumentElement).FirstOrDefault(element => ElementHasId(element, idValue));
        }
    }

    private async Task<SqlOSUser?> ResolveUserAsync(
        SqlOSSsoConnection connection,
        SqlOSSamlPrincipal principal,
        string? email,
        string organizationId,
        CancellationToken cancellationToken)
    {
        var externalIdentity = await _context.Set<SqlOSExternalIdentity>()
            .FirstOrDefaultAsync(x => x.SsoConnectionId == connection.Id && x.Subject == principal.Subject, cancellationToken);
        SqlOSUser? user = externalIdentity == null
            ? null
            : await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == externalIdentity.UserId, cancellationToken);
        string? normalizedEmail = null;

        if (user != null)
        {
            var hasMembership = await UserHasActiveMembershipAsync(user.Id, organizationId, cancellationToken);
            if (!hasMembership)
            {
                if (!connection.AutoProvisionUsers)
                {
                    return null;
                }

                await EnsureMembershipAsync(organizationId, user.Id, cancellationToken);
            }
        }
        else if (!string.IsNullOrWhiteSpace(email))
        {
            normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
            var existingEmail = await _context.Set<SqlOSUserEmail>().FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
            if (existingEmail != null)
            {
                user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == existingEmail.UserId, cancellationToken);
                var hasMembership = await UserHasActiveMembershipAsync(user.Id, organizationId, cancellationToken);
                if (hasMembership)
                {
                    if (!connection.AutoLinkByEmail || !existingEmail.IsVerified)
                    {
                        return null;
                    }
                }
                else
                {
                    if (!connection.AutoProvisionUsers)
                    {
                        return null;
                    }

                    if (!existingEmail.IsVerified)
                    {
                        existingEmail.IsVerified = true;
                        existingEmail.VerifiedAt = DateTime.UtcNow;
                    }

                    await EnsureMembershipAsync(organizationId, user.Id, cancellationToken);
                }
            }
            else if (connection.AutoProvisionUsers)
            {
                var displayName = $"{principal.Attributes.GetValueOrDefault(connection.FirstNameAttributeName, string.Empty)} {principal.Attributes.GetValueOrDefault(connection.LastNameAttributeName, string.Empty)}".Trim();
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = email;
                }

                user = new SqlOSUser
                {
                    Id = _cryptoService.GenerateId("usr"),
                    DisplayName = displayName,
                    DefaultEmail = email,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.Set<SqlOSUser>().Add(user);
                _context.Set<SqlOSUserEmail>().Add(new SqlOSUserEmail
                {
                    Id = _cryptoService.GenerateId("eml"),
                    UserId = user.Id,
                    Email = email,
                    NormalizedEmail = normalizedEmail ?? SqlOSAdminService.NormalizeEmail(email),
                    IsPrimary = true,
                    IsVerified = true,
                    VerifiedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                });
                await EnsureMembershipAsync(organizationId, user.Id, cancellationToken);
            }
        }

        if (user == null)
        {
            return null;
        }

        if (externalIdentity == null)
        {
            _context.Set<SqlOSExternalIdentity>().Add(new SqlOSExternalIdentity
            {
                Id = _cryptoService.GenerateId("ext"),
                UserId = user.Id,
                SsoConnectionId = connection.Id,
                Issuer = principal.Issuer,
                Subject = principal.Subject,
                Email = email,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        return user;
    }

    private async Task<bool> UserHasActiveMembershipAsync(
        string userId,
        string organizationId,
        CancellationToken cancellationToken)
        => await _context.Set<SqlOSMembership>()
            .AnyAsync(x => x.UserId == userId && x.OrganizationId == organizationId && x.IsActive, cancellationToken);

    private async Task EnsureMembershipAsync(
        string organizationId,
        string userId,
        CancellationToken cancellationToken)
    {
        var existing = await _context.Set<SqlOSMembership>()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.UserId == userId, cancellationToken);
        if (existing != null)
        {
            existing.IsActive = true;
            existing.Role = "member";
            return;
        }

        _context.Set<SqlOSMembership>().Add(new SqlOSMembership
        {
            OrganizationId = organizationId,
            UserId = userId,
            Role = "member",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        });
    }

    private sealed record SsoRequestPayload(
        string ClientId,
        string RedirectUri,
        string ConnectionId,
        string? SamlRequestId,
        string? AssertionConsumerServiceUrl);

    private sealed record SamlAuthnRequest(string Id, string AssertionConsumerServiceUrl, string EncodedRequest);
    private sealed record SamlRequestState(string RequestId, string AssertionConsumerServiceUrl);
    private sealed record SamlValidationContext(string RequestId, string AssertionConsumerServiceUrl, string Audience);
    private sealed record AuthCodePayload(string ClientId, string RedirectUri, string AuthenticationMethod);
    private sealed record SqlOSSamlPrincipal(string Issuer, string Subject, Dictionary<string, string> Attributes);

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => exception.InnerException is SqlException { Number: 2601 or 2627 };
}
