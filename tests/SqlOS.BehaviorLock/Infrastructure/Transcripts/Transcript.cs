using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Fakes;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Hosting;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.Configuration;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// A scenario's record of external behavior. Start one per scenario, drive the journey through
/// its actors, observe what matters, and approve it:
/// <code>
/// await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
/// var alice = await t.Setup.CreateUserAsync("alice");
/// var page = t.Observe(await t.GetAsync(t.Urls.Authorize()), "open the sign-in page");
/// ...
/// await t.ObserveAuditAsync();
/// await t.ApproveAsync();
/// </code>
/// Every exchange must be observed (<see cref="Observe"/>) or explicitly discarded
/// (<see cref="Discard"/>); outbound effects are attributed to the exchange that caused them;
/// <see cref="ApproveAsync"/> scrubs the rendered transcript and compares it with the approved
/// file next to the scenario.
/// </summary>
public sealed class Transcript : IAsyncDisposable
{
    private readonly List<TranscriptEntry> _entries = [];
    private readonly List<HttpExchange> _pending = [];
    private readonly HashSet<string> _seenAuditEvents = new(StringComparer.Ordinal);
    private readonly ScenarioOptions _options;
    private int _exchangeNumber;
    private long _effectsWatermark;

    private Transcript(ScenarioContext scenario, ScenarioHost host, ScenarioOptions options)
    {
        Scenario = scenario;
        Host = host;
        _options = options;
        Client = host.CreateClient();
        Scrubber = new Scrubber();
        Browser = new HttpActor(this, "browser", isBrowser: true);
        Api = new HttpActor(this, "client", isBrowser: false);
        Operator = new HttpActor(this, "operator", isBrowser: false, AuthenticateOperatorAsync);
        Setup = new ScenarioSetup(this);
        Unique = new UniqueValues(this);
        Urls = new ScenarioUrls(this);
        Scrubber.RegisterNamed(BehaviorLockConstants.DashboardPassword, "password", "dashboard");
    }

    public ScenarioContext Scenario { get; }

    public ScenarioHost Host { get; }

    public HostProfile Profile => Host.Profile;

    public BehaviorLockFakes Fakes => Host.Fakes;

    /// <summary>The default browser. <see cref="GetAsync"/> and friends send through it.</summary>
    public HttpActor Browser { get; }

    /// <summary>A plain API client: no cookies, no <c>Origin</c> (token endpoint, SCIM, resource APIs).</summary>
    public HttpActor Api { get; }

    /// <summary>The SqlOS operator, authenticated the way the profile admits operators.</summary>
    public HttpActor Operator { get; }

    /// <summary>Unrecorded preconditions: users, organizations, clients, connections.</summary>
    public ScenarioSetup Setup { get; }

    /// <summary>Per-run unique values (emails, slugs) that scrub to stable named placeholders.</summary>
    public UniqueValues Unique { get; }

    /// <summary>Builders for the protocol URLs scenarios use most.</summary>
    public ScenarioUrls Urls { get; }

    internal HttpClient Client { get; }

    internal Scrubber Scrubber { get; }

    /// <summary>
    /// Starts a fresh host for <paramref name="profile"/> on its own database. Must run inside a
    /// <see cref="ScenarioAttribute"/> test method.
    /// </summary>
    public static async Task<Transcript> StartAsync(string profile, Action<ScenarioOptions>? configure = null)
    {
        var scenario = ScenarioContext.Current
            ?? throw new InvalidOperationException("Transcript.StartAsync must run inside a [Scenario] test method.");
        var options = new ScenarioOptions();
        configure?.Invoke(options);
        var host = await ScenarioHost.StartAsync(
            profile,
            options.ConfigureSqlOS,
            options.ConfigureServices,
            options.ExistingDatabase,
            options.DataProtectionKeysDirectory);
        var transcript = new Transcript(scenario, host, options);
        try
        {
            await transcript.InitializeAsync();
            return transcript;
        }
        catch
        {
            await transcript.DisposeAsync();
            throw;
        }
    }

