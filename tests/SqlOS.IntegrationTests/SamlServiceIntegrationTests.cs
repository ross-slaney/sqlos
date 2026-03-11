using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.IO.Compression;
using System.Xml;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class SamlServiceIntegrationTests
{
    [TestMethod]
    public async Task SignedSamlResponse_ProducesExchangeableAuthCode()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options);
        var auth = new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings);

        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"SAML {Guid.NewGuid():N}", null));
        var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"saml-client-{Guid.NewGuid():N}"[..18],
            "SAML Client",
            "sqlos-tests",
            new List<string> { "https://client.example.local/callback" }));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSTestIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Test SSO",
            "urn:test:idp",
            "https://idp.example.test/sso",
            cert.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var authUrl = await saml.CreateAuthorizationUrlAsync(new SqlOSAuthorizationUrlRequest(connection.Id, client.ClientId, "https://client.example.local/callback"));
        var requestToken = QueryHelpers.ParseQuery(new Uri($"https://localhost{authUrl}").Query)["requestToken"].ToString();
        requestToken.Should().NotBeNull();

        var samlResponse = BuildSignedSamlResponse(cert, "urn:test:idp", "user@example.com", "Saml", "User");
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, requestToken!, default);
        redirectUrl.Should().StartWith("https://client.example.local/callback?code=");

        var code = QueryHelpers.ParseQuery(new Uri(redirectUrl).Query)["code"].ToString();
        code.Should().NotBeNull();

        var tokens = await auth.ExchangeCodeAsync(new SqlOSExchangeCodeRequest(code!, client.ClientId), new DefaultHttpContext());
        tokens.OrganizationId.Should().Be(org.Id);
        tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task PkceSamlAuthorizationFlow_CanExchangeCode()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);
        var auth = new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings);
        var discovery = new SqlOSHomeRealmDiscoveryService(AspireFixture.SharedContext);
        var ssoAuth = new SqlOSSsoAuthorizationService(AspireFixture.SharedContext, admin, crypto, discovery, saml, auth);

        var domain = $"contoso-{Guid.NewGuid():N}".ToLowerInvariant()[..20] + ".com";
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"PKCE {Guid.NewGuid():N}", null, domain));
        var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"pkce-client-{Guid.NewGuid():N}"[..20],
            "PKCE Client",
            "sqlos-tests",
            new List<string> { "https://client.example.local/auth/callback" }));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSPkceIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "PKCE SSO",
            "urn:pkce:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var codeVerifier = crypto.GenerateOpaqueToken();
        var state = crypto.GenerateOpaqueToken();
        var start = await ssoAuth.StartAuthorizationAsync(new SqlOSSsoAuthorizationStartRequest(
            $"user@{domain}",
            client.ClientId,
            "https://client.example.local/auth/callback",
            state,
            crypto.CreatePkceCodeChallenge(codeVerifier),
            "S256"));

        start.AuthorizationUrl.Should().Contain("SAMLRequest=");
        var relayState = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["RelayState"].ToString();
        relayState.Should().NotBeNullOrWhiteSpace();

        var samlResponse = BuildSignedSamlResponse(certificate, "urn:pkce:idp", $"user@{domain}", "Pkce", "User");
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, relayState!, default);
        redirectUrl.Should().Contain("state=");

        var query = QueryHelpers.ParseQuery(new Uri(redirectUrl).Query);
        var code = query["code"].ToString();
        query["state"].ToString().Should().Be(state);

        var tokens = await ssoAuth.ExchangeCodeAsync(
            new SqlOSPkceExchangeRequest(code!, client.ClientId, "https://client.example.local/auth/callback", codeVerifier),
            new DefaultHttpContext());

        tokens.OrganizationId.Should().Be(org.Id);
        tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task AuthorizationUrl_UsesRedirectBindingDeflateEncoding()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);

        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Redirect {Guid.NewGuid():N}", null));
        var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"redir-client-{Guid.NewGuid():N}"[..20],
            "Redirect Client",
            "sqlos-tests",
            new List<string> { "https://client.example.local/callback" }));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSRedirectIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Redirect SSO",
            "urn:redirect:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var startUrl = await saml.CreateAuthorizationUrlAsync(new SqlOSAuthorizationUrlRequest(
            connection.Id,
            client.ClientId,
            "https://client.example.local/callback"));

        var loginUrl = await saml.BuildIdentityProviderRedirectAsync(
            connection.Id,
            QueryHelpers.ParseQuery(new Uri($"https://localhost{startUrl}").Query)["requestToken"].ToString());

        var samlRequest = QueryHelpers.ParseQuery(new Uri(loginUrl).Query)["SAMLRequest"].ToString();
        samlRequest.Should().NotBeNullOrWhiteSpace();

        var xml = InflateSamlRequest(samlRequest!);
        xml.Should().Contain("<samlp:AuthnRequest");
        xml.Should().Contain("AssertionConsumerServiceURL=");
        xml.Should().Contain(connection.SingleSignOnUrl);
    }

    private static string BuildSignedSamlResponse(
        X509Certificate2 certificate,
        string issuer,
        string email,
        string firstName,
        string lastName)
    {
        var responseId = $"_{Guid.NewGuid():N}";
        var assertionId = $"_{Guid.NewGuid():N}";
        var issueInstant = DateTime.UtcNow.ToString("o");
        var xml = $"""
        <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="{responseId}" Version="2.0" IssueInstant="{issueInstant}">
          <saml:Issuer>{issuer}</saml:Issuer>
          <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success" /></samlp:Status>
          <saml:Assertion ID="{assertionId}" Version="2.0" IssueInstant="{issueInstant}">
            <saml:Issuer>{issuer}</saml:Issuer>
            <saml:Subject>
              <saml:NameID>{email}</saml:NameID>
            </saml:Subject>
            <saml:AttributeStatement>
              <saml:Attribute Name="email"><saml:AttributeValue>{email}</saml:AttributeValue></saml:Attribute>
              <saml:Attribute Name="first_name"><saml:AttributeValue>{firstName}</saml:AttributeValue></saml:Attribute>
              <saml:Attribute Name="last_name"><saml:AttributeValue>{lastName}</saml:AttributeValue></saml:Attribute>
            </saml:AttributeStatement>
          </saml:Assertion>
        </samlp:Response>
        """;

        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        xmlDoc.LoadXml(xml);
        var responseElement = xmlDoc.DocumentElement!;
        var privateKey = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Test certificate does not contain an RSA private key.");
        var signedXml = new SignedXml(responseElement)
        {
            SigningKey = privateKey
        };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var reference = new Reference { Uri = $"#{responseId}" };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);
        signedXml.KeyInfo = new KeyInfo();
        signedXml.KeyInfo.AddClause(new KeyInfoX509Data(certificate));
        signedXml.ComputeSignature();
        responseElement.InsertAfter(xmlDoc.ImportNode(signedXml.GetXml(), true), responseElement.FirstChild);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(xmlDoc.OuterXml));
    }

    private static string InflateSamlRequest(string samlRequest)
    {
        var bytes = Convert.FromBase64String(samlRequest);
        using var compressed = new MemoryStream(bytes);
        using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(inflater, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
