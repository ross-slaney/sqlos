using Microsoft.AspNetCore.Http;

namespace SqlOS.BehaviorLock.Host.Fakes;

/// <summary>
/// Ordered, thread-safe log of every outbound side effect a host produces through the fakes:
/// emails, SMS, outbound HTTP, and DNS lookups. Each effect carries a monotonically increasing
/// sequence number and, when it happened while serving a behavior-lock request, the exchange
/// number the harness stamped on that request, so the transcript can attribute effects to the
/// exchange that caused them even when requests overlap.
/// </summary>
public sealed class EffectLog
{
    /// <summary>Request header the behavior-lock harness uses to number its exchanges.</summary>
    public const string ExchangeHeader = "X-BehaviorLock-Exchange";

    private readonly object _gate = new();
    private readonly List<OutboundEffect> _effects = [];
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private long _sequence;

    public EffectLog(IHttpContextAccessor? httpContextAccessor = null)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>The sequence number of the most recent effect, or zero before the first one.</summary>
    public long Sequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    public void Record(OutboundEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        var exchange = ReadExchangeNumber();
        lock (_gate)
        {
            _sequence++;
            _effects.Add(effect with { Sequence = _sequence, Exchange = exchange });
        }
    }

    /// <summary>Returns the effects recorded after <paramref name="sequence"/>, oldest first.</summary>
    public IReadOnlyList<OutboundEffect> Since(long sequence)
    {
        lock (_gate)
        {
            return _effects.Where(effect => effect.Sequence > sequence).ToList();
        }
    }

    public IReadOnlyList<OutboundEffect> All()
    {
        lock (_gate)
        {
            return _effects.ToList();
        }
    }

    private int? ReadExchangeNumber()
    {
        var header = _httpContextAccessor?.HttpContext?.Request.Headers[ExchangeHeader].ToString();
        return int.TryParse(header, out var exchange) ? exchange : null;
    }
}

/// <summary>A side effect that left the host through one of the behavior-lock fakes.</summary>
public abstract record OutboundEffect
{
    public long Sequence { get; init; }

    /// <summary>The harness exchange being served when the effect happened, if any.</summary>
    public int? Exchange { get; init; }
}

/// <summary>An email handed to <c>ISqlOSAuthEmailSender</c> (<c>auth</c>) or <c>ISqlOSEmailSender</c> (<c>transactional</c>).</summary>
public sealed record EmailEffect(
    string Channel,
    string To,
    string Subject,
    string? TextBody,
    string HtmlBody) : OutboundEffect;

/// <summary>A verification SMS the fake OTP delivery channel sent, or a code it checked.</summary>
public sealed record SmsEffect(
    string Operation,
    string To,
    string Purpose,
    string? Code,
    bool? Approved) : OutboundEffect;

/// <summary>An outbound HTTP call made through <c>IHttpClientFactory</c>.</summary>
public sealed record HttpEffect(
    string Client,
    string Method,
    string Url,
    string? RequestContentType,
    string? RequestBody,
    int StatusCode) : OutboundEffect;

/// <summary>A DNS TXT lookup made through <c>ISqlOSDomainDnsVerifier</c>.</summary>
public sealed record DnsEffect(
    string RecordName,
    string ExpectedValue,
    bool Found) : OutboundEffect;
