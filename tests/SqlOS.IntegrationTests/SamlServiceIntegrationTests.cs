using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.IO.Compression;
using System.Xml;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
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
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(AspireFixture.SharedContext, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings, emailOtp);

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
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options, emailSender);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);
        var emailOtp = new SqlOSEmailOtpService(AspireFixture.SharedContext, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings, emailOtp);
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
    public async Task SignedSamlResponse_WithExistingEmail_ReusesUserWhenAutoProvisioning()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);

        var email = $"existing-saml-{Guid.NewGuid():N}@example.com";
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Existing SAML User", email, "P@ssword123!"));
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Existing SAML {Guid.NewGuid():N}", null));
        var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"existing-saml-{Guid.NewGuid():N}"[..20],
            "Existing SAML Client",
            "sqlos-tests",
            new List<string> { "https://client.example.local/callback" }));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSExistingSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Existing User SSO",
            "urn:existing:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var authUrl = await saml.CreateAuthorizationUrlAsync(new SqlOSAuthorizationUrlRequest(connection.Id, client.ClientId, "https://client.example.local/callback"));
        var requestToken = QueryHelpers.ParseQuery(new Uri($"https://localhost{authUrl}").Query)["requestToken"].ToString();
        requestToken.Should().NotBeNull();

        var samlResponse = BuildSignedSamlResponse(certificate, "urn:existing:idp", email, "Existing", "User");
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, requestToken!, default);
        redirectUrl.Should().StartWith("https://client.example.local/callback?code=");

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        var matchingEmails = await AspireFixture.SharedContext.Set<SqlOSUserEmail>()
            .Where(x => x.NormalizedEmail == normalizedEmail)
            .ToListAsync();
        matchingEmails.Should().ContainSingle();
        matchingEmails.Single().UserId.Should().Be(existingUser.Id);

        var externalIdentity = await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .SingleAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email);
        externalIdentity.UserId.Should().Be(existingUser.Id);

        (await AspireFixture.SharedContext.Set<SqlOSMembership>()
            .AnyAsync(x => x.OrganizationId == org.Id && x.UserId == existingUser.Id && x.IsActive))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithExistingOrgMemberAndRequireSso_LinksExternalIdentityWithoutCreatingUser()
    {
        var (_, admin, saml) = CreateSamlServices();
        var email = $"existing-member-{Guid.NewGuid():N}@example.com";
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Existing Member", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(existingUser.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Existing Member {Guid.NewGuid():N}", null));
        AspireFixture.SharedContext.Set<SqlOSMembership>().Add(new SqlOSMembership
        {
            OrganizationId = org.Id,
            UserId = existingUser.Id,
            Role = "member",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await AspireFixture.SharedContext.SaveChangesAsync();
        var client = await CreateSamlClientAsync(admin, "existing-member");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSExistingMemberSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Existing Member SSO",
            "urn:existing-member:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            false,
            true,
            "email",
            "first_name",
            "last_name"));

        var requestToken = await CreateRequestTokenAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, "urn:existing-member:idp", email, "Existing", "Member");
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, requestToken, default);

        redirectUrl.Should().StartWith("https://client.example.local/callback?code=");
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        (await AspireFixture.SharedContext.Set<SqlOSUserEmail>().CountAsync(x => x.NormalizedEmail == normalizedEmail))
            .Should().Be(1);
        (await AspireFixture.SharedContext.Set<SqlOSMembership>().CountAsync(x => x.OrganizationId == org.Id && x.UserId == existingUser.Id))
            .Should().Be(1);
        var externalIdentity = await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .SingleAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email);
        externalIdentity.UserId.Should().Be(existingUser.Id);
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithExistingNonMemberAndJitOff_IsDenied()
    {
        var (_, admin, saml) = CreateSamlServices();
        var email = $"existing-nonmember-{Guid.NewGuid():N}@example.com";
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Existing Nonmember", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(existingUser.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Existing Nonmember {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "existing-nonmember");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSExistingNonmemberSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Existing Nonmember SSO",
            "urn:existing-nonmember:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            false,
            true,
            "email",
            "first_name",
            "last_name"));

        var requestToken = await CreateRequestTokenAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, "urn:existing-nonmember:idp", email, "Existing", "Nonmember");
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, requestToken, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        (await AspireFixture.SharedContext.Set<SqlOSMembership>().AnyAsync(x => x.OrganizationId == org.Id && x.UserId == existingUser.Id))
            .Should().BeFalse();
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>().AnyAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email))
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithMissingUserAndJitOff_IsDenied()
    {
        var (_, admin, saml) = CreateSamlServices();
        var email = $"missing-jit-off-{Guid.NewGuid():N}@example.com";
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Missing JIT Off {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "missing-jit-off");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSMissingJitOffSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Missing JIT Off SSO",
            "urn:missing-jit-off:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            false,
            true,
            "email",
            "first_name",
            "last_name"));

        var requestToken = await CreateRequestTokenAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, "urn:missing-jit-off:idp", email, "Missing", "User");
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, requestToken, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        (await AspireFixture.SharedContext.Set<SqlOSUserEmail>().AnyAsync(x => x.NormalizedEmail == normalizedEmail))
            .Should().BeFalse();
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

    private static (SqlOSCryptoService Crypto, SqlOSAdminService Admin, SqlOSSamlService Saml) CreateSamlServices()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);
        return (crypto, admin, saml);
    }

    private static async Task<SqlOSClientApplication> CreateSamlClientAsync(SqlOSAdminService admin, string prefix)
        => await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"{prefix}-{Guid.NewGuid():N}"[..20],
            $"{prefix} client",
            "sqlos-tests",
            new List<string> { "https://client.example.local/callback" }));

    private static async Task<string> CreateRequestTokenAsync(SqlOSSamlService saml, string connectionId, string clientId)
    {
        var authUrl = await saml.CreateAuthorizationUrlAsync(new SqlOSAuthorizationUrlRequest(
            connectionId,
            clientId,
            "https://client.example.local/callback"));
        return QueryHelpers.ParseQuery(new Uri($"https://localhost{authUrl}").Query)["requestToken"].ToString();
    }

    private static async Task MarkEmailVerifiedAsync(string userId)
    {
        var email = await AspireFixture.SharedContext.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == userId);
        email.IsVerified = true;
        email.VerifiedAt = DateTime.UtcNow;
        await AspireFixture.SharedContext.SaveChangesAsync();
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
