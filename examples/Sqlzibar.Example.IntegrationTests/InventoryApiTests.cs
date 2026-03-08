using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sqlzibar.Example.IntegrationTests.Infrastructure;

namespace Sqlzibar.Example.IntegrationTests;

[TestClass]
public class InventoryApiTests
{
    private HttpClient _client = null!;
    private const string AdminSubjectId = "subj_company_admin";
    private const string StoreManagerSubjectId = "subj_store_mgr_001";

    [TestInitialize]
    public void TestInit()
    {
        _client = ExampleApiFixture.Client;
    }

    [TestMethod]
    public async Task GetInventory_ReturnsPaginatedResult()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/locations/loc_001/inventory");
        request.Headers.Add("X-Subject-Id", AdminSubjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<PaginatedResponse<InventoryResponse>>();
        json.Should().NotBeNull();
        json!.Data.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [TestMethod]
    public async Task GetInventoryItem_ValidId_ReturnsDetail()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/inventory/inv_laptop");
        request.Headers.Add("X-Subject-Id", AdminSubjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<InventoryDetailResponse>();
        json.Should().NotBeNull();
        json!.Sku.Should().Be("ELEC-LAPTOP-001");
    }

    [TestMethod]
    public async Task GetInventoryItem_InvalidId_Returns404()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/inventory/nonexistent");
        request.Headers.Add("X-Subject-Id", AdminSubjectId);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task CreateInventoryItem_WithPermission_Returns201()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/locations/loc_001/inventory");
        request.Headers.Add("X-Subject-Id", StoreManagerSubjectId);
        request.Content = JsonContent.Create(new
        {
            Sku = "NEW-ITEM-001",
            Name = "New Item",
            Description = "A new inventory item",
            Price = 29.99,
            QuantityOnHand = 100
        });

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task CreateInventoryItem_WithoutPermission_Returns403()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/locations/loc_001/inventory");
        request.Headers.Add("X-Subject-Id", "subj_store_clerk_001");
        request.Content = JsonContent.Create(new
        {
            Sku = "UNAUTH-001",
            Name = "Unauthorized Item",
            Price = 9.99,
            QuantityOnHand = 1
        });

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private record PaginatedResponse<T>(List<T> Data, int PageSize, string? NextCursor, bool HasNextPage);
    private record InventoryResponse(string Id, string Name, string Sku);
    private record InventoryDetailResponse(string Id, string Name, string Sku, decimal Price, int QuantityOnHand);
}
