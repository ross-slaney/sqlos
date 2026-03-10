using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Example.IntegrationTests.Infrastructure;

namespace SqlOS.Example.IntegrationTests;

[TestClass]
public sealed class SqlOSExampleApiIntegrationTests
{
    [TestMethod]
    public async Task Signup_CanCreateAndListWorkspaces()
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var signupResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/auth/signup", new
        {
            displayName = "Alice",
            email,
            password = "P@ssword123!",
            organizationName = "Acme",
            clientId = "example-web"
        });
        signupResponse.EnsureSuccessStatusCode();
        var signupJson = JsonDocument.Parse(await signupResponse.Content.ReadAsStringAsync());
        var accessToken = signupJson.RootElement.GetProperty("tokens").GetProperty("accessToken").GetString();

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/workspaces")
        {
            Content = JsonContent.Create(new { name = "North America Workspace" })
        };
        createRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var createResponse = await ExampleApiFixture.Client.SendAsync(createRequest);
        createResponse.EnsureSuccessStatusCode();

        var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/workspaces");
        listRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var listResponse = await ExampleApiFixture.Client.SendAsync(listRequest);
        listResponse.EnsureSuccessStatusCode();
        var listContent = await listResponse.Content.ReadAsStringAsync();
        listContent.Should().Contain("North America Workspace");
    }

    [TestMethod]
    public async Task PasswordResetAndRefresh_Work()
    {
        var email = $"reset-{Guid.NewGuid():N}@example.com";
        var signupResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/auth/signup", new
        {
            displayName = "Reset User",
            email,
            password = "OldPassword123!",
            organizationName = "Reset Org",
            clientId = "example-web"
        });
        signupResponse.EnsureSuccessStatusCode();
        var signupJson = JsonDocument.Parse(await signupResponse.Content.ReadAsStringAsync());
        var tokens = signupJson.RootElement.GetProperty("tokens");
        var refreshToken = tokens.GetProperty("refreshToken").GetString();
        var organizationId = tokens.GetProperty("organizationId").GetString();

        var refreshResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/auth/token/refresh", new
        {
            refreshToken,
            organizationId
        });
        refreshResponse.EnsureSuccessStatusCode();

        var forgotResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/auth/password/forgot", new { email });
        forgotResponse.EnsureSuccessStatusCode();
        var forgotJson = JsonDocument.Parse(await forgotResponse.Content.ReadAsStringAsync());
        var resetToken = forgotJson.RootElement.GetProperty("token").GetString();

        var resetResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/auth/password/reset", new
        {
            token = resetToken,
            newPassword = "NewPassword123!"
        });
        resetResponse.EnsureSuccessStatusCode();

        var loginResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/auth/password/login", new
        {
            email,
            password = "NewPassword123!",
            clientId = "example-web",
            organizationId
        });
        loginResponse.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task SignedSamlFlow_ReturnsExchangeableCode()
    {
        var orgResponse = await AdminPostAsync("/sqlos/admin/auth/api/organizations", new
        {
            name = $"Saml Org {Guid.NewGuid():N}"
        });
        orgResponse.EnsureSuccessStatusCode();
        var orgJson = JsonDocument.Parse(await orgResponse.Content.ReadAsStringAsync());
        var organizationId = orgJson.RootElement.GetProperty("id").GetString();

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSExampleIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        var connectionResponse = await AdminPostAsync("/sqlos/admin/auth/api/sso-connections", new
        {
            organizationId,
            displayName = "Example SSO",
            identityProviderEntityId = "urn:example:idp",
            singleSignOnUrl = "https://idp.example.local/sso",
            x509CertificatePem = certificate.ExportCertificatePem(),
            autoProvisionUsers = true,
            autoLinkByEmail = false
        });
        connectionResponse.EnsureSuccessStatusCode();
        var connectionJson = JsonDocument.Parse(await connectionResponse.Content.ReadAsStringAsync());
        var connectionId = connectionJson.RootElement.GetProperty("id").GetString();

        var authUrlResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/auth/sso/authorization-url", new
        {
            connectionId,
            clientId = "example-web",
            redirectUri = "https://client.example.local/callback"
        });
        authUrlResponse.EnsureSuccessStatusCode();
        var authUrlJson = JsonDocument.Parse(await authUrlResponse.Content.ReadAsStringAsync());
        var authUrl = authUrlJson.RootElement.GetProperty("authorizationUrl").GetString()!;
        var relayState = QueryHelpers.ParseQuery(new Uri($"https://localhost{authUrl}").Query)["requestToken"].ToString();

        var samlResponse = BuildSignedSamlResponse(certificate, "urn:example:idp", "saml-user@example.com", "Saml", "User");
        var acsResponse = await ExampleApiFixture.Client.PostAsync(
            $"/sqlos/auth/saml/acs/{connectionId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["SAMLResponse"] = samlResponse,
                ["RelayState"] = relayState
            }));

        acsResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
        var location = acsResponse.Headers.Location!.ToString();
        var code = QueryHelpers.ParseQuery(new Uri(location).Query)["code"].ToString();
        code.Should().NotBeNullOrWhiteSpace();

        var exchangeResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/auth/token/exchange", new
        {
            code,
            clientId = "example-web"
        });
        exchangeResponse.EnsureSuccessStatusCode();
        var exchangeJson = JsonDocument.Parse(await exchangeResponse.Content.ReadAsStringAsync());
        exchangeJson.RootElement.GetProperty("organizationId").GetString().Should().Be(organizationId);
    }

    [TestMethod]
    public async Task DashboardStats_AreAvailableInDevelopment()
    {
        var response = await ExampleApiFixture.Client.GetAsync("/sqlos/admin/auth/api/stats");

        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("organizations");
    }

    [DataTestMethod]
    [DataRow("/sqlos/")]
    [DataRow("/sqlos/admin/auth/organizations")]
    [DataRow("/sqlos/admin/auth/clients")]
    [DataRow("/sqlos/admin/fga/resources")]
    public async Task DashboardShell_PageRoutes_Render(string path)
    {
        var response = await ExampleApiFixture.Client.GetAsync(path);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("SqlOS Dashboard");
    }

    private static async Task<HttpResponseMessage> AdminPostAsync(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        return await ExampleApiFixture.Client.SendAsync(request);
    }

    private static string BuildSignedSamlResponse(
        X509Certificate2 certificate,
        string issuer,
        string email,
        string firstName,
        string lastName)
    {
        var responseId = $"_{Guid.NewGuid():N}";
        var issueInstant = DateTime.UtcNow.ToString("o");
        var xml = $"""
        <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="{responseId}" Version="2.0" IssueInstant="{issueInstant}">
          <saml:Issuer>{issuer}</saml:Issuer>
          <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success" /></samlp:Status>
          <saml:Assertion ID="_{Guid.NewGuid():N}" Version="2.0" IssueInstant="{issueInstant}">
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
}
