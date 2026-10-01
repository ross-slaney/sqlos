using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;
using SqlOS.BehaviorLock.Host.Diagnostics;
using SqlOS.BehaviorLock.Host.Fakes;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// One request and its response, captured completely so the transcript can render it later,
/// plus the effects and route hits the host attributed to it. Scenarios read values from an
/// exchange (a redirect's <c>code</c>, a hidden form field, a JSON property) to continue the
/// journey; the transcript records it only once passed to <c>Transcript.Observe</c>.
/// </summary>
public sealed partial class HttpExchange
{
    private readonly Lazy<JsonNode?> _json;

    internal HttpExchange(
        int number,
        string actor,
        string method,
        string target,
        IReadOnlyList<KeyValuePair<string, string>> requestHeaders,
        string? requestContentType,
        string? requestBody,
        HttpStatusCode status,
        string? reasonPhrase,
        IReadOnlyList<KeyValuePair<string, string>> responseHeaders,
        IReadOnlyList<string> setCookies,
        string? responseContentType,
        string responseBody,
        DateTimeOffset started,
        DateTimeOffset completed)
    {
        Number = number;
        Actor = actor;
        Method = method;
        Target = target;
        RequestHeaders = requestHeaders;
        RequestContentType = requestContentType;
        RequestBody = requestBody;
        Status = status;
        ReasonPhrase = reasonPhrase;
        ResponseHeaders = responseHeaders;
        SetCookies = setCookies;
        ResponseContentType = responseContentType;
        ResponseBody = responseBody;
        Started = started;
        Completed = completed;
        _json = new Lazy<JsonNode?>(ParseJson, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The internal exchange number stamped on the request, used to attribute effects and route hits.</summary>
    public int Number { get; }

    /// <summary>Who sent it: <c>browser</c>, <c>operator</c>, <c>client</c>, or a scenario-chosen label.</summary>
    public string Actor { get; }

    public string Method { get; }

    /// <summary>The path and query as sent.</summary>
    public string Target { get; }

    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders { get; }

    public string? RequestContentType { get; }

    public string? RequestBody { get; }

    public HttpStatusCode Status { get; }

    public int StatusCode => (int)Status;

    public string? ReasonPhrase { get; }

    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; }

    public IReadOnlyList<string> SetCookies { get; }

    public string? ResponseContentType { get; }

    public string ResponseBody { get; }

    public DateTimeOffset Started { get; }

    public DateTimeOffset Completed { get; }

    public IReadOnlyList<OutboundEffect> Effects { get; internal set; } = [];

    public IReadOnlyList<RouteHit> RouteHits { get; internal set; } = [];

    /// <summary>The response parsed as JSON, or null when the body is not JSON.</summary>
    public JsonNode? Json => _json.Value;

    /// <summary>Reads a string property from the JSON response, failing clearly when it is missing.</summary>
    public string JsonString(string path)
    {
        JsonNode? node = Json ?? throw new InvalidOperationException($"Exchange {Describe()} did not return JSON: {Preview()}");
        foreach (var segment in path.Split('.'))
        {
            node = node is JsonArray array && int.TryParse(segment, out var index)
                ? array[index]
                : node?[segment];
        }

        return node?.GetValue<string>()
            ?? throw new InvalidOperationException($"Exchange {Describe()} has no JSON value at '{path}': {Preview()}");
    }

    public string? Header(string name)
        => ResponseHeaders.FirstOrDefault(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public string? Location => Header("Location");

    /// <summary>
    /// Where a browser goes next: the <c>Location</c> of a redirect, or the target of the
    /// same-origin meta-refresh interstitial SqlOS answers hosted form posts with.
    /// </summary>
    public string? NextUrl
    {
        get
        {
            if (Location is { } location)
            {
                return location;
            }

            var match = MetaRefresh().Match(ResponseBody);
            return match.Success ? WebUtility.HtmlDecode(match.Groups["url"].Value) : null;
        }
    }

    /// <summary>The decoded query (and fragment) parameters of <see cref="NextUrl"/>.</summary>
    public IReadOnlyDictionary<string, string> NextUrlParameters
    {
        get
        {
            var url = NextUrl ?? throw new InvalidOperationException($"Exchange {Describe()} has no redirect: {Preview()}");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var query = url.IndexOf('?', StringComparison.Ordinal);
            var fragment = url.IndexOf('#', StringComparison.Ordinal);
            if (query >= 0)
            {
                var end = fragment > query ? fragment : url.Length;
                foreach (var (key, value) in QueryHelpers.ParseQuery(url[query..end]))
                {
                    result[key] = value.ToString();
                }
            }

            if (fragment >= 0)
            {
                foreach (var (key, value) in QueryHelpers.ParseQuery(url[(fragment + 1)..]))
                {
                    result[key] = value.ToString();
                }
            }

            return result;
        }
    }

    /// <summary>Reads one parameter from <see cref="NextUrl"/>, failing clearly when it is missing.</summary>
    public string NextUrlParameter(string name)
        => NextUrlParameters.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : throw new InvalidOperationException($"Exchange {Describe()} redirect has no '{name}': {NextUrl}");

    /// <summary>The value this response set for a cookie, or null.</summary>
    public string? SetCookieValue(string name)
    {
        foreach (var header in SetCookies)
        {
            var nameValue = header.Split(';', 2)[0];
            var separator = nameValue.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && nameValue[..separator].Trim().Equals(name, StringComparison.Ordinal))
            {
                return nameValue[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// The HTML form whose <c>action</c> contains <paramref name="actionContains"/>, with every
    /// field's current value, ready to be completed and posted like a browser would.
    /// </summary>
    public HtmlForm Form(string actionContains)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(ResponseBody);
        var forms = document.Forms.ToList();
        var form = forms.FirstOrDefault(candidate => (candidate.GetAttribute("action") ?? string.Empty)
                .Contains(actionContains, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Exchange {Describe()} has no form posting to '{actionContains}'. Forms: " +
                string.Join(", ", forms.Select(candidate => candidate.GetAttribute("action"))));
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var element in form.Elements)
        {
            var name = element.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var type = element.GetAttribute("type")?.ToLowerInvariant();
            if (type is "submit" or "button" or "image" or "reset")
            {
                continue;
            }

            if (type is "checkbox" or "radio" && !element.HasAttribute("checked"))
            {
                continue;
            }

            var value = element switch
            {
                IHtmlSelectElement select => select.Value ?? string.Empty,
                IHtmlTextAreaElement textArea => textArea.Value ?? string.Empty,
                _ => element.GetAttribute("value") ?? (type is "checkbox" or "radio" ? "on" : string.Empty)
            };
            fields.Add(new KeyValuePair<string, string>(name, value));
        }

        return new HtmlForm(WebUtility.HtmlDecode(form.GetAttribute("action") ?? string.Empty), fields);
    }

    private JsonNode? ParseJson()
    {
        try
        {
            return string.IsNullOrWhiteSpace(ResponseBody) ? null : JsonNode.Parse(ResponseBody);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A one-line description used in failure messages.</summary>
    public string Describe() => $"#{Number} {Method} {Target} -> {StatusCode}";

    internal string Preview() => ResponseBody.Length <= 600 ? ResponseBody : ResponseBody[..600] + "…";

    [GeneratedRegex("""http-equiv="refresh" content="0;\s*url=(?<url>[^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefresh();
}

/// <summary>A parsed HTML form: its action and fields, in document order.</summary>
public sealed class HtmlForm
{
    private readonly List<KeyValuePair<string, string>> _fields;

    public HtmlForm(string action, IEnumerable<KeyValuePair<string, string>> fields)
    {
        Action = action;
        _fields = fields.ToList();
    }

    public string Action { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Fields => _fields;

    public string this[string name]
        => _fields.FirstOrDefault(field => field.Key == name).Value
           ?? throw new KeyNotFoundException($"Form posting to {Action} has no field '{name}'.");

    /// <summary>Returns a copy with <paramref name="name"/> set, replacing an existing field or appending one.</summary>
    public HtmlForm With(string name, string value)
    {
        var fields = _fields.ToList();
        var index = fields.FindIndex(field => field.Key == name);
        if (index >= 0)
        {
            fields[index] = new KeyValuePair<string, string>(name, value);
        }
        else
        {
            fields.Add(new KeyValuePair<string, string>(name, value));
        }

        return new HtmlForm(Action, fields);
    }

    /// <summary>Returns a copy without <paramref name="name"/>, for adversarial posts.</summary>
    public HtmlForm Without(string name)
        => new(Action, _fields.Where(field => field.Key != name));
}
