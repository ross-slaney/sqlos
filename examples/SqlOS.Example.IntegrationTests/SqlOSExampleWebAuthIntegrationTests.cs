using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Example.IntegrationTests.Infrastructure;

namespace SqlOS.Example.IntegrationTests;

[TestClass]
public sealed class SqlOSExampleWebAuthIntegrationTests
{
    [TestMethod]
    public async Task BackendLocalLogin_AndSessionEndpoint_Work()
    {
        var email = $"local-{Guid.NewGuid():N}@example.com";

        var orgResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/organizations", new
        {
            name = $"Local Org {Guid.NewGuid():N}"
        });
        orgResponse.EnsureSuccessStatusCode();
        var orgJson = JsonDocument.Parse(await orgResponse.Content.ReadAsStringAsync());
        var organizationId = orgJson.RootElement.GetProperty("id").GetString();

        var userResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/users", new
        {
            displayName = "Example Local User",
            email,
            password = "P@ssword123!"
        });
        userResponse.EnsureSuccessStatusCode();
        var userJson = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync());
        var userId = userJson.RootElement.GetProperty("id").GetString();

        var membershipResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/memberships", new
        {
            organizationId,
            userId,
            role = "member"
        });
        membershipResponse.EnsureSuccessStatusCode();

        var discoverResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/discover", new { email });
        discoverResponse.EnsureSuccessStatusCode();
        var discoverJson = JsonDocument.Parse(await discoverResponse.Content.ReadAsStringAsync());
        discoverJson.RootElement.GetProperty("mode").GetString().Should().Be("password");

        var loginResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email,
            password = "P@ssword123!"
        });
        loginResponse.EnsureSuccessStatusCode();
        var loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        var accessToken = loginJson.RootElement.GetProperty("accessToken").GetString();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var sessionResponse = await ExampleApiFixture.Client.SendAsync(request);
        sessionResponse.EnsureSuccessStatusCode();

        var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        sessionJson.RootElement.GetProperty("user").GetProperty("email").GetString().Should().Be(email);
        sessionJson.RootElement.GetProperty("token").GetProperty("organizationId").GetString().Should().Be(organizationId);
    }

    [TestMethod]
    public async Task SsoDraft_MetadataImport_AndDiscovery_Work()
    {
        var domain = $"entra-{Guid.NewGuid():N}".ToLowerInvariant()[..18] + ".com";
        var organizationResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/organizations", new
        {
            name = $"Entra Org {Guid.NewGuid():N}",
            primaryDomain = domain
        });
        organizationResponse.EnsureSuccessStatusCode();
        var organizationJson = JsonDocument.Parse(await organizationResponse.Content.ReadAsStringAsync());
        var organizationId = organizationJson.RootElement.GetProperty("id").GetString();

        var draftResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/sso-connections/draft", new
        {
            organizationId,
            displayName = "Entra SSO",
            primaryDomain = domain,
            autoProvisionUsers = true,
            autoLinkByEmail = false
        });
        draftResponse.EnsureSuccessStatusCode();
        var draftJson = JsonDocument.Parse(await draftResponse.Content.ReadAsStringAsync());
        var connectionId = draftJson.RootElement.GetProperty("id").GetString();

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSEntraMetadata", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var metadataXml = BuildFederationMetadata("https://sts.windows.net/test-tenant/", "https://login.microsoftonline.com/test-tenant/saml2", certificate);

        var importResponse = await ExampleApiFixture.Client.PostAsJsonAsync($"/sqlos/admin/auth/api/sso-connections/{connectionId}/metadata", new
        {
            metadataXml
        });
        importResponse.EnsureSuccessStatusCode();

        var discoverResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/discover", new
        {
            email = $"user@{domain}"
        });
        discoverResponse.EnsureSuccessStatusCode();
        var discoverJson = JsonDocument.Parse(await discoverResponse.Content.ReadAsStringAsync());
        discoverJson.RootElement.GetProperty("mode").GetString().Should().Be("sso");
        discoverJson.RootElement.GetProperty("primaryDomain").GetString().Should().Be(domain);
    }

    private static string BuildFederationMetadata(string entityId, string singleSignOnUrl, X509Certificate2 certificate)
    {
        var rawCertificate = Convert.ToBase64String(certificate.Export(X509ContentType.Cert));
        return $"""
        <EntityDescriptor xmlns="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{entityId}">
          <IDPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
            <KeyDescriptor use="signing">
              <KeyInfo xmlns="http://www.w3.org/2000/09/xmldsig#">
                <X509Data>
                  <X509Certificate>{rawCertificate}</X509Certificate>
                </X509Data>
              </KeyInfo>
            </KeyDescriptor>
            <SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect" Location="{singleSignOnUrl}" />
          </IDPSSODescriptor>
        </EntityDescriptor>
        """;
    }
}
