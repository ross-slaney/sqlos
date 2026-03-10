using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Example.Api.FgaRetail.Seeding;
using SqlOS.Example.IntegrationTests.Infrastructure;

namespace SqlOS.Example.IntegrationTests;

[TestClass]
public sealed class SqlOSExampleRetailFgaIntegrationTests
{
    [TestMethod]
    public async Task CompanyAdmin_CanListAndCreateChains()
    {
        var initialResponse = await GetWithSubjectAsync("/api/chains", RetailSeedService.CompanyAdminSubjectId);
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
        var createResponse = await PostWithSubjectAsync("/api/chains", RetailSeedService.CompanyAdminSubjectId, new
        {
            name = chainName,
            description = "Created from integration tests.",
            headquartersAddress = "100 Test Ave"
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var listResponse = await GetWithSubjectAsync("/api/chains", RetailSeedService.CompanyAdminSubjectId);
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
        var listResponse = await GetWithSubjectAsync("/api/chains", RetailSeedService.ChainManagerWalmartSubjectId);
        listResponse.EnsureSuccessStatusCode();
        var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var chains = listJson.RootElement.GetProperty("data").EnumerateArray().ToList();

        chains.Should().HaveCount(1);
        chains[0].GetProperty("name").GetString().Should().Be("Walmart");

        var deniedDetail = await GetWithSubjectAsync("/api/chains/chain_target", RetailSeedService.ChainManagerWalmartSubjectId);
        deniedDetail.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var allowedCreate = await PostWithSubjectAsync("/api/chains/chain_walmart/locations", RetailSeedService.ChainManagerWalmartSubjectId, new
        {
            name = $"Managed Location {Guid.NewGuid():N}"[..28],
            storeNumber = $"T{Random.Shared.Next(1000, 9999)}",
            address = "200 Test Blvd",
            city = "Springfield",
            state = "MO",
            zipCode = "65802"
        });
        allowedCreate.StatusCode.Should().Be(HttpStatusCode.Created);

        var deniedCreate = await PostWithSubjectAsync("/api/chains/chain_target/locations", RetailSeedService.ChainManagerWalmartSubjectId, new
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
        var locationsResponse = await GetWithSubjectAsync("/api/chains/chain_walmart/locations", RetailSeedService.StoreClerk001SubjectId);
        locationsResponse.EnsureSuccessStatusCode();
        var locationsJson = JsonDocument.Parse(await locationsResponse.Content.ReadAsStringAsync());
        var locations = locationsJson.RootElement.GetProperty("data").EnumerateArray().ToList();

        locations.Should().HaveCount(1);
        locations[0].GetProperty("id").GetString().Should().Be("loc_001");

        var inventoryResponse = await GetWithSubjectAsync("/api/locations/loc_001/inventory", RetailSeedService.StoreClerk001SubjectId);
        inventoryResponse.EnsureSuccessStatusCode();
        var inventoryJson = JsonDocument.Parse(await inventoryResponse.Content.ReadAsStringAsync());
        var inventoryNames = inventoryJson.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .ToList();

        inventoryNames.Should().Contain("ProBook Laptop");
        inventoryNames.Should().Contain("SmartPhone X");

        var createResponse = await PostWithSubjectAsync("/api/locations/loc_001/inventory", RetailSeedService.StoreClerk001SubjectId, new
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
        var response = await GetWithSubjectAsync("/api/chains", RetailSeedService.RegionalUserAliceSubjectId);
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var chains = json.RootElement.GetProperty("data").EnumerateArray().ToList();

        chains.Should().HaveCount(1);
        chains[0].GetProperty("id").GetString().Should().Be("chain_walmart");
    }

    private static async Task<HttpResponseMessage> GetWithSubjectAsync(string path, string subjectId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Subject-Id", subjectId);
        return await ExampleApiFixture.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostWithSubjectAsync(string path, string subjectId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Subject-Id", subjectId);
        return await ExampleApiFixture.Client.SendAsync(request);
    }
}
