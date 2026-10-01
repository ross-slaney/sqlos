using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// Makes a check-then-write race deterministic, so a scenario can lock how SqlOS behaves under it.
/// Once armed, every request to <c>path</c> is held at its first <c>statement</c> against
/// <c>table</c> until <c>participants</c> requests are waiting there (or ten seconds pass): each of
/// them has made its decision on the same data before any of them writes. That is exactly the
/// interleaving #424 describes for email-code attempts and send limits; without the barrier the
/// outcome would depend on thread timing. A fixed implementation that reserves before it reads
/// simply meets the barrier at its reservation instead, and the transcript shows the fixed result.
/// </summary>
internal sealed class HostedRaceBarrier : DbCommandInterceptor
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(10);
    private const string HeldItemKey = "SqlOS.BehaviorLock.RaceBarrier.Held";

    private readonly string _path;
    private readonly Regex _statement;
    private readonly int _participants;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IHttpContextAccessor? _httpContextAccessor;
    private int _arrived;
    private volatile bool _armed;

    /// <param name="path">The request path whose requests are held, for example <c>/sqlos/auth/login/email-otp/verify</c>.</param>
    /// <param name="statement"><c>UPDATE</c> or <c>INSERT INTO</c>.</param>
    /// <param name="table">The SqlOS table, for example <c>SqlOSEmailOtpChallenges</c>.</param>
    /// <param name="participants">How many requests must be waiting before all of them proceed.</param>
    public HostedRaceBarrier(string path, string statement, string table, int participants)
    {
        _path = path;
        // SQL Server writes [dbo].[Table]; PostgreSQL writes dbo."Table" (or "dbo"."Table").
        _statement = new Regex(
            $@"\b{Regex.Escape(statement).Replace(@"\ ", @"\s+", StringComparison.Ordinal)}\s+(?:[\[""]?dbo[\]""]?\.)?[\[""]?{Regex.Escape(table)}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        _participants = participants;
    }

    /// <summary>
    /// Posts <paramref name="forms"/> all at once from the transcript's browser: the first through
    /// the browser actor (so the scenario can observe it), the rest as other tabs of the same
    /// browser (same cookies and origin) through a plain client, whose status codes are returned.
    /// The transcript records exchanges one at a time, so only one of the concurrent requests goes
    /// through it.
    /// </summary>
    public static async Task<(HttpExchange Shown, IReadOnlyList<int> OtherStatuses)> PostTogetherAsync(
        Transcript t,
        IReadOnlyList<HtmlForm> forms)
    {
        using var tabs = t.Host.CreateClient();
        // Read the browser's cookies before any request runs; the shown request may update them.
        var cookies = forms
            .Select(form => t.Browser.Cookies!.GetCookieHeader(new Uri(new Uri(BehaviorLockConstants.PublicOrigin), form.Action)))
            .ToList();
        var others = forms.Skip(1).Select((form, index) => PostFromAnotherTabAsync(tabs, form, cookies[index + 1])).ToList();
        var shown = t.SubmitAsync(forms[0]);
        await Task.WhenAll(others.Cast<Task>().Append(shown));
        return (await shown, others.Select(task => task.Result).ToList());
    }

    private static async Task<int> PostFromAnotherTabAsync(HttpClient tabs, HtmlForm form, string cookies)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, form.Action) { Content = new FormUrlEncodedContent(form.Fields) };
        request.Headers.TryAddWithoutValidation("Origin", BehaviorLockConstants.PublicOrigin);
        request.Headers.TryAddWithoutValidation("Cookie", cookies);
        using var response = await tabs.SendAsync(request);
        return (int)response.StatusCode;
    }

    /// <summary>Adds the barrier to the host's SqlOS <see cref="DbContext"/> (a scenario option).</summary>
    public void Install(ScenarioOptions options)
    {
        var previous = options.ConfigureServices;
        options.ConfigureServices = services =>
        {
            previous?.Invoke(services);
            services.ConfigureDbContext<BehaviorLockDbContext>((provider, db) =>
            {
                _httpContextAccessor ??= provider.GetRequiredService<IHttpContextAccessor>();
                db.AddInterceptors(this);
            });
        };
    }

    /// <summary>Starts holding requests. Preconditions that run before this are never held.</summary>
    public void Arm() => _armed = true;

    /// <summary>
    /// How many requests reached the barrier. Scenarios record it, so a barrier that stopped
    /// matching (a renamed table, another SQL dialect) shows up as an approval diff, not as a
    /// race that is suddenly left to timing.
    /// </summary>
    public int Held => Volatile.Read(ref _arrived);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await HoldAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await HoldAsync(command, cancellationToken);
        return result;
    }

    private async Task HoldAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var http = _httpContextAccessor?.HttpContext;
        if (!_armed
            || http == null
            || !string.Equals(http.Request.Path.Value, _path, StringComparison.Ordinal)
            || !_statement.IsMatch(command.CommandText)
            || !http.Items.TryAdd(HeldItemKey, true))
        {
            return;
        }

        if (Interlocked.Increment(ref _arrived) >= _participants)
        {
            _released.TrySetResult();
        }

        await Task.WhenAny(_released.Task, Task.Delay(MaximumWait, cancellationToken));
    }
}