    public Task<HttpExchange> GetAsync(string target, Action<RequestOptions>? configure = null)
        => Browser.GetAsync(target, configure);

    public Task<HttpExchange> PostFormAsync(string target, IEnumerable<KeyValuePair<string, string>> fields, Action<RequestOptions>? configure = null)
        => Browser.PostFormAsync(target, fields, configure);

    public Task<HttpExchange> PostFormAsync(string target, object fields, Action<RequestOptions>? configure = null)
        => Browser.PostFormAsync(target, fields, configure);

    public Task<HttpExchange> SubmitAsync(HtmlForm form, Action<RequestOptions>? configure = null)
        => Browser.SubmitAsync(form, configure);

    public Task<HttpExchange> PostJsonAsync(string target, object? body, Action<RequestOptions>? configure = null)
        => Browser.PostJsonAsync(target, body, configure);

    /// <summary>A second, independent browser (another device or tab without shared cookies).</summary>
    public HttpActor NewBrowser(string name) => new(this, name, isBrowser: true);

    /// <summary>An API client with its own label in the transcript, for example <c>scim</c> or <c>mcp-client</c>.</summary>
    public HttpActor NewClient(string name) => new(this, name, isBrowser: false);

    /// <summary>Records <paramref name="exchange"/> in the transcript, with the effects it caused.</summary>
    public HttpExchange Observe(HttpExchange exchange, string? caption = null)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        if (!_pending.Remove(exchange))
        {
            throw new InvalidOperationException($"Exchange {exchange.Describe()} was already observed or discarded.");
        }

