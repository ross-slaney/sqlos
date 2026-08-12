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

    /// <summary>
    /// Reserves both global and per-IP password-comparison capacity before hashing in one short
    /// transaction. An abandoned reservation remains fail-closed until the window or lockout expires.
    /// </summary>
    public async Task<SqlOSDashboardLoginReservationResult> ReserveAsync(
        string? clientIp,
        SqlOSDashboardLoginThrottlingOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var normalizedIp = NormalizeClientIp(clientIp);
        if (!options.Enabled)
        {
            return new SqlOSDashboardLoginReservationResult(
                new SqlOSDashboardLoginReservation(normalizedIp, null, null),
                null);
        }

        var state = await _store.ReservePairAsync(
            new SqlOSRateLimitBucketRequest(
                GlobalScope, GlobalKey, options.MaxGlobalFailures, options.Window, options.LockoutDuration),
            new SqlOSRateLimitBucketRequest(
                IpScope, normalizedIp, options.MaxFailuresPerIp, options.Window, options.LockoutDuration),
            now,
            cancellationToken);
        if (!state.Admitted)
        {
            return new SqlOSDashboardLoginReservationResult(
                null,
                new SqlOSDashboardLoginThrottleRejection(
                    state.RejectedIndex == 0 ? "global" : "ip",
                    state.RejectedLockedUntil!.Value));
        }

        var globalState = state.First!;
        var ipState = state.Second!;
        return new SqlOSDashboardLoginReservationResult(
            new SqlOSDashboardLoginReservation(
                normalizedIp,
                ipState.LockedUntil,
                globalState.LockedUntil,
                ipState.WindowStartedAt!.Value,
                globalState.WindowStartedAt!.Value),
            null);
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
        var result = await ReserveAsync(clientIp, options, now, cancellationToken);
        if (result.Reservation is { } reservation)
        {
            return new SqlOSDashboardLoginLockoutResult(
                reservation.PerIpLockedUntil,
                reservation.GlobalLockedUntil);
        }

        if (result.Rejection is { } rejection)
        {
            return rejection.Scope == "ip"
                ? new SqlOSDashboardLoginLockoutResult(rejection.RetryAfter, null)
                : new SqlOSDashboardLoginLockoutResult(null, rejection.RetryAfter);
        }

        return SqlOSDashboardLoginLockoutResult.None;
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

    public async Task RecordSuccessAsync(
        SqlOSDashboardLoginReservation reservation,
        SqlOSDashboardLoginThrottlingOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return;
        }

        await _store.ReleaseAsync(
            IpScope,
            reservation.ClientIp,
            options.MaxFailuresPerIp,
            reservation.PerIpWindowStartedAt,
            now,
            cancellationToken);
        await _store.ReleaseAsync(
            GlobalScope,
            GlobalKey,
            options.MaxGlobalFailures,
            reservation.GlobalWindowStartedAt,
            now,
            cancellationToken);
    }

    private static string NormalizeClientIp(string? clientIp)
        => string.IsNullOrWhiteSpace(clientIp) ? SqlOSClientIpAddress.Unknown : clientIp.Trim();
}

public sealed record SqlOSDashboardLoginThrottleRejection(string Scope, DateTimeOffset RetryAfter);

public sealed record SqlOSDashboardLoginReservation(
    string ClientIp,
    DateTimeOffset? PerIpLockedUntil,
    DateTimeOffset? GlobalLockedUntil,
    DateTimeOffset PerIpWindowStartedAt = default,
    DateTimeOffset GlobalWindowStartedAt = default);

public sealed record SqlOSDashboardLoginReservationResult(
    SqlOSDashboardLoginReservation? Reservation,
    SqlOSDashboardLoginThrottleRejection? Rejection);

public sealed record SqlOSDashboardLoginLockoutResult(DateTimeOffset? PerIpLockedUntil, DateTimeOffset? GlobalLockedUntil)
{
    public static SqlOSDashboardLoginLockoutResult None { get; } = new(null, null);
    public bool PerIpLocked => PerIpLockedUntil.HasValue;
    public bool GlobalLocked => GlobalLockedUntil.HasValue;
}
