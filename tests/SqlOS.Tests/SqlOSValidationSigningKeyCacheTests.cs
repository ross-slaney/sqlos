using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSValidationSigningKeyCacheTests
{
    [TestMethod]
    public async Task UnknownKid_ConcurrentAndRepeatedMissesShareOneAuthoritativeRefresh()
    {
        var cache = new SqlOSValidationSigningKeyCache();
        var ttl = TimeSpan.FromMinutes(5);
        var original = CreateKey("original");
        await cache.GetOrCreateAsync("issuer", ttl, _ => Task.FromResult(new List<SqlOSSigningKey> { original }), default);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCount = 0;

        async Task<List<SqlOSSigningKey>> Refresh(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref refreshCount);
            refreshStarted.TrySetResult();
            await releaseRefresh.Task.WaitAsync(cancellationToken);
            return [original];
        }

        var validations = Enumerable.Range(0, 16)
            .Select(_ => cache.RefreshIfMissingAsync("issuer", "attacker-kid", ttl, Refresh, default))
            .ToArray();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseRefresh.TrySetResult();
        var results = await Task.WhenAll(validations);
        var repeated = await cache.RefreshIfMissingAsync("issuer", "attacker-kid", ttl, Refresh, default);

        refreshCount.Should().Be(1);
        results.Should().OnlyContain(keys => keys.Select(key => key.Kid).SequenceEqual(new[] { original.Kid }));
        repeated.Select(key => key.Kid).Should().Equal(original.Kid);
    }

    [TestMethod]
    public async Task UnknownKid_RefreshFailureKeepsLastKnownKeysAndBoundsRetries()
    {
        var cache = new SqlOSValidationSigningKeyCache();
        var ttl = TimeSpan.FromMinutes(5);
        var original = CreateKey("original");
        await cache.GetOrCreateAsync("issuer", ttl, _ => Task.FromResult(new List<SqlOSSigningKey> { original }), default);
        var refreshCount = 0;

        Task<List<SqlOSSigningKey>> Fail(CancellationToken _)
        {
            Interlocked.Increment(ref refreshCount);
            throw new InvalidOperationException("database unavailable");
        }

        var first = await cache.RefreshIfMissingAsync("issuer", "unknown", ttl, Fail, default);
        var second = await cache.RefreshIfMissingAsync("issuer", "unknown", ttl, Fail, default);

        refreshCount.Should().Be(1);
        first.Select(key => key.Kid).Should().Equal(original.Kid);
        second.Select(key => key.Kid).Should().Equal(original.Kid);
    }

    [TestMethod]
    public async Task UnknownKid_AuthoritativeRefreshPublishesRotatedKey()
    {
        var cache = new SqlOSValidationSigningKeyCache();
        var ttl = TimeSpan.FromMinutes(5);
        var original = CreateKey("original");
        var rotated = CreateKey("rotated");
        await cache.GetOrCreateAsync("issuer", ttl, _ => Task.FromResult(new List<SqlOSSigningKey> { original }), default);
        var refreshCount = 0;

        var refreshed = await cache.RefreshIfMissingAsync(
            "issuer",
            rotated.Kid,
            ttl,
            _ =>
            {
                Interlocked.Increment(ref refreshCount);
                return Task.FromResult(new List<SqlOSSigningKey> { original, rotated });
            },
            default);
        var cached = await cache.GetOrCreateAsync(
            "issuer",
            ttl,
            _ => throw new InvalidOperationException("The refreshed value should be cached."),
            default);

        refreshCount.Should().Be(1);
        refreshed.Select(key => key.Kid).Should().Equal(original.Kid, rotated.Kid);
        cached.Select(key => key.Kid).Should().Equal(original.Kid, rotated.Kid);
    }

    private static SqlOSSigningKey CreateKey(string kid)
        => new()
        {
            Id = $"key_{kid}",
            Kid = kid,
            Algorithm = "RS256",
            PublicKeyPem = "public-key",
            CustodyProvider = "test",
            KeyReference = $"reference-{kid}",
            IsActive = true,
            ActivatedAt = DateTime.UtcNow
        };
}