        _entries.Add(TranscriptEntry.ForExchange(exchange, caption));
        return exchange;
    }

    /// <summary>Marks an exchange as deliberately unrecorded (a precondition, not part of the locked behavior).</summary>
    public HttpExchange Discard(HttpExchange exchange)
    {
        _pending.Remove(exchange);
        return exchange;
    }

    /// <summary>Adds a free-form line to the transcript, for context a reviewer needs.</summary>
    public void Note(string text) => _entries.Add(TranscriptEntry.ForNote(text));

    /// <summary>
    /// Records a document the journey decoded, such as the SAML AuthnRequest SqlOS sent to an
    /// identity provider. XML is pretty-printed; everything is scrubbed like the rest of the transcript.
    /// </summary>
    public void ObserveDocument(string caption, string content)
        => _entries.Add(TranscriptEntry.ForDocument(caption, DocumentText.Normalize(content)));

    /// <summary>Records the audit events written since the last audit observation, oldest first.</summary>
    public async Task ObserveAuditAsync(string? caption = null)
    {
        var events = await ReadNewAuditEventsAsync();
        _entries.Add(TranscriptEntry.ForAudit(caption, events));
    }

    /// <summary>Reads and forgets the audit events written so far (setup noise).</summary>
    public async Task SkipAuditAsync()
    {
        if (OperatorCanReadAudit)
        {
            await ReadNewAuditEventsAsync();
        }
    }

    /// <summary>Reads end state through an admin or dashboard read API and records it.</summary>
    public async Task<HttpExchange> ObserveStateAsync(string target, string? caption = null)
        => Observe(await Operator.GetAsync(target), caption ?? "state");

    /// <summary>Records outbound effects that no exchange caused (for example work triggered out of band).</summary>
    public void ObserveUnattributedEffects(string caption)
    {
        var effects = Fakes.Effects.Since(_effectsWatermark).Where(effect => effect.Exchange == null).ToList();
        _effectsWatermark = Fakes.Effects.Sequence;
        _entries.Add(TranscriptEntry.ForEffects(caption, effects));
    }

    /// <summary>Registers a value the scrubber cannot infer (for example a code shown only in page text).</summary>
    public void Scrub(string value, string kind) => Scrubber.Register(value, kind);

    /// <summary>Registers a value under a fixed, readable placeholder: <c>{kind:name}</c>.</summary>
    public void Scrub(string value, string kind, string name) => Scrubber.RegisterNamed(value, kind, name);

    /// <summary>Emails the fakes captured so far, oldest first.</summary>
    public IReadOnlyList<EmailEffect> Emails => Fakes.Effects.All().OfType<EmailEffect>().ToList();

    /// <summary>The most recent email to <paramref name="to"/>.</summary>
    public EmailEffect LatestEmailTo(string to)
        => Emails.LastOrDefault(email => string.Equals(email.To, to, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"No email was sent to {to}. Sent: {string.Join(", ", Emails.Select(email => email.To))}");

    /// <summary>The most recent SMS code sent to <paramref name="phoneNumber"/>.</summary>
    public string LatestSmsCodeTo(string phoneNumber)
        => Fakes.Effects.All().OfType<SmsEffect>().LastOrDefault(sms => sms.Operation == "send" && sms.To == phoneNumber)?.Code
           ?? throw new InvalidOperationException($"No SMS was sent to {phoneNumber}.");

    /// <summary>
    /// Renders, scrubs, and approves the transcript. Fails first when an exchange was neither
    /// observed nor discarded, or when a <c>[Covers]</c> route was not hit by an observed exchange.
    /// </summary>
    public async Task ApproveAsync()
    {
        Scenario.MarkApproved();
        if (_pending.Count > 0)
        {
            throw new InvalidOperationException(
                "Every exchange must be observed or discarded. Not recorded: " +
                string.Join("; ", _pending.Select(exchange => exchange.Describe())) +
                ". Pass it to t.Observe(...) or, for a precondition, t.Discard(...).");
        }

        CoverageVerification.Verify(Scenario, _entries);
        var text = TranscriptRenderer.Render(Scenario, Profile.Name, _entries, Scrubber);
        await Approvals.VerifyTranscriptAsync(Scenario, text);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.DisposeAsync();
    }

    internal int NextExchangeNumber() => Interlocked.Increment(ref _exchangeNumber);

    internal void Captured(HttpExchange exchange)
    {
        exchange.Effects = Fakes.Effects.All().Where(effect => effect.Exchange == exchange.Number).ToList();
        exchange.RouteHits = Host.Routes.ForExchange(exchange.Number);
        foreach (var hit in exchange.RouteHits)
        {
            // The host's request identifier appears in audit contexts; give it a stable name.
            Scrubber.Register(hit.TraceIdentifier, "trace");
        }

        _pending.Add(exchange);
    }

    internal bool OperatorCanReadAudit => Profile.OperatorAccess != OperatorAccess.None && AuditApiIsMapped;

    /// <summary>
    /// Whether the host maps the admin audit API. A host that also calls <c>MapAuthServer()</c>
    /// itself (the legacy-host profile) does not: SqlOS then withdraws its whole core route set,
    /// including the audit, email, and calendar admin APIs that <c>MapAuthServer()</c> does not map.
    /// </summary>
    private bool AuditApiIsMapped => Host.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
        .OfType<RouteEndpoint>()
        .Any(endpoint => string.Equals(endpoint.RoutePattern.RawText, "/sqlos/admin/audit/api/events", StringComparison.Ordinal));

    private async Task InitializeAsync()
    {
        if (Profile.OperatorAccess == OperatorAccess.Password)
        {
            await SignInOperatorAsync();
        }

        // Startup reconciliation wrote audit events before the scenario began.
        await SkipAuditAsync();
    }

    private async Task SignInOperatorAsync()
    {
        var login = await Operator.PostJsonAsync(
            "/sqlos/dashboard-auth/login",
            new { password = BehaviorLockConstants.DashboardPassword },
            options => options.WithoutCredentials());
        Discard(login);
        if (login.StatusCode != 200)
        {
            throw new InvalidOperationException($"Operator sign-in failed: {login.Describe()} {login.Preview()}");
        }
    }

    private Task AuthenticateOperatorAsync(HttpRequestMessage request)
    {
        switch (Profile.OperatorAccess)
        {
            case OperatorAccess.AuthorizationCallback:
                request.Headers.TryAddWithoutValidation(BehaviorLockConstants.OperatorHeader, BehaviorLockConstants.OperatorSecret);
                break;
            case OperatorAccess.Password when request.Method != HttpMethod.Get && request.Method != HttpMethod.Head:
                // Password-mode mutations presenting the session cookie need a same-origin proof.
                request.Headers.TryAddWithoutValidation("X-SqlOS-Request", "1");
                request.Headers.TryAddWithoutValidation("Origin", BehaviorLockConstants.PublicOrigin);
                break;
        }

        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<JsonNode>> ReadNewAuditEventsAsync()
    {
        if (!OperatorCanReadAudit)
        {
            throw new InvalidOperationException(
                $"Profile '{Profile.Name}' has no operator access or does not map the admin audit API, so audit events cannot be read through it.");
        }

        var fresh = new List<JsonNode>();
        string? cursor = null;
        do
        {
            var target = "/sqlos/admin/audit/api/events?pageSize=200" + (cursor == null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = Discard(await Operator.GetAsync(target));
            if (page.StatusCode != 200 || page.Json?["data"] is not JsonArray data)
            {
                throw new InvalidOperationException($"Reading audit events failed: {page.Describe()} {page.Preview()}");
            }

            foreach (var item in data)
            {
                var id = item?["id"]?.GetValue<string>();
                if (item != null && id != null && _seenAuditEvents.Add(id))
                {
                    fresh.Add(item.DeepClone());
                }
            }

            cursor = page.Json?["hasNextPage"]?.GetValue<bool>() == true ? page.Json?["nextCursor"]?.GetValue<string>() : null;
        }
        while (cursor != null);

        return AuditOrdering.Chronological(fresh);
    }
}

/// <summary>Scenario-specific host adjustments. Keep them rare: prefer a profile.</summary>
public sealed class ScenarioOptions
{
    public Action<SqlOSOptions>? ConfigureSqlOS { get; set; }

    public Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>Host an existing database (the upgrade gate) instead of a fresh one; it is not dropped.</summary>
    public string? ExistingDatabase { get; set; }

    public string? DataProtectionKeysDirectory { get; set; }
}

/// <summary>One section of a transcript.</summary>
internal sealed record TranscriptEntry(
    string Kind,
    string? Caption,
    HttpExchange? Exchange,
    IReadOnlyList<JsonNode>? AuditEvents,
    IReadOnlyList<OutboundEffect>? Effects,
    string? Note)
{
    public static TranscriptEntry ForExchange(HttpExchange exchange, string? caption) => new("exchange", caption, exchange, null, null, null);

    public static TranscriptEntry ForAudit(string? caption, IReadOnlyList<JsonNode> events) => new("audit", caption, null, events, null, null);

    public static TranscriptEntry ForEffects(string caption, IReadOnlyList<OutboundEffect> effects) => new("effects", caption, null, null, effects, null);

    public static TranscriptEntry ForNote(string note) => new("note", null, null, null, null, note);

    public static TranscriptEntry ForDocument(string caption, string content) => new("document", caption, null, null, null, content);
}

/// <summary>
/// The audit API returns newest first and breaks timestamp ties by random event ID. The transcript
/// shows events oldest first; events that share a timestamp are ordered by action and content
/// (with generated IDs masked), which is stable across runs.
/// </summary>
internal static class AuditOrdering
{
    public static IReadOnlyList<JsonNode> Chronological(IEnumerable<JsonNode> events)
        => events
            .OrderBy(item => Timestamp(item["occurredAt"]))
            .ThenBy(item => Timestamp(item["ingestedAt"]))
            .ThenBy(item => item["action"]?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => MaskGeneratedIds(item.ToJsonString()), StringComparer.Ordinal)
            .ToList();

    private static DateTime Timestamp(JsonNode? node)
        => node?.GetValue<string>() is { } value
           && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.MinValue;

    private static string MaskGeneratedIds(string json)
        => System.Text.RegularExpressions.Regex.Replace(json, "[a-z][a-z0-9]{0,11}_[0-9a-f]{16,32}", "id");
}

/// <summary>Formats numbers without culture surprises.</summary>
internal static class Invariant
{
    public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
