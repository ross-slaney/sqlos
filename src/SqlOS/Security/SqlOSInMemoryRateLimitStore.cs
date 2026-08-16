namespace SqlOS.Security;

internal sealed class SqlOSInMemoryRateLimitStore : ISqlOSRateLimitStore
{
    internal const int MaximumBuckets = 4096;
    private readonly object _sync = new();
    private readonly Dictionary<(string Scope, string Key), Bucket> _buckets = [];

    public Task<SqlOSRateLimitBucketState> IncrementAsync(
        string scope,
        string key,
        int lockThreshold,
        TimeSpan window,
        TimeSpan lockoutDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EvictExpired(now);
            var bucketKey = (scope, key);
            if (!_buckets.TryGetValue(bucketKey, out var bucket))
            {
                if (!EnsureCapacity(now))
                {
                    return Task.FromResult(new SqlOSRateLimitBucketState(
                        lockThreshold,
                        now.Add(lockoutDuration),
                        Admitted: false));
                }

                bucket = new Bucket(now);
                _buckets[bucketKey] = bucket;
            }
            else if (IsExpired(bucket, now, window))
            {
                bucket = new Bucket(now);
                _buckets[bucketKey] = bucket;
            }

            var admitted = bucket.LockedUntil is null || bucket.LockedUntil <= now;
            if (admitted)
            {
                bucket.Count++;
                bucket.LockedUntil = bucket.Count >= lockThreshold
                    ? now.Add(lockoutDuration)
                    : null;
            }

            bucket.UpdatedAt = now;
            return Task.FromResult(new SqlOSRateLimitBucketState(
                bucket.Count, bucket.LockedUntil, admitted, bucket.WindowStartedAt));
        }
    }

    public Task<SqlOSRateLimitPairReservationState> ReservePairAsync(
        SqlOSRateLimitBucketRequest first,
        SqlOSRateLimitBucketRequest second,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EvictExpired(now);
            var firstBucket = GetActiveBucket(first, now);
            if (firstBucket?.LockedUntil is { } firstLockedUntil && firstLockedUntil > now)
            {
                return Task.FromResult(new SqlOSRateLimitPairReservationState(
                    null, null, 0, firstLockedUntil));
            }

            var secondBucket = GetActiveBucket(second, now);
            if (secondBucket?.LockedUntil is { } secondLockedUntil && secondLockedUntil > now)
            {
                return Task.FromResult(new SqlOSRateLimitPairReservationState(
                    null, null, 1, secondLockedUntil));
            }

            var requiredCapacity = (firstBucket == null ? 1 : 0) + (secondBucket == null ? 1 : 0);
            if (!EnsurePairCapacity(first, second, requiredCapacity, now))
            {
                return Task.FromResult(new SqlOSRateLimitPairReservationState(
                    null, null, 0, now.Add(first.LockoutDuration)));
            }

            firstBucket ??= AddBucket(first.Scope, first.Key, now);
            secondBucket ??= AddBucket(second.Scope, second.Key, now);

            IncrementBucket(firstBucket, first.LockThreshold, first.LockoutDuration, now);
            IncrementBucket(secondBucket, second.LockThreshold, second.LockoutDuration, now);
            return Task.FromResult(new SqlOSRateLimitPairReservationState(
                ToState(firstBucket), ToState(secondBucket), null, null));
        }
    }

    public Task<SqlOSRateLimitReservationState> ReserveManyAsync(
        IReadOnlyList<SqlOSRateLimitBucketRequest> buckets,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(buckets);
        lock (_sync)
        {
            EvictExpired(now);
            if (buckets.Count == 0)
            {
                return Task.FromResult(new SqlOSRateLimitReservationState([], null, null));
            }

            var active = new Bucket?[buckets.Count];
            for (var index = 0; index < buckets.Count; index++)
            {
                var request = buckets[index];
                var bucket = GetActiveBucket(request, now);
                if (bucket?.LockedUntil is { } lockedUntil && lockedUntil > now)
                {
                    return Task.FromResult(new SqlOSRateLimitReservationState(
                        new SqlOSRateLimitBucketState?[buckets.Count],
                        index,
                        lockedUntil));
                }

                active[index] = bucket;
            }

            var requiredCapacity = active.Count(static bucket => bucket == null);
            if (!EnsureManyCapacity(buckets, requiredCapacity, now))
            {
                return Task.FromResult(new SqlOSRateLimitReservationState(
                    new SqlOSRateLimitBucketState?[buckets.Count],
                    0,
                    now.Add(buckets[0].LockoutDuration)));
            }

            var states = new SqlOSRateLimitBucketState?[buckets.Count];
            for (var index = 0; index < buckets.Count; index++)
            {
                var request = buckets[index];
                var bucket = active[index] ?? AddBucket(request.Scope, request.Key, now);
                IncrementBucket(bucket, request.LockThreshold, request.LockoutDuration, now);
                states[index] = ToState(bucket);
            }

            return Task.FromResult(new SqlOSRateLimitReservationState(states, null, null));
        }
    }

    public Task<SqlOSRateLimitBucketState?> GetAsync(
        string scope,
        string key,
        DateTimeOffset now,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_buckets.TryGetValue((scope, key), out var bucket))
            {
                return Task.FromResult<SqlOSRateLimitBucketState?>(null);
            }

            if (IsExpired(bucket, now, window))
            {
                _buckets.Remove((scope, key));
                return Task.FromResult<SqlOSRateLimitBucketState?>(null);
            }

            return Task.FromResult<SqlOSRateLimitBucketState?>(
                new SqlOSRateLimitBucketState(
                    bucket.Count, bucket.LockedUntil, WindowStartedAt: bucket.WindowStartedAt));
        }
    }

    public Task DeleteAsync(
        string scope,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _buckets.Remove((scope, key));
        }

        return Task.CompletedTask;
    }

    public Task DecrementAsync(
        string scope,
        string key,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_buckets.TryGetValue((scope, key), out var bucket)
                && (bucket.LockedUntil is null || bucket.LockedUntil <= now))
            {
                bucket.Count = Math.Max(0, bucket.Count - 1);
                bucket.UpdatedAt = now;
                if (bucket.Count == 0)
                {
                    _buckets.Remove((scope, key));
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task ReleaseAsync(
        string scope,
        string key,
        int lockThreshold,
        DateTimeOffset windowStartedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_buckets.TryGetValue((scope, key), out var bucket)
                && bucket.WindowStartedAt == windowStartedAt)
            {
                bucket.Count = Math.Max(0, bucket.Count - 1);
                bucket.LockedUntil = bucket.Count >= lockThreshold ? bucket.LockedUntil : null;
                bucket.UpdatedAt = now;
                if (bucket.Count == 0)
                {
                    _buckets.Remove((scope, key));
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task ReleaseManyAsync(
        IReadOnlyList<SqlOSRateLimitReservationRelease> releases,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(releases);
        lock (_sync)
        {
            foreach (var release in releases)
            {
                if (_buckets.TryGetValue((release.Scope, release.Key), out var bucket)
                    && bucket.WindowStartedAt == release.WindowStartedAt)
                {
                    bucket.Count = Math.Max(0, bucket.Count - 1);
                    bucket.LockedUntil = bucket.Count >= release.LockThreshold ? bucket.LockedUntil : null;
                    bucket.UpdatedAt = now;
                    if (bucket.Count == 0)
                    {
                        _buckets.Remove((release.Scope, release.Key));
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    internal int BucketCount
    {
        get
        {
            lock (_sync)
            {
                return _buckets.Count;
            }
        }
    }

    private bool EnsureCapacity(DateTimeOffset now)
    {
        if (_buckets.Count < MaximumBuckets)
        {
            return true;
        }

        var evictable = _buckets
            .Where(entry => entry.Value.LockedUntil is null || entry.Value.LockedUntil <= now);
        if (!evictable.Any())
        {
            return false;
        }

        var oldest = evictable.MinBy(entry => entry.Value.UpdatedAt);
        _buckets.Remove(oldest.Key);
        return true;
    }

    private Bucket? GetActiveBucket(SqlOSRateLimitBucketRequest request, DateTimeOffset now)
    {
        if (!_buckets.TryGetValue((request.Scope, request.Key), out var bucket))
        {
            return null;
        }

        if (!IsExpired(bucket, now, request.Window))
        {
            return bucket;
        }

        _buckets.Remove((request.Scope, request.Key));
        return null;
    }

    private Bucket AddBucket(string scope, string key, DateTimeOffset now)
    {
        var bucket = new Bucket(now);
        _buckets[(scope, key)] = bucket;
        return bucket;
    }

    private bool EnsurePairCapacity(
        SqlOSRateLimitBucketRequest first,
        SqlOSRateLimitBucketRequest second,
        int requiredCapacity,
        DateTimeOffset now)
    {
        while (_buckets.Count + requiredCapacity > MaximumBuckets)
        {
            var evictable = _buckets
                .Where(entry => entry.Key != (first.Scope, first.Key)
                                && entry.Key != (second.Scope, second.Key)
                                && (entry.Value.LockedUntil is null || entry.Value.LockedUntil <= now))
                .ToArray();
            if (evictable.Length == 0)
            {
                return false;
            }

            var oldest = evictable.MinBy(entry => entry.Value.UpdatedAt);
            _buckets.Remove(oldest.Key);
        }

        return true;
    }

    private bool EnsureManyCapacity(
        IReadOnlyList<SqlOSRateLimitBucketRequest> buckets,
        int requiredCapacity,
        DateTimeOffset now)
    {
        var reserved = buckets
            .Select(request => (request.Scope, request.Key))
            .ToHashSet();
        while (_buckets.Count + requiredCapacity > MaximumBuckets)
        {
            var evictable = _buckets
                .Where(entry =>
                    !reserved.Contains(entry.Key)
                    && (entry.Value.LockedUntil is null || entry.Value.LockedUntil <= now))
                .ToArray();
            if (evictable.Length == 0)
            {
                return false;
            }

            var oldest = evictable.MinBy(entry => entry.Value.UpdatedAt);
            _buckets.Remove(oldest.Key);
        }

        return true;
    }

    private static void IncrementBucket(
        Bucket bucket,
        int lockThreshold,
        TimeSpan lockoutDuration,
        DateTimeOffset now)
    {
        bucket.Count++;
        bucket.LockedUntil = bucket.Count >= lockThreshold ? now.Add(lockoutDuration) : null;
        bucket.UpdatedAt = now;
    }

    private static SqlOSRateLimitBucketState ToState(Bucket bucket)
        => new(bucket.Count, bucket.LockedUntil, WindowStartedAt: bucket.WindowStartedAt);

    private void EvictExpired(DateTimeOffset now)
    {
        var staleBefore = now.AddDays(-1);
        foreach (var key in _buckets
                     .Where(entry =>
                         entry.Value.UpdatedAt < staleBefore
                         && (entry.Value.LockedUntil is null || entry.Value.LockedUntil <= now))
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _buckets.Remove(key);
        }
    }

    private static bool IsExpired(Bucket bucket, DateTimeOffset now, TimeSpan window)
        => (bucket.LockedUntil is null || bucket.LockedUntil <= now)
           && now - bucket.WindowStartedAt >= window;

    private sealed class Bucket(DateTimeOffset now)
    {
        public DateTimeOffset WindowStartedAt { get; } = now;
        public DateTimeOffset UpdatedAt { get; set; } = now;
        public int Count { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
    }
}
