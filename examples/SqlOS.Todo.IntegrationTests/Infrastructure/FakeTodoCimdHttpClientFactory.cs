using System.Net;
using System.Text;

namespace SqlOS.Todo.IntegrationTests.Infrastructure;

internal sealed class FakeTodoCimdHttpClientFactory(IReadOnlyDictionary<string, string> responses) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
        => new(new FakeTodoCimdHttpMessageHandler(responses));

    private sealed class FakeTodoCimdHttpMessageHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (responses.TryGetValue(url, out var payload))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"not_found\"}", Encoding.UTF8, "application/json")
            });
        }
    }
}
