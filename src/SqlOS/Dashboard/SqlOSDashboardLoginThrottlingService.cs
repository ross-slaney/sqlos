using SqlOS.Configuration;
using SqlOS.Security;

namespace SqlOS.Dashboard;

public sealed class SqlOSDashboardLoginThrottlingService
{
    private const string IpScope = "dashboard-ip";
    private const string GlobalScope = "dashboard-global";
    private const string GlobalKey = "all";
    private readonly ISqlOSRateLimitStore _store;

    public SqlOSDashboardLoginThrottlingService()
        : this(new SqlOSInMemoryRateLimitStore())
    {
    }

    internal SqlOSDashboardLoginThrottlingService(ISqlOSRateLimitStore store)
    {
        _store = store;
    }

    public async Task<SqlOSDashboardLoginThrottleRejection?> GetRejectionAsync(
        string? clientIp,
        SqlOSDashboardLoginThrottlingOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return null;
        }

        var normalizedIp = NormalizeClientIp(clientIp);
        var ipState = await _store.GetAsync(
            IpScope,
            normalizedIp,
            now,
            options.Window,
            cancellationToken);
        if (ipState?.LockedUntil is { } ipLockedUntil && ipLockedUntil > now)
        {
            return new SqlOSDashboardLoginThrottleRejection("ip", ipLockedUntil);
        }

        var globalState = await _store.GetAsync(
            GlobalScope,
            GlobalKey,
            now,
            options.Window,
            cancellationToken);
        return globalState?.LockedUntil is { } globalLockedUntil && globalLockedUntil > now
            ? new SqlOSDashboardLoginThrottleRejection("global", globalLockedUntil)
            : null;
    }

    public async Task<SqlOSDashboardLoginLockoutResult> RecordFailureAsync(
        string? clientIp,
        SqlOSDashboardLoginThrottlingOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return SqlOSDashboardLoginLockoutResult.None;
        }

        var normalizedIp = NormalizeClientIp(clientIp);
        var ipState = await _store.IncrementAsync(
            IpScope,
            normalizedIp,
            options.MaxFailuresPerIp,
            options.Window,
            options.LockoutDuration,
            now,
            cancellationToken);
        var globalState = await _store.IncrementAsync(
            GlobalScope,
            GlobalKey,
            options.MaxGlobalFailures,
            options.Window,
            options.LockoutDuration,
            now,
            cancellationToken);

        return new SqlOSDashboardLoginLockoutResult(
            ipState.LockedUntil,
            globalState.LockedUntil);
    }

    public async Task RecordSuccessAsync(
        string? clientIp,
        SqlOSDashboardLoginThrottlingOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return;
        }

        var normalizedIp = NormalizeClientIp(clientIp);
        await _store.DeleteAsync(IpScope, normalizedIp, cancellationToken);
        await _store.DecrementAsync(GlobalScope, GlobalKey, now, cancellationToken);
    }

    private static string NormalizeClientIp(string? clientIp)
        => string.IsNullOrWhiteSpace(clientIp) ? SqlOSClientIpAddress.Unknown : clientIp.Trim();
}

public sealed record SqlOSDashboardLoginThrottleRejection(string Scope, DateTimeOffset RetryAfter);

public sealed record SqlOSDashboardLoginLockoutResult(DateTimeOffset? PerIpLockedUntil, DateTimeOffset? GlobalLockedUntil)
{
    public static SqlOSDashboardLoginLockoutResult None { get; } = new(null, null);
    public bool PerIpLocked => PerIpLockedUntil.HasValue;
    public bool GlobalLocked => GlobalLockedUntil.HasValue;
}
