using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sqlzibar.Example.IntegrationTests.Infrastructure;

namespace Sqlzibar.Example.IntegrationTests;

[TestClass]
public class LocationApiTests
{
    private HttpClient _client = null!;
    private const string AdminSubjectId = "subj_company_admin";
    private const string ChainManagerSubjectId = "subj_chain_mgr_walmart";

    [TestInitialize]
    public void TestInit()
    {
        _client = ExampleApiFixture.Client;
    }

    [TestMethod]
    public async Task GetLocations_ReturnsPaginatedResult()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/chains/chain_walmart/locations");
        request.Headers.Add("X-Subject-Id", AdminSubjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<PaginatedResponse<LocationResponse>>();
        json.Should().NotBeNull();
        json!.Data.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [TestMethod]
    public async Task GetLocation_ValidId_ReturnsDetail()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/locations/loc_001");
        request.Headers.Add("X-Subject-Id", AdminSubjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<LocationDetailResponse>();
        json.Should().NotBeNull();
        json!.StoreNumber.Should().Be("001");
    }

    [TestMethod]
    public async Task GetLocation_InvalidId_Returns404()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/locations/nonexistent");
        request.Headers.Add("X-Subject-Id", AdminSubjectId);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task CreateLocation_WithPermission_Returns201()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chains/chain_walmart/locations");
        request.Headers.Add("X-Subject-Id", ChainManagerSubjectId);
        request.Content = JsonContent.Create(new
        {
            Name = "New Walmart Store",
            StoreNumber = "999",
            City = "Dallas",
            State = "TX"
        });

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task CreateLocation_WithoutPermission_Returns403()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chains/chain_walmart/locations");
        request.Headers.Add("X-Subject-Id", "subj_store_clerk_001");
        request.Content = JsonContent.Create(new
        {
            Name = "Unauthorized Store",
            StoreNumber = "000"
        });

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private record PaginatedResponse<T>(List<T> Data, int PageSize, string? NextCursor, bool HasNextPage);
    private record LocationResponse(string Id, string Name, string? StoreNumber);
    private record LocationDetailResponse(string Id, string Name, string? StoreNumber, string? City, string? State);
}
