using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SqlOS.BehaviorLock.Host.Fakes;

/// <summary>
/// The only <c>IHttpClientFactory</c> a behavior-lock host has. Every outbound call SqlOS makes is
/// answered by an in-process fake upstream and recorded as an <see cref="HttpEffect"/>; nothing
/// reaches the network. Routing is by the named client SqlOS asks for:
/// <list type="bullet">
/// <item><c>SqlOSCimdClientService</c>: client ID metadata documents published with <see cref="CimdDocuments"/> (404 otherwise);</item>
/// <item><c>SqlOSCalendarService</c>, <c>SqlOSGoogleCalendarAdapter</c>, <c>SqlOSMicrosoftGraphCalendarAdapter</c>: <see cref="FakeCalendarUpstream"/>;</item>
/// <item>every other client (social and custom OIDC): <see cref="FakeOidcUpstream"/>.</item>
/// </list>
/// </summary>
public sealed class RecordingHttpClientFactory : IHttpClientFactory
{
    private static readonly HashSet<string> CalendarClients = new(StringComparer.Ordinal)
    {
        "SqlOSCalendarService",
        "SqlOSGoogleCalendarAdapter",
        "SqlOSMicrosoftGraphCalendarAdapter"
    };

    private readonly EffectLog _effects;
    private readonly FakeOidcUpstream _oidc;
    private readonly FakeCalendarUpstream _calendar;

    public RecordingHttpClientFactory(EffectLog effects, CimdDocuments cimdDocuments, FakeOidcUpstream oidc, FakeCalendarUpstream calendar)
    {
        _effects = effects;
        CimdDocuments = cimdDocuments;
        _oidc = oidc;
        _calendar = calendar;
    }

    public CimdDocuments CimdDocuments { get; }

    public HttpClient CreateClient(string name)
        => new(new RecordingHandler(name, this)) { BaseAddress = new Uri("https://localhost") };

    private Task<HttpResponseMessage> RouteAsync(string clientName, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (string.Equals(clientName, "SqlOSCimdClientService", StringComparison.Ordinal))
        {
            return Task.FromResult(CimdDocuments.Respond(request));
        }

        return CalendarClients.Contains(clientName)
            ? _calendar.SendAsync(request, cancellationToken)
            : _oidc.SendAsync(request, cancellationToken);
    }

    private sealed class RecordingHandler(string clientName, RecordingHttpClientFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? requestBody = null;
            string? contentType = null;
            if (request.Content != null)
            {
                requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                contentType = request.Content.Headers.ContentType?.ToString();
                // The fake upstream reads the body again; give it a fresh copy.
                var replacement = new StringContent(requestBody, Encoding.UTF8);
                replacement.Headers.ContentType = request.Content.Headers.ContentType;
                request.Content = replacement;
            }

            var response = await owner.RouteAsync(clientName, request, cancellationToken);
            response.RequestMessage = request;
            owner._effects.Record(new HttpEffect(
                clientName,
                request.Method.Method,
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                contentType,
                requestBody,
                (int)response.StatusCode));
            return response;
        }
    }
}

/// <summary>
/// Client ID metadata documents the fake network serves, keyed by exact URL. Scenarios publish
/// documents before an authorization request names the URL as its <c>client_id</c>.
/// </summary>
public sealed class CimdDocuments
{
    private readonly ConcurrentDictionary<string, string> _documents = new(StringComparer.Ordinal);

    public void Publish(string url, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        _documents[url] = json;
    }

    public void Unpublish(string url) => _documents.TryRemove(url, out _);

    internal HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
        return _documents.TryGetValue(url, out var document)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(document, Encoding.UTF8, "application/json") }
            : FakeOidcUpstream.Json(HttpStatusCode.NotFound, new JsonObject());
    }
}
