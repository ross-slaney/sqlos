using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlOS.BehaviorLock.Host.Fakes;

namespace SqlOS.BehaviorLock.Host.Diagnostics;

/// <summary>
/// Records which route served each request, so the behavior-lock harness can prove a scenario
/// really exercised every <c>[Covers]</c> route it claims. Endpoint-routed requests record the
/// matched <see cref="RouteEndpoint"/> pattern; requests the dashboard middleware answers by
/// string matching (no endpoint) record the raw method and path, which the coverage gate maps
/// onto the dashboard route manifest.
/// </summary>
public sealed class RouteHitRecorder
{
    private readonly object _gate = new();
    private readonly List<RouteHit> _hits = [];

    public void Record(RouteHit hit)
    {
        lock (_gate)
        {
            _hits.Add(hit);
        }
    }

    public IReadOnlyList<RouteHit> ForExchange(int exchange)
    {
        lock (_gate)
        {
            return _hits.Where(hit => hit.Exchange == exchange).ToList();
        }
    }

    public IReadOnlyList<RouteHit> All()
    {
        lock (_gate)
        {
            return _hits.ToList();
        }
    }
}

/// <param name="Exchange">The harness exchange number, or null for requests the harness did not number.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The raw request path.</param>
/// <param name="RoutePattern">The matched endpoint's route pattern, or null when no endpoint served the request.</param>
/// <param name="StatusCode">The response status.</param>
/// <param name="TraceIdentifier">The server's request identifier, which audit contexts record.</param>
public sealed record RouteHit(int? Exchange, string Method, string Path, string? RoutePattern, int StatusCode, string TraceIdentifier);

/// <summary>
/// Outermost startup filter: registered before <c>AddSqlOS</c>, so it wraps SqlOS's own pipeline
/// (forwarded headers, dashboard middleware, endpoint routing) and sees every request.
/// </summary>
internal sealed class RouteHitRecorderStartupFilter(RouteHitRecorder recorder) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            try
            {
                await nextMiddleware(context);
            }
            finally
            {
                var exchange = int.TryParse(context.Request.Headers[EffectLog.ExchangeHeader].ToString(), out var number)
                    ? number
                    : (int?)null;
                var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
                recorder.Record(new RouteHit(
                    exchange,
                    context.Request.Method,
                    context.Request.Path.Value ?? string.Empty,
                    pattern,
                    context.Response.StatusCode,
                    context.TraceIdentifier));
            }
        });
        next(app);
    };
}
