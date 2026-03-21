using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Example.IntegrationTests.Infrastructure;

namespace SqlOS.Example.IntegrationTests;

[TestClass]
public sealed class SqlOSExampleOidcAuthIntegrationTests
{
    [TestMethod]
    public async Task GoogleOidcFlow_Works_ThroughBackendEndpoints()
    {
        var email = $"google-oidc-{Guid.NewGuid():N}@example.com";
        var organizationId = await CreateOrganizationAsync($"Google Org {Guid.NewGuid():N}");
        var userId = await CreateUserAsync(email, "Google OIDC User", null);
        await CreateMembershipAsync(organizationId, userId, "member");
        var connectionId = await UpsertOidcConnectionAsync("Google");

        var providersResponse = await ExampleApiFixture.Client.GetAsync("/api/v1/auth/oidc/providers");
        providersResponse.EnsureSuccessStatusCode();
        var providersJson = JsonDocument.Parse(await providersResponse.Content.ReadAsStringAsync());
        providersJson.RootElement.EnumerateArray().Any(x => x.GetProperty("providerType").GetString() == "Google").Should().BeTrue();
        providersJson.RootElement.EnumerateArray()
            .Single(x => x.GetProperty("providerType").GetString() == "Google")
            .GetProperty("logoDataUrl").GetString()
            .Should().StartWith("data:image/svg+xml");

        var startResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/oidc/start", new
        {
            email,
            connectionId
        });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        startJson.RootElement.GetProperty("mode").GetString().Should().Be("oidc");
        var authorizationUrl = startJson.RootElement.GetProperty("authorizationUrl").GetString()!;
        var state = ExtractQueryValue(new Uri(authorizationUrl), "state")!;
        var nonce = ExtractQueryValue(new Uri(authorizationUrl), "nonce")!;

        var callbackResponse = await ExampleApiFixture.Client.GetAsync($"/api/v1/auth/oidc/callback/{connectionId}?code=success:{Uri.EscapeDataString(email)}:{Uri.EscapeDataString(nonce)}&state={Uri.EscapeDataString(state)}");
        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var handoff = ExtractQueryValue(callbackResponse.Headers.Location!, "handoff");
        handoff.Should().NotBeNullOrWhiteSpace();

