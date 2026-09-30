using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Fakes;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// Someone who talks to the host: a browser (keeps cookies and sends <c>Origin</c> on unsafe
/// requests, as browsers do), the operator (adds the profile's admin credential), or a plain API
/// client (no cookies). Every request is numbered and captured as an <see cref="HttpExchange"/>;
/// nothing is recorded until the scenario observes it.
/// </summary>
public sealed class HttpActor
{
    private readonly Transcript _transcript;
    private readonly Func<HttpRequestMessage, Task>? _authenticate;

    internal HttpActor(Transcript transcript, string name, bool isBrowser, Func<HttpRequestMessage, Task>? authenticate = null)
    {
        _transcript = transcript;
        Name = name;
        IsBrowser = isBrowser;
        _authenticate = authenticate;
        Cookies = isBrowser || authenticate != null ? new CookieContainer() : null;
    }

    public string Name { get; }

    public bool IsBrowser { get; }

    /// <summary>The actor's cookie jar, or null for API clients that never keep cookies.</summary>
    public CookieContainer? Cookies { get; }

    public Task<HttpExchange> GetAsync(string target, Action<RequestOptions>? configure = null)
        => SendAsync(HttpMethod.Get, target, content: null, configure);

    public Task<HttpExchange> DeleteAsync(string target, Action<RequestOptions>? configure = null)
        => SendAsync(HttpMethod.Delete, target, content: null, configure);

    public Task<HttpExchange> PostFormAsync(string target, IEnumerable<KeyValuePair<string, string>> fields, Action<RequestOptions>? configure = null)
        => SendAsync(HttpMethod.Post, target, new FormUrlEncodedContent(fields), configure);

    public Task<HttpExchange> PostFormAsync(string target, object fields, Action<RequestOptions>? configure = null)
        => PostFormAsync(target, ToFields(fields), configure);

    /// <summary>Submits a parsed HTML form to its action, as a browser would.</summary>
    public Task<HttpExchange> SubmitAsync(HtmlForm form, Action<RequestOptions>? configure = null)
        => PostFormAsync(form.Action, form.Fields, configure);

    public Task<HttpExchange> PostJsonAsync(string target, object? body, Action<RequestOptions>? configure = null)
        => SendAsync(HttpMethod.Post, target, JsonBody(body), configure);

    public Task<HttpExchange> PutJsonAsync(string target, object? body, Action<RequestOptions>? configure = null)
        => SendAsync(HttpMethod.Put, target, JsonBody(body), configure);

    public Task<HttpExchange> PatchJsonAsync(string target, object? body, Action<RequestOptions>? configure = null)
        => SendAsync(HttpMethod.Patch, target, JsonBody(body), configure);

    public async Task<HttpExchange> SendAsync(HttpMethod method, string target, HttpContent? content, Action<RequestOptions>? configure = null)
    {
        var options = new RequestOptions();
        configure?.Invoke(options);
        var number = _transcript.NextExchangeNumber();
        var uri = new Uri(new Uri(BehaviorLockConstants.PublicOrigin), target);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        var shownHeaders = new List<KeyValuePair<string, string>>();

        if (_authenticate != null && !options.OmitCredentials)
        {
            await _authenticate(request);
        }

        if (IsBrowser && options.Origin is null && !options.OmitOrigin && IsUnsafe(method))
        {
            request.Headers.TryAddWithoutValidation("Origin", BehaviorLockConstants.PublicOrigin);
        }

        if (options.Origin is { } origin)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (options.BearerToken is { } bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        foreach (var (name, value) in options.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (Cookies != null && options.SendCookies && Cookies.GetCookieHeader(uri) is { Length: > 0 } cookieHeader)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        foreach (var cookie in options.ExtraCookies)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        foreach (var header in request.Headers)
        {
            foreach (var value in header.Value)
            {
                shownHeaders.Add(new KeyValuePair<string, string>(header.Key, value));
            }
        }

        string? requestBody = null;
        string? requestContentType = null;
        if (content != null)
        {
            requestBody = await content.ReadAsStringAsync();
            requestContentType = content.Headers.ContentType?.ToString();
        }

        // Internal: lets the host attribute effects and route hits to this exchange. Not rendered.
        request.Headers.TryAddWithoutValidation(EffectLog.ExchangeHeader, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (options.ClientAddress is { } address)
        {
            request.Headers.TryAddWithoutValidation(BehaviorLockHost.ClientAddressHeader, address);
        }

        var started = DateTimeOffset.UtcNow;
        using var response = await _transcript.Client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        var completed = DateTimeOffset.UtcNow;

        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.ToList() : [];
        if (Cookies != null)
        {
            foreach (var setCookie in setCookies)
            {
                Cookies.SetCookies(uri, setCookie);
            }
        }

        var responseHeaders = response.Headers
            .Where(header => !header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .Concat(response.Content.Headers)
            .SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value)))
            .ToList();

        var exchange = new HttpExchange(
            number,
            Name,
            method.Method,
            uri.PathAndQuery,
            shownHeaders,
            requestContentType,
            requestBody,
            response.StatusCode,
            response.ReasonPhrase,
            responseHeaders,
            setCookies,
            response.Content.Headers.ContentType?.ToString(),
            responseBody,
            started,
            completed);
        _transcript.Captured(exchange);
        return exchange;
    }

    private static bool IsUnsafe(HttpMethod method)
        => method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;

    private static StringContent JsonBody(object? body)
        => new(body is string text ? text : JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json");

    private static IEnumerable<KeyValuePair<string, string>> ToFields(object fields)
        => fields switch
        {
            IEnumerable<KeyValuePair<string, string>> pairs => pairs,
            _ => fields.GetType().GetProperties()
                .Select(property => new KeyValuePair<string, string>(property.Name, property.GetValue(fields)?.ToString() ?? string.Empty))
        };
}

/// <summary>Per-request adjustments for adversarial or protocol-specific requests.</summary>
public sealed class RequestOptions
{
    internal List<KeyValuePair<string, string>> Headers { get; } = [];

    internal List<string> ExtraCookies { get; } = [];

    internal string? Origin { get; private set; }

    internal bool OmitOrigin { get; private set; }

    internal bool OmitCredentials { get; private set; }

    internal bool SendCookies { get; private set; } = true;

    internal string? BearerToken { get; private set; }

    internal string? ClientAddress { get; private set; }

    public RequestOptions Header(string name, string value)
    {
        Headers.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    public RequestOptions Bearer(string token)
    {
        BearerToken = token;
        return this;
    }

    /// <summary>Sends this <c>Origin</c> instead of the browser's own origin.</summary>
    public RequestOptions WithOrigin(string origin)
    {
        Origin = origin;
        return this;
    }

    /// <summary>Sends no <c>Origin</c> header on an unsafe browser request.</summary>
    public RequestOptions WithoutOrigin()
    {
        OmitOrigin = true;
        return this;
    }

    /// <summary>Sends the operator request without its admin credential.</summary>
    public RequestOptions WithoutCredentials()
    {
        OmitCredentials = true;
        return this;
    }

    /// <summary>Does not attach the actor's cookie jar.</summary>
    public RequestOptions WithoutCookies()
    {
        SendCookies = false;
        return this;
    }

    /// <summary>Adds a raw <c>name=value</c> cookie pair, for replaying a captured cookie.</summary>
    public RequestOptions Cookie(string nameValue)
    {
        ExtraCookies.Add(nameValue);
        return this;
    }

    /// <summary>Presents a different client IP address (per-IP throttling cases).</summary>
    public RequestOptions FromAddress(string address)
    {
        ClientAddress = address;
        return this;
    }
}
