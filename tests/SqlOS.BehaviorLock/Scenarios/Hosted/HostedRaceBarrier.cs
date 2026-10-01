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
/// <c>table</c> until each of <c>participants</c> requests is either waiting there or finished
/// without reaching it (or ten seconds pass): each of them has made its decision on the same data
/// before any of them writes. That is exactly the interleaving #424 describes for email-code
/// attempts and send limits; without the barrier the outcome would depend on thread timing. A fixed
/// implementation that reserves before it reads simply meets the barrier at its reservation
/// instead, or refuses a request before its write, and the transcript shows the fixed result.
/// </summary>
internal sealed class HostedRaceBarrier : DbCommandInterceptor
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(10);
    private const string HeldItemKey = "SqlOS.BehaviorLock.RaceBarrier.Held";

    private readonly string _path;
    private readonly Regex _statement;
    private readonly int _participants;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstArrival = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IHttpContextAccessor? _httpContextAccessor;
    private int _arrived;
    private int _finished;
    private volatile bool _armed;

    /// <param name="path">The request path whose requests are held, for example <c>/sqlos/auth/login/email-otp/verify</c>.</param>
    /// <param name="statement"><c>UPDATE</c> or <c>INSERT INTO</c>.</param>
    /// <param name="table">The SqlOS table, for example <c>SqlOSEmailOtpChallenges</c>.</param>
    /// <param name="participants">How many requests must be waiting before all of them proceed.</param>
    public HostedRaceBarrier(string path, string statement, string table, int participants)
    {
        _path = path;
        // SQL Server writes [dbo].[Table]; PostgreSQL writes dbo."Table" (or "dbo"."Table").
        var qualifiedTable = $@"(?:[\[""]?dbo[\]""]?\.)?[\[""]?{Regex.Escape(table)}\b";
        var pattern = $@"\b{Regex.Escape(statement).Replace(@"\ ", @"\s+", StringComparison.Ordinal)}\s+{qualifiedTable}";
        if (string.Equals(statement, "UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            // A set-based update (ExecuteUpdate) on SQL Server names its alias first and the table
            // in its FROM clause: UPDATE [c] SET ... FROM [dbo].[Table] AS [c].
            pattern += $@"|\bUPDATE\s+\[\w+\]\s+SET\b[\s\S]*?\bFROM\s+{qualifiedTable}";
        }

        _statement = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        _participants = participants;
    }

    /// <summary>
    /// Posts <paramref name="forms"/> together from the transcript's browser: the first through
    /// the browser itself (so the scenario can observe it), the rest from other tabs of the same
    /// browser (<see cref="HttpActor.Tab"/>: the same cookies and origin), whose exchanges are
    /// discarded and whose status codes are returned in ascending order. Every tab copies the
    /// cookies before any request runs, because the shown request may update the browser's own.
    /// The shown request starts first and reaches the barrier (or finishes) before the others
    /// start, so when a limit admits only some of them, the shown one is admitted and which of the
    /// others are refused does not depend on thread timing.
    /// </summary>
    public async Task<(HttpExchange Shown, IReadOnlyList<int> OtherStatuses)> PostTogetherAsync(
        Transcript t,
        IReadOnlyList<HtmlForm> forms)
    {
        var tabs = forms.Skip(1).Select((_, index) => t.Browser.Tab($"tab-{index + 2}")).ToList();
        var shown = FinishAsync(t.SubmitAsync(forms[0]));
        await Task.WhenAny(_firstArrival.Task, shown);
        var others = forms.Skip(1).Select((form, index) => FinishAsync(tabs[index].SubmitAsync(form))).ToList();
        await Task.WhenAll(others.Append(shown));
        return (await shown, others.Select(other => t.Discard(other.Result).StatusCode).Order().ToList());
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

        Interlocked.Increment(ref _arrived);
        _firstArrival.TrySetResult();
        ReleaseOnceEveryoneIsAccountedFor();
        await Task.WhenAny(_released.Task, Task.Delay(MaximumWait, cancellationToken));
    }

    // A request that finishes while the barrier is closed never reached it (a request that did is
    // held until it opens), so it no longer needs to be waited for.
    private async Task<HttpExchange> FinishAsync(Task<HttpExchange> request)
    {
        try
        {
            return await request;
        }
        finally
        {
            Interlocked.Increment(ref _finished);
            ReleaseOnceEveryoneIsAccountedFor();
        }
    }

    private void ReleaseOnceEveryoneIsAccountedFor()
    {
        if (Volatile.Read(ref _arrived) + Volatile.Read(ref _finished) >= _participants)
        {
            _released.TrySetResult();
        }
    }
}
