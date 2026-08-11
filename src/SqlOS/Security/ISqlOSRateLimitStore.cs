namespace SqlOS.Security;

internal interface ISqlOSRateLimitStore
{
    Task<SqlOSRateLimitBucketState> IncrementAsync(
        string scope,
        string key,
        int lockThreshold,
        TimeSpan window,
        TimeSpan lockoutDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<SqlOSRateLimitBucketState?> GetAsync(
        string scope,
        string key,
        DateTimeOffset now,
        TimeSpan window,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string scope,
        string key,
        CancellationToken cancellationToken = default);

    Task DecrementAsync(
        string scope,
        string key,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(
        string scope,
        string key,
        int lockThreshold,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

internal sealed record SqlOSRateLimitBucketState(
    int Count,
    DateTimeOffset? LockedUntil,
    bool Admitted = true);
