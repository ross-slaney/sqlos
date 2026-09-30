using System.Collections.Concurrent;
using SqlOS.AuthServer.Interfaces;

namespace SqlOS.BehaviorLock.Host.Fakes;

/// <summary>
/// In-memory DNS for domain-ownership verification. Scenarios "publish" TXT records with
/// <see cref="Publish"/> (or the <c>/__probe/dns/txt</c> endpoint); lookups succeed only for a
/// published record name and exact value, and every lookup is recorded as a <see cref="DnsEffect"/>.
/// </summary>
public sealed class FakeDnsTxtVerifier : ISqlOSDomainDnsVerifier
{
    private readonly EffectLog _effects;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _records =
        new(StringComparer.OrdinalIgnoreCase);

    public FakeDnsTxtVerifier(EffectLog effects)
    {
        _effects = effects;
    }

    public void Publish(string recordName, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordName);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _records.GetOrAdd(NormalizeName(recordName), _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
            [value] = 0;
    }

    public void Unpublish(string recordName)
        => _records.TryRemove(NormalizeName(recordName), out _);

    public Task<bool> HasTxtRecordValueAsync(
        string recordName,
        string expectedValue,
        CancellationToken cancellationToken = default)
    {
        var found = _records.TryGetValue(NormalizeName(recordName), out var values)
            && values.ContainsKey(expectedValue);
        _effects.Record(new DnsEffect(recordName, expectedValue, found));
        return Task.FromResult(found);
    }

    private static string NormalizeName(string recordName)
        => recordName.Trim().TrimEnd('.');
}
