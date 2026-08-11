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

    Task<SqlOSRateLimitPairReservationState> ReservePairAsync(
        SqlOSRateLimitBucketRequest first,
        SqlOSRateLimitBucketRequest second,
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
        DateTimeOffset windowStartedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

internal sealed record SqlOSRateLimitBucketState(
    int Count,
    DateTimeOffset? LockedUntil,
    bool Admitted = true,
    DateTimeOffset? WindowStartedAt = null);

internal sealed record SqlOSRateLimitBucketRequest(
    string Scope,
    string Key,
    int LockThreshold,
    TimeSpan Window,
    TimeSpan LockoutDuration);

internal sealed record SqlOSRateLimitPairReservationState(
    SqlOSRateLimitBucketState? First,
    SqlOSRateLimitBucketState? Second,
    int? RejectedIndex,
    DateTimeOffset? RejectedLockedUntil)
{
    public bool Admitted => RejectedIndex == null;
}
