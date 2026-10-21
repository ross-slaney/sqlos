using SqlOS.Security;

namespace SqlOS.Tests.Infrastructure;

internal static class DashboardRequestProof
{
    /// <summary>
    /// Makes a test client send what the dashboard page sends on every call: the
    /// <c>X-SqlOS-Request</c> header, plus the page's own origin as a browser would.
    /// </summary>
    public static HttpClient WithDashboardRequestProof(this HttpClient client)
    {
        client.DefaultRequestHeaders.Remove(SqlOSCookieMutationCsrf.HeaderName);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add(SqlOSCookieMutationCsrf.HeaderName, SqlOSCookieMutationCsrf.HeaderValue);
        client.DefaultRequestHeaders.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return client;
    }
}
