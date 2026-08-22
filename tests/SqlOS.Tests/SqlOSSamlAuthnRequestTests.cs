using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSSamlAuthnRequestTests
{
    private const string PkceChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task AuthnRequest_MaxAgeZero_CarriesForceAuthn()
    {
        var xml = await BuildAuthnRequestXmlAsync(maxAgeSeconds: 0, prompt: null);
        xml.Should().Contain("ForceAuthn=\"true\"");
    }

    [TestMethod]
    public async Task AuthnRequest_PromptLogin_CarriesForceAuthn()
    {
        var xml = await BuildAuthnRequestXmlAsync(maxAgeSeconds: null, prompt: "login");
        xml.Should().Contain("ForceAuthn=\"true\"");
    }

    [TestMethod]
    public async Task AuthnRequest_PromptSelectAccount_CarriesForceAuthn()
    {
        var xml = await BuildAuthnRequestXmlAsync(maxAgeSeconds: null, prompt: "select_account");
        xml.Should().Contain("ForceAuthn=\"true\"");
    }

    [TestMethod]
    public async Task AuthnRequest_WithoutFreshAuthenticationDemand_OmitsForceAuthn()
    {
        var xml = await BuildAuthnRequestXmlAsync(maxAgeSeconds: null, prompt: null);
        xml.Should().NotContain("ForceAuthn");

        // A nonzero max_age constrains freshness at issuance but does not force
        // upstream reauthentication by itself.
        var positiveMaxAge = await BuildAuthnRequestXmlAsync(maxAgeSeconds: 300, prompt: "consent");
        positiveMaxAge.Should().NotContain("ForceAuthn");
    }

    /// <summary>
    /// Runs the real federated start path for a bound authorization request and
    /// returns the inflated samlp:AuthnRequest XML from the redirect URL.
    /// </summary>
    private static async Task<string> BuildAuthnRequestXmlAsync(long? maxAgeSeconds, string? prompt)
    {
        await using var context = new TestSqlOSInMemoryDbContext(
            new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://auth.example.test",
            Issuer = "https://auth.example.test/sqlos/auth"
        };
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options, new EphemeralDataProtectionProvider());
        var admin = new SqlOSAdminService(context, options, crypto);
        var saml = new SqlOSSamlService(context, options, admin, crypto);

        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("ForceAuthn Org", null));
        var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            "saml-client",
            "SAML Client",
            "sqlos-tests",
            ["https://client.example.test/callback"]));

        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=SqlOSForceAuthnIdP",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            organization.Id,
            "ForceAuthn SSO",
            "urn:forceauthn:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var authorizationRequest = new SqlOSAuthorizationRequest
        {
            Id = crypto.GenerateId("req"),
            ClientApplicationId = client.Id,
            OrganizationId = organization.Id,
            ConnectionId = connection.Id,
            PresentationMode = "hosted",
            RedirectUri = "https://client.example.test/callback",
            State = "state-forceauthn",
            Scope = "openid profile email",
            Prompt = prompt,
            MaxAgeSeconds = maxAgeSeconds,
            CodeChallenge = PkceChallenge,
            CodeChallengeMethod = "S256",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };
        context.Set<SqlOSAuthorizationRequest>().Add(authorizationRequest);
        await context.SaveChangesAsync();

        var redirectUrl = await saml.BuildIdentityProviderRedirectForAuthorizationRequestAsync(authorizationRequest.Id);
        var samlRequest = QueryHelpers.ParseQuery(new Uri(redirectUrl).Query)["SAMLRequest"].ToString();
        samlRequest.Should().NotBeNullOrWhiteSpace();
        return InflateSamlRequest(samlRequest);
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
