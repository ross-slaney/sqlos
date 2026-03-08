using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sqlzibar.Example.IntegrationTests.Infrastructure;

namespace Sqlzibar.Example.IntegrationTests;

[TestClass]
public class AuthorizationFlowTests
{
    private HttpClient _client = null!;

    [TestInitialize]
    public void TestInit()
    {
        _client = ExampleApiFixture.Client;
    }

    [TestMethod]
    public async Task Request_WithoutHeader_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/chains");
        // No X-Subject-Id header

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task CompanyAdmin_GetChains_SeesAllChains()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/chains");
        request.Headers.Add("X-Subject-Id", "subj_company_admin");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<PaginatedResponse<ChainResponse>>();
        json.Should().NotBeNull();
        json!.Data.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [TestMethod]
    public async Task StoreClerk_GetChains_SeesNoChains()
    {
        // Store clerk only has LOCATION_VIEW and INVENTORY_VIEW, not CHAIN_VIEW
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/chains");
        request.Headers.Add("X-Subject-Id", "subj_store_clerk_001");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<PaginatedResponse<ChainResponse>>();
        json.Should().NotBeNull();
        json!.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task StoreClerk_CreateChain_Returns403()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chains");
        request.Headers.Add("X-Subject-Id", "subj_store_clerk_001");
        request.Content = JsonContent.Create(new { Name = "New Chain" });

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task CompanyAdmin_CreateChain_Returns201()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chains");
        request.Headers.Add("X-Subject-Id", "subj_company_admin");
        request.Content = JsonContent.Create(new { Name = "Publix", Description = "Supermarket chain" });

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task ChainManager_GetLocations_SeesLocationsInTheirChain()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/chains/chain_walmart/locations");
        request.Headers.Add("X-Subject-Id", "subj_chain_mgr_walmart");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<PaginatedResponse<LocationResponse>>();
        json.Should().NotBeNull();
        json!.Data.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [TestMethod]
    public async Task StoreManager_GetInventory_SeesItemsInTheirStore()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/locations/loc_001/inventory");
        request.Headers.Add("X-Subject-Id", "subj_store_mgr_001");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<PaginatedResponse<InventoryResponse>>();
        json.Should().NotBeNull();
        json!.Data.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    // Helper response types for deserialization
    private record PaginatedResponse<T>(List<T> Data, int PageSize, string? NextCursor, bool HasNextPage);
    private record ChainResponse(string Id, string Name);
    private record LocationResponse(string Id, string Name, string? StoreNumber);
    private record InventoryResponse(string Id, string Name, string Sku);
}
