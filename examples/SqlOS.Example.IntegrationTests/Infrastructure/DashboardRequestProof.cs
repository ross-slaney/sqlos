using System.Net.Http.Json;

namespace SqlOS.Example.IntegrationTests.Infrastructure;

internal static class DashboardRequestProof
{
    /// <summary>
    /// Makes a test client send what the SqlOS dashboard page sends on every call: the
    /// <c>X-SqlOS-Request</c> header, plus the page's own origin as a browser would.
    /// </summary>
    public static HttpClient WithDashboardRequestProof(this HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("X-SqlOS-Request");
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("X-SqlOS-Request", "1");
        client.DefaultRequestHeaders.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return client;
    }

    /// <summary>Posts JSON to an admin route the way the dashboard page does, for one request only.</summary>
    public static Task<HttpResponseMessage> PostAsDashboardJsonAsync(this HttpClient client, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-SqlOS-Request", "1");
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return client.SendAsync(request);
    }
}
