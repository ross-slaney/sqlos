using System.Net;
using System.Text;
using SqlOS.AuthServer.Services;

namespace SqlOS.IntegrationTests.Infrastructure;

/// <summary>
/// One <see cref="IHttpClientFactory"/> for hosts that resolve CIMD clients and run social login:
/// CIMD metadata fetches return the supplied documents (404 otherwise), and every other named
/// client talks to the fake OIDC provider.
/// </summary>
internal sealed class FakeCimdAndOidcHttpClientFactory(IReadOnlyDictionary<string, string> metadataDocuments) : IHttpClientFactory
{
    private readonly FakeOidcProviderHttpClientFactory _oidcProvider = new();

    public HttpClient CreateClient(string name)
        => string.Equals(name, nameof(SqlOSCimdClientService), StringComparison.Ordinal)
            ? new HttpClient(new MetadataDocumentHandler(metadataDocuments))
            : _oidcProvider.CreateClient(name);

    private sealed class MetadataDocumentHandler(IReadOnlyDictionary<string, string> documents) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            return Task.FromResult(documents.TryGetValue(url, out var document)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(document, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }
}
