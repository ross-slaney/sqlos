using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;
using SqlOS.BehaviorLock.Host;

namespace SqlOS.BehaviorLock.UpgradeSeed;

/// <summary>
/// A small browser/API client for building the seed dataset over HTTP, in-process against the
/// host's TestServer. The seed drives the same public routes a real deployment serves; every
/// call must succeed or the seed fails with the response.
/// </summary>
internal sealed class SeedHttp(HttpMessageHandler handler, string name, bool isBrowser, bool isOperator = false)
{
    private readonly HttpClient _client = new(handler) { BaseAddress = new Uri(BehaviorLockConstants.PublicOrigin) };
    private readonly CookieContainer _cookies = new();

    public Task<SeedResponse> GetAsync(string target, params (string Name, string Value)[] headers)
        => SendAsync(HttpMethod.Get, target, null, headers);

    public Task<SeedResponse> PostFormAsync(string target, IEnumerable<KeyValuePair<string, string>> fields, params (string Name, string Value)[] headers)
        => SendAsync(HttpMethod.Post, target, new FormUrlEncodedContent(fields), headers);

    public Task<SeedResponse> PostJsonAsync(string target, object body, params (string Name, string Value)[] headers)
        => SendAsync(HttpMethod.Post, target, new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"), headers);

    public Task<SeedResponse> SubmitAsync(SeedForm form)
        => PostFormAsync(form.Action, form.Fields);

    /// <summary>The value of a cookie this browser holds for the public origin.</summary>
    public string CookieValue(string cookieName)
        => _cookies.GetCookies(new Uri(BehaviorLockConstants.PublicOrigin))[cookieName]?.Value
           ?? throw new InvalidOperationException($"{name} holds no {cookieName} cookie.");

    public async Task<SeedResponse> SendAsync(HttpMethod method, string target, HttpContent? content, params (string Name, string Value)[] headers)
    {
        var uri = new Uri(new Uri(BehaviorLockConstants.PublicOrigin), target);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (isOperator)
        {
            request.Headers.TryAddWithoutValidation(BehaviorLockConstants.OperatorHeader, BehaviorLockConstants.OperatorSecret);
        }

        if (isBrowser && method != HttpMethod.Get)
        {
            request.Headers.TryAddWithoutValidation("Origin", BehaviorLockConstants.PublicOrigin);
        }

        if (isBrowser && _cookies.GetCookieHeader(uri) is { Length: > 0 } cookie)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        foreach (var (headerName, value) in headers)
        {
            if (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(headerName, value);
            }
        }

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (isBrowser && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
            {
                _cookies.SetCookies(uri, setCookie);
            }
        }

        return new SeedResponse($"{name} {method} {target}", (int)response.StatusCode, body, response.Headers.Location?.ToString());
    }
}

internal sealed partial record SeedResponse(string Description, int Status, string Body, string? Location)
{
    public SeedResponse EnsureSuccess()
        => Status is >= 200 and < 400
            ? this
            : throw new InvalidOperationException($"{Description} returned {Status}: {Preview}");

    public string Preview => Body.Length <= 800 ? Body : Body[..800];

    public JsonNode Json => JsonNode.Parse(Body) ?? throw new InvalidOperationException($"{Description} did not return JSON: {Preview}");

    public string JsonString(string path)
    {
        JsonNode? node = Json;
        foreach (var segment in path.Split('.'))
        {
            node = node?[segment];
        }

        return node?.GetValue<string>() ?? throw new InvalidOperationException($"{Description} has no '{path}': {Preview}");
    }

    /// <summary>Whether the response sends the browser on: a Location header or the hosted meta-refresh interstitial.</summary>
    public bool IsRedirect => Location != null || MetaRefresh().IsMatch(Body);

    /// <summary>The redirect target: a Location header or the hosted meta-refresh interstitial.</summary>
    public string NextUrl
    {
        get
        {
            if (Location != null)
            {
                return Location;
            }

            var match = MetaRefresh().Match(Body);
            return match.Success
                ? WebUtility.HtmlDecode(match.Groups["url"].Value)
                : throw new InvalidOperationException($"{Description} did not redirect: {Preview}");
        }
    }

    public string NextParameter(string name)
    {
        var url = NextUrl;
        var query = url.Contains('?', StringComparison.Ordinal) ? url[url.IndexOf('?', StringComparison.Ordinal)..] : string.Empty;
        var value = QueryHelpers.ParseQuery(query)[name].ToString();
        return string.IsNullOrEmpty(value) ? throw new InvalidOperationException($"{Description} redirect has no '{name}': {url}") : value;
    }

    public bool HasForm(string actionContains) => Body.Contains(actionContains, StringComparison.Ordinal);

    public SeedForm Form(string actionContains)
    {
        using var document = new HtmlParser().ParseDocument(Body);
        var form = document.Forms.FirstOrDefault(candidate => (candidate.GetAttribute("action") ?? string.Empty).Contains(actionContains, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{Description} has no form posting to {actionContains}: {Preview}");
        var fields = form.Elements
            .Where(element => !string.IsNullOrEmpty(element.GetAttribute("name")) && element.GetAttribute("type") is not ("submit" or "button"))
            .Select(element => new KeyValuePair<string, string>(element.GetAttribute("name")!, element.GetAttribute("value") ?? string.Empty))
            .ToList();
        return new SeedForm(WebUtility.HtmlDecode(form.GetAttribute("action") ?? string.Empty), fields);
    }

    /// <summary>The text of the first <c>&lt;code&gt;</c> element whose content matches <paramref name="pattern"/>.</summary>
    public string Code(string pattern)
    {
        using var document = new HtmlParser().ParseDocument(Body);
        return document.QuerySelectorAll("code").Select(element => element.TextContent.Trim()).FirstOrDefault(text => Regex.IsMatch(text, pattern))
            ?? throw new InvalidOperationException($"{Description} shows no code element matching {pattern}: {Preview}");
    }

    [GeneratedRegex("""http-equiv="refresh" content="0;\s*url=(?<url>[^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefresh();
}

internal sealed record SeedForm(string Action, IReadOnlyList<KeyValuePair<string, string>> Fields)
{
    public SeedForm With(string name, string value)
    {
        var fields = Fields.Where(field => field.Key != name).ToList();
        fields.Add(new KeyValuePair<string, string>(name, value));
        return this with { Fields = fields };
    }
}
