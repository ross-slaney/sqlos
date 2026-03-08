using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sqlzibar.Example.IntegrationTests.Infrastructure;

namespace Sqlzibar.Example.IntegrationTests;

[TestClass]
public class ChainApiTests
{
    private HttpClient _client = null!;
    private const string AdminSubjectId = "subj_company_admin";

    [TestInitialize]
    public void TestInit()
    {
        _client = ExampleApiFixture.Client;
    }

    [TestMethod]
    public async Task GetChains_ReturnsPaginatedResult()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/chains");
        request.Headers.Add("X-Subject-Id", AdminSubjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<PaginatedResponse<ChainResponse>>();
        json.Should().NotBeNull();
        json!.PageSize.Should().Be(10);
        json.Data.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [TestMethod]
    public async Task GetChains_WithSearch_FiltersCorrectly()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/chains?search=walmart");
        request.Headers.Add("X-Subject-Id", AdminSubjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<PaginatedResponse<ChainResponse>>();
        json.Should().NotBeNull();
        json!.Data.Should().ContainSingle();
        json.Data[0].Name.Should().Be("Walmart");
    }

    [TestMethod]
    public async Task GetChain_ValidId_ReturnsDetail()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/chains/chain_walmart");
        request.Headers.Add("X-Subject-Id", AdminSubjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<ChainDetailResponse>();
        json.Should().NotBeNull();
        json!.Name.Should().Be("Walmart");
        json.LocationCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [TestMethod]
    public async Task GetChain_InvalidId_Returns404()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/chains/nonexistent");
        request.Headers.Add("X-Subject-Id", AdminSubjectId);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private record PaginatedResponse<T>(List<T> Data, int PageSize, string? NextCursor, bool HasNextPage);
    private record ChainResponse(string Id, string Name, string? Description);
    private record ChainDetailResponse(string Id, string Name, string? Description, string? HeadquartersAddress, int LocationCount);
}
