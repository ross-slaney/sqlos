using SqlOS.Configuration;

namespace SqlOS.Dashboard;

public sealed class SqlOSDashboardLoginThrottlingService
{
    private const string UnknownClientIp = "unknown";
    private readonly object _sync = new();
    private readonly Dictionary<string, FailureBucket> _ipFailures = new(StringComparer.Ordinal);
    private readonly FailureBucket _globalFailures = new();

    public SqlOSDashboardLoginThrottleRejection? GetRejection(
        string? clientIp,
        SqlOSDashboardLoginThrottlingOptions options,
        DateTimeOffset now)
    {
        if (!options.Enabled)
        {
            return null;
        }

        var normalizedIp = NormalizeClientIp(clientIp);
        lock (_sync)
        {
            if (_ipFailures.TryGetValue(normalizedIp, out var ipBucket))
            {
                ResetIfExpired(ipBucket, options, now);
                if (ipBucket.LockedUntil is { } ipLockedUntil && ipLockedUntil > now)
                {
                    return new SqlOSDashboardLoginThrottleRejection("ip", ipLockedUntil);
                }

                if (ipBucket.Count == 0)
                {
                    _ipFailures.Remove(normalizedIp);
                }
            }

            ResetIfExpired(_globalFailures, options, now);
            if (_globalFailures.LockedUntil is { } globalLockedUntil && globalLockedUntil > now)
            {
                return new SqlOSDashboardLoginThrottleRejection("global", globalLockedUntil);
            }

            return null;
        }
    }

    public SqlOSDashboardLoginLockoutResult RecordFailure(
        string? clientIp,
        SqlOSDashboardLoginThrottlingOptions options,
        DateTimeOffset now)
    {
        if (!options.Enabled)
        {
            return SqlOSDashboardLoginLockoutResult.None;
        }

        var normalizedIp = NormalizeClientIp(clientIp);
        lock (_sync)
        {
            var ipBucket = GetActiveIpBucket(normalizedIp, options, now);
            var perIpLockedUntil = RecordFailure(ipBucket, options.MaxFailuresPerIp, options.LockoutDuration, now);

            ResetIfExpired(_globalFailures, options, now);
            var globalLockedUntil = RecordFailure(_globalFailures, options.MaxGlobalFailures, options.LockoutDuration, now);

            return new SqlOSDashboardLoginLockoutResult(perIpLockedUntil, globalLockedUntil);
        }
    }

    public void RecordSuccess(
        string? clientIp,
        SqlOSDashboardLoginThrottlingOptions options,
        DateTimeOffset now)
    {
        if (!options.Enabled)
        {
            return;
        }

        var normalizedIp = NormalizeClientIp(clientIp);
        lock (_sync)
        {
            _ipFailures.Remove(normalizedIp);

            ResetIfExpired(_globalFailures, options, now);
            if (_globalFailures.LockedUntil is null && _globalFailures.Count > 0)
            {
                _globalFailures.Count--;
                if (_globalFailures.Count == 0)
                {
                    _globalFailures.WindowStartedAt = null;
                }
            }
        }
    }

    private FailureBucket GetActiveIpBucket(
        string clientIp,
        SqlOSDashboardLoginThrottlingOptions options,
        DateTimeOffset now)
    {
        if (!_ipFailures.TryGetValue(clientIp, out var bucket))
        {
            bucket = new FailureBucket();
            _ipFailures[clientIp] = bucket;
            return bucket;
        }

        ResetIfExpired(bucket, options, now);
        return bucket;
    }

    private static DateTimeOffset? RecordFailure(
        FailureBucket bucket,
        int threshold,
        TimeSpan lockoutDuration,
        DateTimeOffset now)
    {
        bucket.WindowStartedAt ??= now;
        bucket.Count++;

        if (bucket.Count < threshold || bucket.LockedUntil is not null)
        {
            return null;
        }

        bucket.LockedUntil = now.Add(lockoutDuration);
        return bucket.LockedUntil;
    }

    private static void ResetIfExpired(
        FailureBucket bucket,
        SqlOSDashboardLoginThrottlingOptions options,
        DateTimeOffset now)
    {
        if (bucket.LockedUntil is { } lockedUntil)
        {
            if (lockedUntil > now)
            {
                return;
            }

            bucket.Count = 0;
            bucket.WindowStartedAt = null;
            bucket.LockedUntil = null;
            return;
        }

        if (bucket.WindowStartedAt is { } windowStartedAt && now - windowStartedAt >= options.Window)
        {
            bucket.Count = 0;
            bucket.WindowStartedAt = null;
        }
    }

    private static string NormalizeClientIp(string? clientIp)
        => string.IsNullOrWhiteSpace(clientIp) ? UnknownClientIp : clientIp.Trim();

    private sealed class FailureBucket
    {
        public DateTimeOffset? WindowStartedAt { get; set; }
        public int Count { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
    }
}

public sealed record SqlOSDashboardLoginThrottleRejection(string Scope, DateTimeOffset RetryAfter);

public sealed record SqlOSDashboardLoginLockoutResult(DateTimeOffset? PerIpLockedUntil, DateTimeOffset? GlobalLockedUntil)
{
    public static SqlOSDashboardLoginLockoutResult None { get; } = new(null, null);
    public bool PerIpLocked => PerIpLockedUntil.HasValue;
    public bool GlobalLocked => GlobalLockedUntil.HasValue;
}
