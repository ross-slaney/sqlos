using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlOS.BehaviorLock.Host;

namespace SqlOS.Benchmarks.SignIn;

/// <summary>
/// Sends one sign-in's requests and times them. Only the requests are timed, from the moment one is
/// sent until its body has been read; parsing a page and reading an email between requests are the
/// client's work and are not. The commands sent to the database and the bytes allocated are read
/// around each request, outside the timed window. Requests run one at a time and nothing else
/// touches the run's database, so the commands belong to this sign-in. Allocation is read without
/// stopping the runtime (<c>GC.GetTotalAllocatedBytes(precise: false)</c>), which counts each
/// thread's allocation context when it is handed out, so a request's figure is approximate (by a
/// few such contexts) and is useful as a mean over many sign-ins.
/// </summary>
internal sealed class IterationRecorder
{
    private readonly HttpClient _client;
    private readonly SqlCommandMeter _meter;
    private readonly string _clientAddress;
    private readonly double[] _stepMilliseconds;
    private SqlCommandTally _database;
    private long _allocatedBytes;

    public IterationRecorder(HttpClient client, SqlCommandMeter meter, string clientAddress, int steps)
    {
        _client = client;
        _meter = meter;
        _clientAddress = clientAddress;
        _stepMilliseconds = new double[steps];
    }

    public async Task<BenchmarkResponse> SendAsync(int step, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            request.Headers.TryAddWithoutValidation(BehaviorLockHost.ClientAddressHeader, _clientAddress);
            var description = $"{request.Method} {request.RequestUri?.OriginalString.Split('?')[0]}";
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var databaseBefore = _meter.Read();

            var started = Stopwatch.GetTimestamp();
            using var response = await _client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started);

            _database += _meter.Read() - databaseBefore;
            _allocatedBytes += GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
            _stepMilliseconds[step] += elapsed.TotalMilliseconds;
            return new BenchmarkResponse(
                description,
                response.StatusCode,
                response.Headers.Location?.ToString(),
                response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.ToList() : [],
                body);
        }
    }

    public IterationSample Complete()
        => new(
            _stepMilliseconds.Sum(),
            _stepMilliseconds,
            _database.Commands,
            _database.Elapsed.TotalMilliseconds,
            _allocatedBytes);
}

/// <summary>One response, read to the end.</summary>
internal sealed partial record BenchmarkResponse(
    string Description,
    HttpStatusCode Status,
    string? Location,
    IReadOnlyList<string> SetCookies,
    string Body)
{
    /// <summary>Fails the run unless the response has one of <paramref name="expected"/>.</summary>
    public BenchmarkResponse Expect(params HttpStatusCode[] expected)
        => expected.Contains(Status)
            ? this
            : throw new InvalidOperationException($"{Description} answered {(int)Status}, expected {string.Join(" or ", expected.Select(code => (int)code))}: {Preview()}");

    /// <summary>A non-empty string at a JSON path of the body, or a failed run.</summary>
    public string JsonString(params string[] path)
    {
        var node = JsonNode.Parse(Body);
        foreach (var segment in path)
        {
            node = node?[segment];
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
            ? text
            : throw new InvalidOperationException($"{Description} returned no {string.Join('.', path)}: {Preview()}");
    }

    /// <summary>
    /// Where a browser goes next: the <c>Location</c> of a redirect, or the target of the
    /// same-origin meta-refresh page SqlOS answers hosted form posts with.
    /// </summary>
    public string NextUrl()
    {
        if (Location != null)
        {
            return Location;
        }

        var refresh = MetaRefresh().Match(Body);
        return refresh.Success
            ? WebUtility.HtmlDecode(refresh.Groups["url"].Value)
            : throw new InvalidOperationException($"{Description} did not redirect: {Preview()}");
    }

    /// <summary>The cookies this response set, as a browser's <c>Cookie</c> header would carry them back.</summary>
    public string CookieHeader()
        => string.Join("; ", SetCookies.Select(cookie => cookie.Split(';', 2)[0].Trim()));

    private string Preview() => Body.Length <= 600 ? Body : Body[..600] + "…";

    [GeneratedRegex("""http-equiv="refresh" content="0;\s*url=(?<url>[^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefresh();
}
