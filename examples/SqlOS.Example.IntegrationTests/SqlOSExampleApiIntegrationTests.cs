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
    public async Task Swagger_DoesNotInclude_SqlOSLibraryOrExampleHelperEndpoints()
    {
        var response = await ExampleApiFixture.Client.GetAsync("/swagger/v1/swagger.json");

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths")
            .EnumerateObject()
            .Select(path => path.Name)
            .ToArray();

        paths.Should().Contain("/api/chains");
        paths.Should().Contain("/api/locations/{id}");
        paths.Should().OnlyContain(path =>
            !path.StartsWith("/sqlos/auth", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/sqlos/admin/auth/api", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/sqlos/saml", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/v1/auth", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/demo", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(path, "/api/hello", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(path, "/api/me", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(path, "/api/workspaces", StringComparison.OrdinalIgnoreCase));
    }

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
    public async Task SsoDraft_ReturnsAcsUrlUnderAuthPath()
    {
        var orgResponse = await AdminPostAsync("/sqlos/admin/auth/api/organizations", new
        {
            name = $"Draft Org {Guid.NewGuid():N}"
        });
        orgResponse.EnsureSuccessStatusCode();
        var orgJson = JsonDocument.Parse(await orgResponse.Content.ReadAsStringAsync());
        var organizationId = orgJson.RootElement.GetProperty("id").GetString();

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSDraftIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        var draftResponse = await AdminPostAsync("/sqlos/admin/auth/api/sso-connections/draft", new
        {
            organizationId,
            displayName = "Draft SSO",
            identityProviderEntityId = "urn:draft:idp",
            singleSignOnUrl = "https://idp.example.local/sso",
            x509CertificatePem = certificate.ExportCertificatePem(),
            autoProvisionUsers = true,
            autoLinkByEmail = false
        });
        draftResponse.EnsureSuccessStatusCode();
        var draftJson = JsonDocument.Parse(await draftResponse.Content.ReadAsStringAsync());
        draftJson.RootElement.GetProperty("assertionConsumerServiceUrl").GetString()
            .Should().StartWith("https://localhost/sqlos/auth/saml/acs/");
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
    [DataRow("/sqlos/admin/auth/organizations/example/general")]
    [DataRow("/sqlos/admin/auth/users/example/general")]
    [DataRow("/sqlos/admin/auth/clients")]
    [DataRow("/sqlos/admin/fga/resources")]
    public async Task DashboardShell_PageRoutes_Render(string path)
    {
        var response = await ExampleApiFixture.Client.GetAsync(path);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("SqlOS");
    }

    [TestMethod]
    public async Task DashboardLogoutEndpoint_ReturnsNoContent_WhenPasswordModeDisabled()
    {
        var response = await ExampleApiFixture.Client.PostAsync("/sqlos/dashboard-auth/logout", null);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);
    }

    [TestMethod]
    public async Task DashboardPasswordMode_RequiresLoginAndProtectsAdminApis()
    {
        const string dashboardPassword = "SqlOSDashboard!123";

        await using var passwordFactory = ExampleApiFixture.CreateFactory(builder =>
        {
            builder.UseSetting("SqlOS:Dashboard:AuthMode", "Password");
            builder.UseSetting("SqlOS:Dashboard:Password", dashboardPassword);
        });

        using var client = passwordFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var dashboardResponse = await client.GetAsync("/sqlos/");
        dashboardResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
        dashboardResponse.Headers.Location!.ToString().Should().Contain("/sqlos/login");

        var deepLinkResponse = await client.GetAsync("/sqlos/admin/auth/clients");
        deepLinkResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Redirect);
        deepLinkResponse.Headers.Location!.ToString().Should().Contain("/sqlos/login");

        var authStatsUnauthorized = await client.GetAsync("/sqlos/admin/auth/api/stats");
        authStatsUnauthorized.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);

        var fgaStatsUnauthorized = await client.GetAsync("/sqlos/admin/fga/api/stats");
        fgaStatsUnauthorized.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);

        var invalidLoginResponse = await client.PostAsJsonAsync("/sqlos/dashboard-auth/login", new { password = "wrong-password" });
        invalidLoginResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);

        var loginResponse = await client.PostAsJsonAsync("/sqlos/dashboard-auth/login", new { password = dashboardPassword });
        loginResponse.EnsureSuccessStatusCode();

        var authStatsResponse = await client.GetAsync("/sqlos/admin/auth/api/stats");
        authStatsResponse.EnsureSuccessStatusCode();

        var fgaStatsResponse = await client.GetAsync("/sqlos/admin/fga/api/stats");
        fgaStatsResponse.EnsureSuccessStatusCode();

        var logoutResponse = await client.PostAsync("/sqlos/dashboard-auth/logout", null);
        logoutResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);

        var authStatsAfterLogout = await client.GetAsync("/sqlos/admin/auth/api/stats");
        authStatsAfterLogout.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Organization_CanBeUpdatedThroughAdminApi()
    {
        var createResponse = await AdminPostAsync("/sqlos/admin/auth/api/organizations", new
        {
            name = $"Update Org {Guid.NewGuid():N}"
        });
        createResponse.EnsureSuccessStatusCode();
        var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var organizationId = createJson.RootElement.GetProperty("id").GetString();

        var updateResponse = await ExampleApiFixture.Client.PutAsJsonAsync($"/sqlos/admin/auth/api/organizations/{organizationId}", new
        {
            name = "Updated Org Name",
            slug = "updated-org-name",
            primaryDomain = "updated.example.com",
            isActive = true
        });

        updateResponse.EnsureSuccessStatusCode();
        var updateJson = JsonDocument.Parse(await updateResponse.Content.ReadAsStringAsync());
        updateJson.RootElement.GetProperty("name").GetString().Should().Be("Updated Org Name");
        updateJson.RootElement.GetProperty("primaryDomain").GetString().Should().Be("updated.example.com");
    }

    [TestMethod]
    public async Task AuthAdmin_ListEndpoints_ReturnPaginationEnvelope()
    {
        var userResponse = await AdminPostAsync("/sqlos/admin/auth/api/users", new
        {
            displayName = $"Paged User {Guid.NewGuid():N}",
            email = $"paged-{Guid.NewGuid():N}@example.com"
        });
        userResponse.EnsureSuccessStatusCode();
        var userJson = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync());
        var userId = userJson.RootElement.GetProperty("id").GetString();

        var orgResponse = await AdminPostAsync("/sqlos/admin/auth/api/organizations", new
        {
            name = $"Paged Org {Guid.NewGuid():N}"
        });
        orgResponse.EnsureSuccessStatusCode();
        var orgJson = JsonDocument.Parse(await orgResponse.Content.ReadAsStringAsync());
        var organizationId = orgJson.RootElement.GetProperty("id").GetString();

        var membershipResponse = await AdminPostAsync($"/sqlos/admin/auth/api/organizations/{organizationId}/memberships", new
        {
            userId,
            role = "member"
        });
        membershipResponse.EnsureSuccessStatusCode();

        var usersResponse = await ExampleApiFixture.Client.GetAsync("/sqlos/admin/auth/api/users?page=1&pageSize=5");
        usersResponse.EnsureSuccessStatusCode();
        var usersJson = JsonDocument.Parse(await usersResponse.Content.ReadAsStringAsync());
        usersJson.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Array);
        usersJson.RootElement.GetProperty("page").GetInt32().Should().Be(1);
        usersJson.RootElement.GetProperty("totalPages").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        var orgMembershipsResponse = await ExampleApiFixture.Client.GetAsync($"/sqlos/admin/auth/api/organizations/{organizationId}/memberships?page=1&pageSize=5");
        orgMembershipsResponse.EnsureSuccessStatusCode();
        var membershipsJson = JsonDocument.Parse(await orgMembershipsResponse.Content.ReadAsStringAsync());
        membershipsJson.RootElement.GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);
        membershipsJson.RootElement.GetProperty("totalCount").GetInt32().Should().BeGreaterThan(0);

        var sessionsResponse = await ExampleApiFixture.Client.GetAsync("/sqlos/admin/auth/api/sessions?page=1&pageSize=5");
        sessionsResponse.EnsureSuccessStatusCode();
        var sessionsJson = JsonDocument.Parse(await sessionsResponse.Content.ReadAsStringAsync());
        sessionsJson.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Array);
        if (sessionsJson.RootElement.GetProperty("data").GetArrayLength() > 0)
        {
            var firstSession = sessionsJson.RootElement.GetProperty("data")[0];
            firstSession.TryGetProperty("idleExpiresAt", out _).Should().BeTrue();
            firstSession.TryGetProperty("absoluteExpiresAt", out _).Should().BeTrue();
        }
    }

    [TestMethod]
    public async Task ClientAdminEndpoints_ReturnRichClientViews_AndSupportLifecycleActions()
    {
        var clientSuffix = Guid.NewGuid().ToString("N")[..10];
        var createResponse = await AdminPostAsync("/sqlos/admin/auth/api/clients", new
        {
            clientId = $"dashboard-client-{clientSuffix}",
            name = $"Dashboard Client {clientSuffix}",
            audience = "sqlos",
            redirectUris = new[] { $"https://dashboard-{clientSuffix}.example.test/callback" },
            description = "Created by admin integration test",
            allowedScopes = new[] { "openid", "profile" },
            requirePkce = true,
            isFirstParty = true
        });
        createResponse.EnsureSuccessStatusCode();
        var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var clientId = createJson.RootElement.GetProperty("id").GetString();

        var listResponse = await ExampleApiFixture.Client.GetAsync($"/sqlos/admin/auth/api/clients?search={clientSuffix}&page=1&pageSize=10");
        listResponse.EnsureSuccessStatusCode();
        var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listJson.RootElement.GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);
        var listItem = listJson.RootElement.GetProperty("data").EnumerateArray()
            .First(item => item.GetProperty("clientId").GetString() == $"dashboard-client-{clientSuffix}");
        listItem.GetProperty("sourceLabel").GetString().Should().Be("Manual");
        listItem.GetProperty("lifecycleState").GetString().Should().Be("active");
        listItem.GetProperty("redirectUris").GetArrayLength().Should().Be(1);
        listItem.TryGetProperty("managedByStartupSeed", out _).Should().BeTrue();
        listItem.TryGetProperty("coreMetadataEditable", out _).Should().BeTrue();

        var detailResponse = await ExampleApiFixture.Client.GetAsync($"/sqlos/admin/auth/api/clients/{clientId}");
        detailResponse.EnsureSuccessStatusCode();
        var detailJson = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        detailJson.RootElement.GetProperty("clientId").GetString().Should().Be($"dashboard-client-{clientSuffix}");
        detailJson.RootElement.GetProperty("allowedScopes").GetArrayLength().Should().Be(2);
        detailJson.RootElement.TryGetProperty("recentAuditEvents", out _).Should().BeTrue();

        var disableResponse = await AdminPostAsync($"/sqlos/admin/auth/api/clients/{clientId}/disable", new
        {
            reason = "integration_test_disable"
        });
        disableResponse.EnsureSuccessStatusCode();
        var disableJson = JsonDocument.Parse(await disableResponse.Content.ReadAsStringAsync());
        disableJson.RootElement.GetProperty("isActive").GetBoolean().Should().BeFalse();
        disableJson.RootElement.GetProperty("disabledReason").GetString().Should().Be("integration_test_disable");

        var disabledListResponse = await ExampleApiFixture.Client.GetAsync($"/sqlos/admin/auth/api/clients?status=disabled&search={clientSuffix}&page=1&pageSize=10");
        disabledListResponse.EnsureSuccessStatusCode();
        var disabledListJson = JsonDocument.Parse(await disabledListResponse.Content.ReadAsStringAsync());
        disabledListJson.RootElement.GetProperty("totalCount").GetInt32().Should().BeGreaterThan(0);

        var enableResponse = await ExampleApiFixture.Client.PostAsync($"/sqlos/admin/auth/api/clients/{clientId}/enable", JsonContent.Create(new { }));
        enableResponse.EnsureSuccessStatusCode();
        var enableJson = JsonDocument.Parse(await enableResponse.Content.ReadAsStringAsync());
        enableJson.RootElement.GetProperty("isActive").GetBoolean().Should().BeTrue();

        var revokeResponse = await AdminPostAsync($"/sqlos/admin/auth/api/clients/{clientId}/revoke", new
        {
            reason = "integration_test_revoke"
        });
        revokeResponse.EnsureSuccessStatusCode();
        var revokeJson = JsonDocument.Parse(await revokeResponse.Content.ReadAsStringAsync());
        revokeJson.RootElement.TryGetProperty("revokedSessions", out _).Should().BeTrue();
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
