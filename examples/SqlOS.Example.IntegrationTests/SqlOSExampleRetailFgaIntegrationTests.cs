using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Example.IntegrationTests.Infrastructure;

namespace SqlOS.Example.IntegrationTests;

[TestClass]
public sealed class SqlOSExampleRetailFgaIntegrationTests
{
    [TestMethod]
    public async Task CompanyAdmin_CanListAndCreateChains()
    {
        var companyAdminEmail = await GetUserEmailByDisplayNameAsync("Company Admin");
        var initialResponse = await GetAsUserAsync("/api/chains", companyAdminEmail);
        initialResponse.EnsureSuccessStatusCode();
        var initialJson = JsonDocument.Parse(await initialResponse.Content.ReadAsStringAsync());
        var initialNames = initialJson.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .ToList();

        initialNames.Should().Contain("Walmart");
        initialNames.Should().Contain("Target");
        initialNames.Should().Contain("Costco");
        initialNames.Should().Contain("Kroger");
        initialNames.Should().Contain("Aldi");

        var chainName = $"Example Chain {Guid.NewGuid():N}"[..26];
        var createResponse = await PostAsUserAsync("/api/chains", companyAdminEmail, new
        {
            name = chainName,
            description = "Created from integration tests.",
            headquartersAddress = "100 Test Ave"
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var listResponse = await GetAsUserAsync("/api/chains", companyAdminEmail);
        listResponse.EnsureSuccessStatusCode();
        var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var names = listJson.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .ToList();

        names.Should().Contain(chainName);
    }

    [TestMethod]
    public async Task ChainManager_IsScopedToAssignedChain_AndCanManageChildLocations()
    {
        var walmartManagerEmail = await GetUserEmailByDisplayNameAsync("Walmart Chain Manager");
        var listResponse = await GetAsUserAsync("/api/chains", walmartManagerEmail);
        listResponse.EnsureSuccessStatusCode();
        var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var chains = listJson.RootElement.GetProperty("data").EnumerateArray().ToList();

        chains.Should().HaveCount(1);
        chains[0].GetProperty("name").GetString().Should().Be("Walmart");

        var deniedDetail = await GetAsUserAsync("/api/chains/chain_target", walmartManagerEmail);
        deniedDetail.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var allowedCreate = await PostAsUserAsync("/api/chains/chain_walmart/locations", walmartManagerEmail, new
        {
            name = $"Managed Location {Guid.NewGuid():N}"[..28],
            storeNumber = $"T{Random.Shared.Next(1000, 9999)}",
            address = "200 Test Blvd",
            city = "Springfield",
            state = "MO",
            zipCode = "65802"
        });
        allowedCreate.StatusCode.Should().Be(HttpStatusCode.Created);

        var deniedCreate = await PostAsUserAsync("/api/chains/chain_target/locations", walmartManagerEmail, new
        {
            name = "Should Fail",
            storeNumber = "9999",
            address = "1 No Way",
            city = "Minneapolis",
            state = "MN",
            zipCode = "55401"
        });
        deniedCreate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task StoreClerk_CanViewAssignedInventory_ButCannotCreateInventory()
    {
        var storeClerkEmail = await GetUserEmailByDisplayNameAsync("Store 001 Clerk");
        var locationsResponse = await GetAsUserAsync("/api/chains/chain_walmart/locations", storeClerkEmail);
        locationsResponse.EnsureSuccessStatusCode();
        var locationsJson = JsonDocument.Parse(await locationsResponse.Content.ReadAsStringAsync());
        var locations = locationsJson.RootElement.GetProperty("data").EnumerateArray().ToList();

        locations.Should().HaveCount(1);
        locations[0].GetProperty("id").GetString().Should().Be("loc_001");

        var inventoryResponse = await GetAsUserAsync("/api/locations/loc_001/inventory", storeClerkEmail);
        inventoryResponse.EnsureSuccessStatusCode();
        var inventoryJson = JsonDocument.Parse(await inventoryResponse.Content.ReadAsStringAsync());
        var inventoryNames = inventoryJson.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .ToList();

        inventoryNames.Should().Contain("ProBook Laptop");
        inventoryNames.Should().Contain("SmartPhone X");

        var createResponse = await PostAsUserAsync("/api/locations/loc_001/inventory", storeClerkEmail, new
        {
            sku = $"SKU-{Guid.NewGuid():N}"[..12],
            name = "Blocked Item",
            description = "Should not be created by clerk.",
            price = 19.99m,
            quantityOnHand = 2
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task GroupMembership_GrantsInheritedRetailAccess()
    {
        var aliceEmail = await GetUserEmailByDisplayNameAsync("Alice (Regional)");
        var response = await GetAsUserAsync("/api/chains", aliceEmail);
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var chains = json.RootElement.GetProperty("data").EnumerateArray().ToList();

        chains.Should().HaveCount(1);
        chains[0].GetProperty("id").GetString().Should().Be("chain_walmart");
    }

    [TestMethod]
    public async Task Agent_HasStoreManagerAccessOnWalmart()
    {
        var agentSubjectId = await GetAgentSubjectIdAsync();
        var chainsResponse = await GetAsAgentAsync("/api/chains", agentSubjectId);
        chainsResponse.EnsureSuccessStatusCode();
        var chainsJson = JsonDocument.Parse(await chainsResponse.Content.ReadAsStringAsync());
        var chains = chainsJson.RootElement.GetProperty("data").EnumerateArray().ToList();

        chains.Should().HaveCount(1);
        chains[0].GetProperty("name").GetString().Should().Be("Walmart");

        var locationsResponse = await GetAsAgentAsync("/api/chains/chain_walmart/locations", agentSubjectId);
        locationsResponse.EnsureSuccessStatusCode();
        var locationsJson = JsonDocument.Parse(await locationsResponse.Content.ReadAsStringAsync());
        var locations = locationsJson.RootElement.GetProperty("data").EnumerateArray().ToList();

        locations.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task ServiceAccount_HasReadOnlyAccessAtRetailRoot()
    {
        var apiClientId = await GetServiceAccountClientIdAsync();
        var locationsResponse = await GetAsApiKeyAsync("/api/locations", apiClientId);
        locationsResponse.EnsureSuccessStatusCode();
        var locationsJson = JsonDocument.Parse(await locationsResponse.Content.ReadAsStringAsync());
        var locations = locationsJson.RootElement.GetProperty("data").EnumerateArray().ToList();

        locations.Should().NotBeEmpty("service account has StoreClerk on retail_root, inheriting location view access");

        var createResponse = await PostAsApiKeyAsync("/api/chains", apiClientId, new
        {
            name = "Should Fail",
            description = "Service account is read-only."
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task InvalidApiKey_Returns401()
    {
        var response = await GetAsApiKeyAsync("/api/chains", "nonexistent_client_id");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task InvalidAgentToken_Returns401()
    {
        var response = await GetAsAgentAsync("/api/chains", "nonexistent_agent_subject");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<IReadOnlyList<DemoSubject>> GetDemoSubjectsAsync()
    {
        var response = await ExampleApiFixture.Client.GetAsync("/api/demo/users");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var subjects = JsonSerializer.Deserialize<List<DemoSubject>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return subjects ?? [];
    }

    private static async Task<string> GetUserEmailByDisplayNameAsync(string displayName)
    {
        var subjects = await GetDemoSubjectsAsync();
        var email = subjects
            .Where(s => s.Type == "user" && s.DisplayName == displayName)
            .Select(s => s.Email)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException($"Could not resolve demo user email for '{displayName}'.");

        return email;
    }

    private static async Task<string> GetAgentSubjectIdAsync()
    {
        var subjects = await GetDemoSubjectsAsync();
        var credential = subjects
            .Where(s => s.Type == "agent" && (s.Role?.Contains("StoreManager", StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(s => s.Credential)
            .FirstOrDefault()
            ?? subjects
                .Where(s => s.Type == "agent")
                .Select(s => s.Credential)
                .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(credential))
            throw new InvalidOperationException("Could not resolve an agent credential from /api/demo/users.");

        return credential;
    }

    private static async Task<string> GetServiceAccountClientIdAsync()
    {
        var subjects = await GetDemoSubjectsAsync();
        var credential = subjects
            .Where(s => s.Type == "service_account" && (s.Role?.Contains("StoreClerk", StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(s => s.Credential)
            .FirstOrDefault()
            ?? subjects
                .Where(s => s.Type == "service_account")
                .Select(s => s.Credential)
                .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(credential))
            throw new InvalidOperationException("Could not resolve a service account credential from /api/demo/users.");

        return credential;
    }

    private static async Task<string> GetAccessTokenAsync(string email)
    {
        var response = await ExampleApiFixture.Client.PostAsJsonAsync("/api/v1/auth/demo/switch", new { email });
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> GetAsUserAsync(string path, string email)
    {
        var token = await GetAccessTokenAsync(email);
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await ExampleApiFixture.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostAsUserAsync(string path, string email, object body)
    {
        var token = await GetAccessTokenAsync(email);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await ExampleApiFixture.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsAgentAsync(string path, string agentSubjectId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Agent-Token", agentSubjectId);
        return await ExampleApiFixture.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsApiKeyAsync(string path, string clientId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Api-Key", clientId);
        return await ExampleApiFixture.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostAsApiKeyAsync(string path, string clientId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Api-Key", clientId);
        return await ExampleApiFixture.Client.SendAsync(request);
    }

    private sealed record DemoSubject(
        string? Email,
        string DisplayName,
        string? Role,
        string? Description,
        string Type,
        string? Credential);
}
