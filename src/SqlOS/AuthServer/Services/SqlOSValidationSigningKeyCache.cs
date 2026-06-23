using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSValidationSigningKeyCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);

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

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_entries.TryGetValue(cacheKey, out var entry) && entry.ExpiresAt > now)
            {
                return CloneKeys(entry.Keys);
            }
        }

        var loadedKeys = CloneKeys(await loader(cancellationToken));
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl);
        lock (_gate)
        {
            _entries[cacheKey] = new CacheEntry(loadedKeys, expiresAt);
        }

        return CloneKeys(loadedKeys);
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
            PrivateKeyPem = key.PrivateKeyPem,
            IsActive = key.IsActive,
            ActivatedAt = key.ActivatedAt,
            RetiredAt = key.RetiredAt
        }).ToList();

    private sealed record CacheEntry(List<SqlOSSigningKey> Keys, DateTimeOffset ExpiresAt);
}
