using Microsoft.Extensions.Logging;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

internal sealed class SqlOSValidationSigningKeyCache
{
    internal const int MaximumNegativeKeyIdentifiers = 128;
    internal static readonly TimeSpan UnknownKidRefreshCooldown = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly ILogger<SqlOSValidationSigningKeyCache>? _logger;
    private readonly TimeProvider _timeProvider;

    public SqlOSValidationSigningKeyCache(
        ILogger<SqlOSValidationSigningKeyCache>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<List<SqlOSSigningKey>> GetOrCreateAsync(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<List<SqlOSSigningKey>>> loader,
        CancellationToken cancellationToken)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return CloneKeys(await loader(cancellationToken));
        }

        if (TryGetFreshEntry(cacheKey, out var cached))
        {
            return CloneKeys(cached.Keys);
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFreshEntry(cacheKey, out cached))
            {
                return CloneKeys(cached.Keys);
            }

            var loadedKeys = CloneKeys(await loader(cancellationToken));
            Store(
                cacheKey,
                loadedKeys,
                _timeProvider.GetUtcNow().Add(ttl),
                [],
                DateTimeOffset.MinValue);
            return CloneKeys(loadedKeys);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task<List<SqlOSSigningKey>> RefreshIfMissingAsync(
        string cacheKey,
        string kid,
        TimeSpan ttl,
        Func<CancellationToken, Task<List<SqlOSSigningKey>>> loader,
        CancellationToken cancellationToken)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return CloneKeys(await loader(cancellationToken));
        }

        if (TryGetRefreshResult(cacheKey, kid, out var cached))
        {
            return cached;
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetRefreshResult(cacheKey, kid, out cached))
            {
                return cached;
            }

            CacheEntry? previous;
            lock (_gate)
            {
                _entries.TryGetValue(cacheKey, out previous);
            }

            try
            {
                var loadedKeys = CloneKeys(await loader(cancellationToken));
                var now = _timeProvider.GetUtcNow();
                var containsKid = ContainsKid(loadedKeys, kid);
                var missingKids = previous?.MissingKids != null
                    ? new HashSet<string>(previous.MissingKids, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                if (!containsKid)
                {
                    AddMissingKid(missingKids, kid);
                }

                Store(
                    cacheKey,
                    loadedKeys,
                    containsKid || previous == null ? now.Add(ttl) : previous.ExpiresAt,
                    missingKids,
                    now.Add(GetRefreshCooldown(ttl)));
                return CloneKeys(loadedKeys);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (previous != null && previous.ExpiresAt > _timeProvider.GetUtcNow())
            {
                lock (_gate)
                {
                    AddMissingKid(previous.MissingKids, kid);
                    previous.NextUnknownKidRefreshAt = _timeProvider.GetUtcNow().Add(GetRefreshCooldown(ttl));
                }
                _logger?.LogWarning(
                    exception,
                    "SqlOS could not refresh validation signing keys for an unknown key identifier; the token remains rejected.");
                return CloneKeys(previous.Keys);
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private bool TryGetFreshEntry(string cacheKey, out CacheEntry entry)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            return _entries.TryGetValue(cacheKey, out entry!) && entry.ExpiresAt > now;
        }
    }

    private bool TryGetRefreshResult(string cacheKey, string kid, out List<SqlOSSigningKey> keys)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_entries.TryGetValue(cacheKey, out var entry)
                && entry.ExpiresAt > now
                && (ContainsKid(entry.Keys, kid)
                    || entry.MissingKids.Contains(kid)
                    || entry.NextUnknownKidRefreshAt > now))
            {
                keys = CloneKeys(entry.Keys);
                return true;
            }
        }

        keys = [];
        return false;
    }

    private void Store(
        string cacheKey,
        List<SqlOSSigningKey> keys,
        DateTimeOffset expiresAt,
        HashSet<string> missingKids,
        DateTimeOffset nextUnknownKidRefreshAt)
    {
        lock (_gate)
        {
            _entries[cacheKey] = new CacheEntry(keys, expiresAt, missingKids, nextUnknownKidRefreshAt);
        }
    }

    public void InvalidateAll()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private static List<SqlOSSigningKey> CloneKeys(IEnumerable<SqlOSSigningKey> keys)
        => keys.Select(static key => new SqlOSSigningKey
        {
            Id = key.Id,
            Kid = key.Kid,
            Algorithm = key.Algorithm,
            PublicKeyPem = key.PublicKeyPem,
            CustodyProvider = key.CustodyProvider,
            KeyReference = key.KeyReference,
            IsActive = key.IsActive,
            ActivatedAt = key.ActivatedAt,
            RetiredAt = key.RetiredAt
        }).ToList();

    private static bool ContainsKid(IEnumerable<SqlOSSigningKey> keys, string kid)
        => keys.Any(key => string.Equals(key.Kid, kid, StringComparison.Ordinal));

    private static void AddMissingKid(HashSet<string> missingKids, string kid)
    {
        if (missingKids.Count < MaximumNegativeKeyIdentifiers)
        {
            missingKids.Add(kid);
        }
    }

    private static TimeSpan GetRefreshCooldown(TimeSpan ttl)
        => ttl < UnknownKidRefreshCooldown ? ttl : UnknownKidRefreshCooldown;

    private sealed class CacheEntry(
        List<SqlOSSigningKey> keys,
        DateTimeOffset expiresAt,
        HashSet<string> missingKids,
        DateTimeOffset nextUnknownKidRefreshAt)
    {
        public List<SqlOSSigningKey> Keys { get; } = keys;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public HashSet<string> MissingKids { get; } = missingKids;
        public DateTimeOffset NextUnknownKidRefreshAt { get; set; } = nextUnknownKidRefreshAt;
    }
}
