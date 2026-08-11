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
    /// Reserves both global and per-IP password-comparison capacity before hashing. The global
    /// reservation is compensated when the IP bucket rejects; an abandoned reservation remains
    /// fail-closed until the configured window or lockout expires.
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

        var globalState = await _store.IncrementAsync(
            GlobalScope,
            GlobalKey,
            options.MaxGlobalFailures,
            options.Window,
            options.LockoutDuration,
            now,
            cancellationToken);
        if (!globalState.Admitted)
        {
            return new SqlOSDashboardLoginReservationResult(
                null,
                new SqlOSDashboardLoginThrottleRejection("global", globalState.LockedUntil!.Value));
        }

        SqlOSRateLimitBucketState ipState;
        try
        {
            ipState = await _store.IncrementAsync(
                IpScope,
                normalizedIp,
                options.MaxFailuresPerIp,
                options.Window,
                options.LockoutDuration,
                now,
                cancellationToken);
        }
        catch
        {
            await TryReleaseAsync(GlobalScope, GlobalKey, options.MaxGlobalFailures, now);
            throw;
        }

        if (!ipState.Admitted)
        {
            await _store.ReleaseAsync(
                GlobalScope,
                GlobalKey,
                options.MaxGlobalFailures,
                now,
                cancellationToken);
            return new SqlOSDashboardLoginReservationResult(
                null,
                new SqlOSDashboardLoginThrottleRejection("ip", ipState.LockedUntil!.Value));
        }

        return new SqlOSDashboardLoginReservationResult(
            new SqlOSDashboardLoginReservation(
                normalizedIp,
                ipState.LockedUntil,
                globalState.LockedUntil),
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
        return result.Reservation is { } reservation
            ? new SqlOSDashboardLoginLockoutResult(
                reservation.PerIpLockedUntil,
                reservation.GlobalLockedUntil)
            : SqlOSDashboardLoginLockoutResult.None;
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
            now,
            cancellationToken);
        await _store.ReleaseAsync(
            GlobalScope,
            GlobalKey,
            options.MaxGlobalFailures,
            now,
            cancellationToken);
    }

    private async Task TryReleaseAsync(string scope, string key, int threshold, DateTimeOffset now)
    {
        try
        {
            await _store.ReleaseAsync(scope, key, threshold, now, CancellationToken.None);
        }
        catch
        {
            // The original infrastructure failure remains authoritative. Leaving capacity reserved
            // is fail-closed and the normal window/lockout expiry repairs it without admitting a hash.
        }
    }

    private static string NormalizeClientIp(string? clientIp)
        => string.IsNullOrWhiteSpace(clientIp) ? SqlOSClientIpAddress.Unknown : clientIp.Trim();
}

public sealed record SqlOSDashboardLoginThrottleRejection(string Scope, DateTimeOffset RetryAfter);

public sealed record SqlOSDashboardLoginReservation(
    string ClientIp,
    DateTimeOffset? PerIpLockedUntil,
    DateTimeOffset? GlobalLockedUntil);

public sealed record SqlOSDashboardLoginReservationResult(
    SqlOSDashboardLoginReservation? Reservation,
    SqlOSDashboardLoginThrottleRejection? Rejection);

public sealed record SqlOSDashboardLoginLockoutResult(DateTimeOffset? PerIpLockedUntil, DateTimeOffset? GlobalLockedUntil)
{
    public static SqlOSDashboardLoginLockoutResult None { get; } = new(null, null);
    public bool PerIpLocked => PerIpLockedUntil.HasValue;
    public bool GlobalLocked => GlobalLockedUntil.HasValue;
}
