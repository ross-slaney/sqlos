using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sqlzibar.Example.IntegrationTests.Infrastructure;

namespace Sqlzibar.Example.IntegrationTests;

/// <summary>
/// Tests that user group membership correctly inherits grants.
///
/// Setup: "Walmart Regional Managers" group has ChainManager grant on res_chain_walmart.
/// Alice and Bob are members of this group (no direct grants).
/// They should see exactly what an individual ChainManager on Walmart would see.
/// </summary>
[TestClass]
public class UserGroupTests
{
    private HttpClient _client = null!;

    private const string Alice = "subj_regional_alice";
    private const string Bob = "subj_regional_bob";
    private const string NoGrants = "subj_no_grants";

    [TestInitialize]
    public void TestInit()
    {
        _client = ExampleApiFixture.Client;
    }

    // ====================================================================
    // CHAIN ACCESS (ChainManager has CHAIN_VIEW)
    // ====================================================================

    [TestMethod]
    public async Task GroupMember_Alice_SeesWalmartChain()
    {
        var result = await GetChainsAsync(Alice);

        result.Data.Should().ContainSingle()
            .Which.Name.Should().Be("Walmart");
    }

    [TestMethod]
    public async Task GroupMember_Bob_SeesWalmartChain()
    {
        var result = await GetChainsAsync(Bob);

        result.Data.Should().ContainSingle()
            .Which.Name.Should().Be("Walmart");
    }

    [TestMethod]
    public async Task GroupMember_Alice_DoesNotSeeTargetChain()
    {
        var result = await GetChainsAsync(Alice, search: "target");

        result.Data.Should().BeEmpty();
    }

    // ====================================================================
    // LOCATION ACCESS (ChainManager has LOCATION_VIEW, cascades from chain)
    // ====================================================================

    [TestMethod]
    public async Task GroupMember_Alice_SeesWalmartLocations()
    {
        var result = await GetLocationsAsync(Alice, "chain_walmart");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(l => l.StoreNumber).Should().Contain("001").And.Contain("002");
    }

    [TestMethod]
    public async Task GroupMember_Bob_SeesWalmartLocations()
    {
        var result = await GetLocationsAsync(Bob, "chain_walmart");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(l => l.StoreNumber).Should().Contain("001").And.Contain("002");
    }

    [TestMethod]
    public async Task GroupMember_Alice_SeesNoTargetLocations()
    {
        var result = await GetLocationsAsync(Alice, "chain_target");

        result.Data.Should().BeEmpty();
    }

    // ====================================================================
    // INVENTORY ACCESS (ChainManager has INVENTORY_VIEW, cascades from chain)
    // ====================================================================

    [TestMethod]
    public async Task GroupMember_Alice_SeesStore001Inventory()
    {
        var result = await GetInventoryAsync(Alice, "loc_001");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(i => i.Name).Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");
    }

    [TestMethod]
    public async Task GroupMember_Alice_SeesStore002Inventory()
    {
        var result = await GetInventoryAsync(Alice, "loc_002");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Select(i => i.Name).Should().Contain("TabPro 11");
    }

    [TestMethod]
    public async Task GroupMember_Bob_SeesStore001Inventory()
    {
        var result = await GetInventoryAsync(Bob, "loc_001");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(i => i.Name).Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");
    }

    [TestMethod]
    public async Task GroupMember_Alice_SeesNoTargetInventory()
    {
        var result = await GetInventoryAsync(Alice, "loc_100");

        result.Data.Should().BeEmpty();
    }

    // ====================================================================
    // WRITE ACCESS (ChainManager has LOCATION_EDIT and INVENTORY_EDIT)
    // ====================================================================

    [TestMethod]
    public async Task GroupMember_Alice_CanCreateLocationInWalmartChain()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chains/chain_walmart/locations");
        request.Headers.Add("X-Subject-Id", Alice);
        request.Content = JsonContent.Create(new
        {
            Name = "Alice Test Store",
            StoreNumber = "A01",
            Address = "1 Test Rd",
            City = "Test City",
            State = "TX",
            ZipCode = "75001"
        });

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task GroupMember_Alice_CannotCreateChain()
    {
        // ChainManager has no CHAIN_EDIT permission
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chains");
        request.Headers.Add("X-Subject-Id", Alice);
        request.Content = JsonContent.Create(new { Name = "Forbidden Chain" });

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ====================================================================
    // BOTH MEMBERS SEE SAME DATA (symmetry check)
    // ====================================================================

    [TestMethod]
    public async Task GroupMembers_AliceAndBob_SeeIdenticalChains()
    {
        var aliceResult = await GetChainsAsync(Alice);
        var bobResult = await GetChainsAsync(Bob);

        aliceResult.Data.Select(c => c.Name).Should().BeEquivalentTo(bobResult.Data.Select(c => c.Name));
    }

    [TestMethod]
    public async Task GroupMembers_AliceAndBob_SeeIdenticalLocations()
    {
        var aliceResult = await GetLocationsAsync(Alice, "chain_walmart");
        var bobResult = await GetLocationsAsync(Bob, "chain_walmart");

        aliceResult.Data.Select(l => l.StoreNumber).Should()
            .BeEquivalentTo(bobResult.Data.Select(l => l.StoreNumber));
    }

    // ====================================================================
    // NON-MEMBER CANNOT ACCESS (confirms it's the group, not something else)
    // ====================================================================

    [TestMethod]
    public async Task NonGroupMember_NoGrants_SeesNoChains()
    {
        var result = await GetChainsAsync(NoGrants);

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NonGroupMember_NoGrants_SeesNoLocations()
    {
        var result = await GetLocationsAsync(NoGrants, "chain_walmart");

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NonGroupMember_NoGrants_SeesNoInventory()
    {
        var result = await GetInventoryAsync(NoGrants, "loc_001");

        result.Data.Should().BeEmpty();
    }

    // ====================================================================
    // HELPERS
    // ====================================================================

    private async Task<PaginatedResult<ChainItem>> GetChainsAsync(
        string subjectId, string? search = null)
    {
        var url = "/api/chains?pageSize=10";
        if (search != null) url += $"&search={search}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Subject-Id", subjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} as {subjectId}");

        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<ChainItem>>();
        result.Should().NotBeNull();
        return result!;
    }

    private async Task<PaginatedResult<LocationItem>> GetLocationsAsync(
        string subjectId, string chainId)
    {
        var url = $"/api/chains/{chainId}/locations?pageSize=10";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Subject-Id", subjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} as {subjectId}");

        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<LocationItem>>();
        result.Should().NotBeNull();
        return result!;
    }

    private async Task<PaginatedResult<InventoryItemResult>> GetInventoryAsync(
        string subjectId, string locationId)
    {
        var url = $"/api/locations/{locationId}/inventory?pageSize=10";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Subject-Id", subjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} as {subjectId}");

        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<InventoryItemResult>>();
        result.Should().NotBeNull();
        return result!;
    }

    // Response DTOs
    private record PaginatedResult<T>(List<T> Data, int PageSize, string? NextCursor, bool HasNextPage);
    private record ChainItem(string Id, string Name, string? Description);
    private record LocationItem(string Id, string Name, string? StoreNumber, string? City, string? State);
    private record InventoryItemResult(string Id, string Name, string Sku, decimal Price);
}
