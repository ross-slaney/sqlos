using Microsoft.Extensions.Logging;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

internal sealed class SqlOSValidationSigningKeyCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly ILogger<SqlOSValidationSigningKeyCache>? _logger;

    public SqlOSValidationSigningKeyCache(ILogger<SqlOSValidationSigningKeyCache>? logger = null)
    {
        _logger = logger;
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
            Store(cacheKey, loadedKeys, ttl, []);
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
                var missingKids = previous?.MissingKids != null
                    ? new HashSet<string>(previous.MissingKids, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                if (!ContainsKid(loadedKeys, kid))
                {
                    missingKids.Add(kid);
                }

                Store(cacheKey, loadedKeys, ttl, missingKids);
                return CloneKeys(loadedKeys);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (previous != null && previous.ExpiresAt > DateTimeOffset.UtcNow)
            {
                lock (_gate)
                {
                    previous.MissingKids.Add(kid);
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
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            return _entries.TryGetValue(cacheKey, out entry!) && entry.ExpiresAt > now;
        }
    }

    private bool TryGetRefreshResult(string cacheKey, string kid, out List<SqlOSSigningKey> keys)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_entries.TryGetValue(cacheKey, out var entry)
                && entry.ExpiresAt > now
                && (ContainsKid(entry.Keys, kid) || entry.MissingKids.Contains(kid)))
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
        TimeSpan ttl,
        HashSet<string> missingKids)
    {
        lock (_gate)
        {
            _entries[cacheKey] = new CacheEntry(keys, DateTimeOffset.UtcNow.Add(ttl), missingKids);
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

    private sealed record CacheEntry(
        List<SqlOSSigningKey> Keys,
        DateTimeOffset ExpiresAt,
        HashSet<string> MissingKids);
}
