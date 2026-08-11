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
            return Task.FromResult(new SqlOSRateLimitBucketState(bucket.Count, bucket.LockedUntil, admitted));
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
                new SqlOSRateLimitBucketState(bucket.Count, bucket.LockedUntil));
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
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_buckets.TryGetValue((scope, key), out var bucket))
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