        var completeResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/oidc/complete", new { handoff });
        completeResponse.EnsureSuccessStatusCode();
        var completeJson = JsonDocument.Parse(await completeResponse.Content.ReadAsStringAsync());
        completeJson.RootElement.GetProperty("user").GetProperty("email").GetString().Should().Be(email);
        completeJson.RootElement.GetProperty("organizationId").GetString().Should().Be(organizationId);
        completeJson.RootElement.GetProperty("claims").EnumerateArray().Any(x => x.GetProperty("value").GetString() == "google").Should().BeTrue();
    }

    [TestMethod]
    public async Task MicrosoftOidcFlow_Works_ForUserWithoutOrganization()
    {
        var email = $"microsoft-oidc-{Guid.NewGuid():N}@example.com";
        var connectionId = await UpsertOidcConnectionAsync("Microsoft");

        var startResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/oidc/start", new
        {
            email,
            connectionId
        });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        startJson.RootElement.GetProperty("mode").GetString().Should().Be("oidc");
        var authorizationUrl = startJson.RootElement.GetProperty("authorizationUrl").GetString()!;
        var state = ExtractQueryValue(new Uri(authorizationUrl), "state")!;
        var nonce = ExtractQueryValue(new Uri(authorizationUrl), "nonce")!;

        var callbackResponse = await ExampleApiFixture.Client.GetAsync($"/api/v1/auth/oidc/callback/{connectionId}?code=success:{Uri.EscapeDataString(email)}:{Uri.EscapeDataString(nonce)}&state={Uri.EscapeDataString(state)}");
        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var handoff = ExtractQueryValue(callbackResponse.Headers.Location!, "handoff");

        var completeResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/oidc/complete", new { handoff });
        completeResponse.EnsureSuccessStatusCode();
        var completeJson = JsonDocument.Parse(await completeResponse.Content.ReadAsStringAsync());
        completeJson.RootElement.GetProperty("organizationId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestMethod]
    public async Task AppleOidcFlow_Works_WithBackendCallback()
    {
        var email = $"apple-oidc-{Guid.NewGuid():N}@example.com";
        var connectionId = await UpsertAppleConnectionAsync();

        var startResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/oidc/start", new
        {
            email,
            connectionId
        });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        var authorizationUrl = startJson.RootElement.GetProperty("authorizationUrl").GetString()!;
        var state = ExtractQueryValue(new Uri(authorizationUrl), "state")!;
        var nonce = ExtractQueryValue(new Uri(authorizationUrl), "nonce")!;

        var callbackResponse = await ExampleApiFixture.Client.PostAsync(
            $"/api/v1/auth/oidc/callback/{connectionId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = $"success:{email}:{nonce}",
                ["state"] = state,
                ["user"] = $"{{\"name\":{{\"firstName\":\"Apple\",\"lastName\":\"User\"}},\"email\":\"{email}\"}}"
            }));
        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var handoff = ExtractQueryValue(callbackResponse.Headers.Location!, "handoff");
        handoff.Should().NotBeNullOrWhiteSpace();

        var completeResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/oidc/complete", new { handoff });
        completeResponse.EnsureSuccessStatusCode();
        var completeJson = JsonDocument.Parse(await completeResponse.Content.ReadAsStringAsync());
        completeJson.RootElement.GetProperty("claims").EnumerateArray().Any(x => x.GetProperty("value").GetString() == "apple").Should().BeTrue();
    }

    [TestMethod]
    public async Task CustomOidcFlow_Works_ThroughBackendEndpoints()
    {
        var email = $"custom-oidc-{Guid.NewGuid():N}@example.com";
        var connectionId = await UpsertCustomConnectionAsync();

        var startResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/oidc/start", new
        {
            email,
            connectionId
        });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        var authorizationUrl = startJson.RootElement.GetProperty("authorizationUrl").GetString()!;
        var state = ExtractQueryValue(new Uri(authorizationUrl), "state")!;
        var nonce = ExtractQueryValue(new Uri(authorizationUrl), "nonce")!;

        var callbackResponse = await ExampleApiFixture.Client.GetAsync($"/api/v1/auth/oidc/callback/{connectionId}?code=success:{Uri.EscapeDataString(email)}:{Uri.EscapeDataString(nonce)}&state={Uri.EscapeDataString(state)}");
        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var handoff = ExtractQueryValue(callbackResponse.Headers.Location!, "handoff");

        var completeResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/oidc/complete", new { handoff });
        completeResponse.EnsureSuccessStatusCode();
        var completeJson = JsonDocument.Parse(await completeResponse.Content.ReadAsStringAsync());
        completeJson.RootElement.GetProperty("user").GetProperty("email").GetString().Should().Be(email);
    }

    [TestMethod]
    public async Task OidcStart_PrefersSso_WhenDomainMatchesConfiguredOrganization()
    {
        var domain = $"oidc-sso-{Guid.NewGuid():N}".ToLowerInvariant()[..18] + ".com";
        var organizationId = await CreateOrganizationAsync($"SSO Org {Guid.NewGuid():N}", domain);
        var connectionId = await CreateSsoDraftAsync(organizationId, domain);
        await ImportSsoMetadataAsync(connectionId);
        var oidcConnectionId = await UpsertOidcConnectionAsync("Google");

        var startResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/oidc/start", new
        {
            email = $"user@{domain}",
            connectionId = oidcConnectionId
        });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        startJson.RootElement.GetProperty("mode").GetString().Should().Be("sso");
        startJson.RootElement.GetProperty("authorizationUrl").GetString().Should().Contain("SAMLRequest=");
    }

    [TestMethod]
    public async Task OidcCallback_RedirectsWithError_WhenUserHasMultipleOrganizations()
    {
        var email = $"multi-oidc-{Guid.NewGuid():N}@example.com";
        var firstOrgId = await CreateOrganizationAsync($"First Org {Guid.NewGuid():N}");
        var secondOrgId = await CreateOrganizationAsync($"Second Org {Guid.NewGuid():N}");
        var userId = await CreateUserAsync(email, "Multi OIDC User", null);
        await CreateMembershipAsync(firstOrgId, userId, "member");
        await CreateMembershipAsync(secondOrgId, userId, "member");
        var connectionId = await UpsertOidcConnectionAsync("Google");

        var startResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/oidc/start", new
        {
            email,
            connectionId
        });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        var authorizationUrl = startJson.RootElement.GetProperty("authorizationUrl").GetString()!;
        var state = ExtractQueryValue(new Uri(authorizationUrl), "state")!;
        var nonce = ExtractQueryValue(new Uri(authorizationUrl), "nonce")!;

        var callbackResponse = await ExampleApiFixture.Client.GetAsync($"/api/v1/auth/oidc/callback/{connectionId}?code=success:{Uri.EscapeDataString(email)}:{Uri.EscapeDataString(nonce)}&state={Uri.EscapeDataString(state)}");
        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        ExtractQueryValue(callbackResponse.Headers.Location!, "error").Should().Contain("zero or one active organization membership");
    }

    private static async Task<string> UpsertOidcConnectionAsync(string providerType)
    {
        var existingResponse = await ExampleApiFixture.Client.GetAsync("/sqlos/admin/auth/api/oidc-connections");
        existingResponse.EnsureSuccessStatusCode();
        var existingJson = JsonDocument.Parse(await existingResponse.Content.ReadAsStringAsync());
        var existing = existingJson.RootElement.EnumerateArray()
            .FirstOrDefault(x => string.Equals(x.GetProperty("providerType").GetString(), providerType, StringComparison.OrdinalIgnoreCase));

        if (existing.ValueKind != JsonValueKind.Undefined)
        {
            var existingConnectionId = existing.GetProperty("id").GetString()!;
            var updateResponse = await ExampleApiFixture.Client.PutAsJsonAsync($"/sqlos/admin/auth/api/oidc-connections/{existingConnectionId}", new
            {
                displayName = providerType,
                clientId = $"{providerType.ToLowerInvariant()}-client",
                clientSecret = $"{providerType.ToLowerInvariant()}-secret",
                allowedCallbackUris = new[] { OidcCallbackUri(existingConnectionId) },
                useDiscovery = true,
                discoveryUrl = (string?)null,
                issuer = (string?)null,
                authorizationEndpoint = (string?)null,
                tokenEndpoint = (string?)null,
                userInfoEndpoint = (string?)null,
                jwksUri = (string?)null,
                microsoftTenant = providerType == "Microsoft" ? "common" : null,
                scopes = Array.Empty<string>(),
                claimMapping = (object?)null,
                clientAuthMethod = (string?)null,
                useUserInfo = providerType != "Apple",
                appleTeamId = (string?)null,
                appleKeyId = (string?)null,
                applePrivateKeyPem = (string?)null
            });
            updateResponse.EnsureSuccessStatusCode();

            var enableResponse = await ExampleApiFixture.Client.PostAsync($"/sqlos/admin/auth/api/oidc-connections/{existingConnectionId}/enable", null);
            enableResponse.EnsureSuccessStatusCode();
            return existingConnectionId;
        }

        var createResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/oidc-connections", new
        {
            providerType,
            displayName = providerType,
            clientId = $"{providerType.ToLowerInvariant()}-client",
            clientSecret = $"{providerType.ToLowerInvariant()}-secret",
            allowedCallbackUris = new[] { "https://placeholder.invalid/callback" },
            useDiscovery = true,
            discoveryUrl = (string?)null,
            issuer = (string?)null,
            authorizationEndpoint = (string?)null,
            tokenEndpoint = (string?)null,
            userInfoEndpoint = (string?)null,
            jwksUri = (string?)null,
            microsoftTenant = providerType == "Microsoft" ? "common" : null,
            scopes = Array.Empty<string>(),
            claimMapping = (object?)null,
            clientAuthMethod = (string?)null,
            useUserInfo = providerType != "Apple",
            appleTeamId = (string?)null,
            appleKeyId = (string?)null,
            applePrivateKeyPem = (string?)null
        });
        createResponse.EnsureSuccessStatusCode();
        var createdJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var connectionId = createdJson.RootElement.GetProperty("id").GetString()!;
        var finalizeResponse = await ExampleApiFixture.Client.PutAsJsonAsync($"/sqlos/admin/auth/api/oidc-connections/{connectionId}", new
        {
            displayName = providerType,
            clientId = $"{providerType.ToLowerInvariant()}-client",
            clientSecret = $"{providerType.ToLowerInvariant()}-secret",
            allowedCallbackUris = new[] { OidcCallbackUri(connectionId) },
            useDiscovery = true,
            discoveryUrl = (string?)null,
            issuer = (string?)null,
            authorizationEndpoint = (string?)null,
            tokenEndpoint = (string?)null,
            userInfoEndpoint = (string?)null,
            jwksUri = (string?)null,
            microsoftTenant = providerType == "Microsoft" ? "common" : null,
            scopes = Array.Empty<string>(),
            claimMapping = (object?)null,
            clientAuthMethod = (string?)null,
            useUserInfo = providerType != "Apple",
            appleTeamId = (string?)null,
            appleKeyId = (string?)null,
            applePrivateKeyPem = (string?)null
        });
        finalizeResponse.EnsureSuccessStatusCode();
        return connectionId;
    }

    private static async Task<string> UpsertAppleConnectionAsync()
    {
        var existingResponse = await ExampleApiFixture.Client.GetAsync("/sqlos/admin/auth/api/oidc-connections");
        existingResponse.EnsureSuccessStatusCode();
        var existingJson = JsonDocument.Parse(await existingResponse.Content.ReadAsStringAsync());
        var existing = existingJson.RootElement.EnumerateArray()
            .FirstOrDefault(x => string.Equals(x.GetProperty("providerType").GetString(), "Apple", StringComparison.OrdinalIgnoreCase));
        var payload = new
        {
            providerType = "Apple",
            displayName = "Apple",
            clientId = "com.example.service",
            clientSecret = (string?)null,
            allowedCallbackUris = new[] { "https://placeholder.invalid/callback" },
            useDiscovery = true,
            discoveryUrl = (string?)null,
            issuer = (string?)null,
            authorizationEndpoint = (string?)null,
            tokenEndpoint = (string?)null,
            userInfoEndpoint = (string?)null,
            jwksUri = (string?)null,
            microsoftTenant = (string?)null,
            scopes = new[] { "name", "email" },
            claimMapping = (object?)null,
            clientAuthMethod = (string?)null,
            useUserInfo = false,
            appleTeamId = "TEAM123",
            appleKeyId = "KEY123",
            applePrivateKeyPem = TestApplePrivateKeyPem.Value
        };

        if (existing.ValueKind != JsonValueKind.Undefined)
        {
            var existingConnectionId = existing.GetProperty("id").GetString()!;
            var updateResponse = await ExampleApiFixture.Client.PutAsJsonAsync($"/sqlos/admin/auth/api/oidc-connections/{existingConnectionId}", new
            {
                providerType = "Apple",
                displayName = "Apple",
                clientId = "com.example.service",
                clientSecret = (string?)null,
                allowedCallbackUris = new[] { OidcCallbackUri(existingConnectionId) },
                useDiscovery = true,
                discoveryUrl = (string?)null,
                issuer = (string?)null,
                authorizationEndpoint = (string?)null,
                tokenEndpoint = (string?)null,
                userInfoEndpoint = (string?)null,
                jwksUri = (string?)null,
                microsoftTenant = (string?)null,
                scopes = new[] { "name", "email" },
                claimMapping = (object?)null,
                clientAuthMethod = (string?)null,
                useUserInfo = false,
                appleTeamId = "TEAM123",
                appleKeyId = "KEY123",
                applePrivateKeyPem = TestApplePrivateKeyPem.Value
            });
            updateResponse.EnsureSuccessStatusCode();
            await ExampleApiFixture.Client.PostAsync($"/sqlos/admin/auth/api/oidc-connections/{existingConnectionId}/enable", null);
            return existingConnectionId;
        }

        var createResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/oidc-connections", payload);
        createResponse.EnsureSuccessStatusCode();
        var createdJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var connectionId = createdJson.RootElement.GetProperty("id").GetString()!;
        var finalizeResponse = await ExampleApiFixture.Client.PutAsJsonAsync($"/sqlos/admin/auth/api/oidc-connections/{connectionId}", new
        {
            displayName = "Apple",
            clientId = "com.example.service",
            clientSecret = (string?)null,
            allowedCallbackUris = new[] { OidcCallbackUri(connectionId) },
            useDiscovery = true,
            discoveryUrl = (string?)null,
            issuer = (string?)null,
            authorizationEndpoint = (string?)null,
            tokenEndpoint = (string?)null,
            userInfoEndpoint = (string?)null,
            jwksUri = (string?)null,
            microsoftTenant = (string?)null,
            scopes = new[] { "name", "email" },
            claimMapping = (object?)null,
            clientAuthMethod = (string?)null,
            useUserInfo = false,
            appleTeamId = "TEAM123",
            appleKeyId = "KEY123",
            applePrivateKeyPem = TestApplePrivateKeyPem.Value
        });
        finalizeResponse.EnsureSuccessStatusCode();
        return connectionId;
    }

    private static async Task<string> UpsertCustomConnectionAsync()
    {
        var createResponse = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/oidc-connections", new
        {
            providerType = "Custom",
            displayName = $"Custom {Guid.NewGuid():N}",
            clientId = "custom-client",
            clientSecret = "custom-secret",
            allowedCallbackUris = new[] { "https://placeholder.invalid/callback" },
            useDiscovery = false,
            discoveryUrl = (string?)null,
            issuer = "https://oidc.example.local",
            authorizationEndpoint = "https://oidc.example.local/authorize",
            tokenEndpoint = "https://oidc.example.local/token",
            userInfoEndpoint = "https://oidc.example.local/userinfo",
            jwksUri = "https://oidc.example.local/jwks",
            microsoftTenant = (string?)null,
            scopes = new[] { "openid", "profile", "email" },
            claimMapping = new
            {
                subjectClaim = "custom_sub",
                emailClaim = "email_address",
                emailVerifiedClaim = "email_verified_flag",
                displayNameClaim = "full_name",
                firstNameClaim = "given_name",
                lastNameClaim = "family_name",
                preferredUsernameClaim = "preferred_username"
            },
            clientAuthMethod = "ClientSecretPost",
            useUserInfo = true,
            appleTeamId = (string?)null,
            appleKeyId = (string?)null,
            applePrivateKeyPem = (string?)null
        });
        createResponse.EnsureSuccessStatusCode();
        var createdJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var connectionId = createdJson.RootElement.GetProperty("id").GetString()!;
        var finalizeResponse = await ExampleApiFixture.Client.PutAsJsonAsync($"/sqlos/admin/auth/api/oidc-connections/{connectionId}", new
        {
            displayName = $"Custom {Guid.NewGuid():N}",
            clientId = "custom-client",
            clientSecret = "custom-secret",
            allowedCallbackUris = new[] { OidcCallbackUri(connectionId) },
            useDiscovery = false,
            discoveryUrl = (string?)null,
            issuer = "https://oidc.example.local",
            authorizationEndpoint = "https://oidc.example.local/authorize",
            tokenEndpoint = "https://oidc.example.local/token",
            userInfoEndpoint = "https://oidc.example.local/userinfo",
            jwksUri = "https://oidc.example.local/jwks",
            microsoftTenant = (string?)null,
            scopes = new[] { "openid", "profile", "email" },
            claimMapping = new
            {
                subjectClaim = "custom_sub",
                emailClaim = "email_address",
                emailVerifiedClaim = "email_verified_flag",
                displayNameClaim = "full_name",
                firstNameClaim = "given_name",
                lastNameClaim = "family_name",
                preferredUsernameClaim = "preferred_username"
            },
            clientAuthMethod = "ClientSecretPost",
            useUserInfo = true,
            appleTeamId = (string?)null,
            appleKeyId = (string?)null,
            applePrivateKeyPem = (string?)null
        });
        finalizeResponse.EnsureSuccessStatusCode();
        return connectionId;
    }

    private static async Task<string> CreateOrganizationAsync(string name, string? primaryDomain = null)
    {
        var response = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/organizations", new { name, primaryDomain });
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateUserAsync(string email, string displayName, string? password)
    {
        var response = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/users", new
        {
            displayName,
            email,
            password
        });
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task CreateMembershipAsync(string organizationId, string userId, string role)
    {
        var response = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/memberships", new
        {
            organizationId,
            userId,
            role
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> CreateSsoDraftAsync(string organizationId, string primaryDomain)
    {
        var response = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/admin/auth/api/sso-connections/draft", new
        {
            organizationId,
            displayName = "Entra SSO",
            primaryDomain,
            autoProvisionUsers = true,
            autoLinkByEmail = false
        });
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task ImportSsoMetadataAsync(string connectionId)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSEntraMetadata", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var rawCertificate = Convert.ToBase64String(certificate.Export(X509ContentType.Cert));
        var metadataXml = $"""
        <EntityDescriptor xmlns="urn:oasis:names:tc:SAML:2.0:metadata" entityID="https://sts.windows.net/test-tenant/">
          <IDPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
            <KeyDescriptor use="signing">
              <KeyInfo xmlns="http://www.w3.org/2000/09/xmldsig#">
                <X509Data>
                  <X509Certificate>{rawCertificate}</X509Certificate>
                </X509Data>
              </KeyInfo>
            </KeyDescriptor>
            <SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect" Location="https://login.microsoftonline.com/test-tenant/saml2" />
          </IDPSSODescriptor>
        </EntityDescriptor>
        """;

        var response = await ExampleApiFixture.Client.PostAsJsonAsync($"/sqlos/admin/auth/api/sso-connections/{connectionId}/metadata", new
        {
            metadataXml
        });
        response.EnsureSuccessStatusCode();
    }

    private static string OidcCallbackUri(string connectionId)
        => $"{ExampleApiFixture.Client.BaseAddress!.GetLeftPart(UriPartial.Authority)}/api/v1/auth/oidc/callback/{connectionId}";

    private static string? ExtractQueryValue(Uri location, string key)
    {
        var values = location.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => string.Equals(parts[0], key, StringComparison.Ordinal))
            .Select(parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty)
            .ToList();

        return values.Count == 0 ? null : values[0];
    }

    private static readonly Lazy<string> TestApplePrivateKeyPem = new(() =>
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ecdsa.ExportPkcs8PrivateKeyPem();
    });
}
