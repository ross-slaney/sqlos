using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Security;

namespace SqlOS.Tests;

[TestClass]
public class SqlOSInMemoryRateLimitStoreTests
{
    [TestMethod]
    public async Task Fallback_EvictsOldestBucketAndNeverExceedsBound()
    {
        var store = new SqlOSInMemoryRateLimitStore();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < SqlOSInMemoryRateLimitStore.MaximumBuckets + 50; i++)
        {
            await store.IncrementAsync(
                "test",
                $"client-{i}",
                lockThreshold: 10,
                window: TimeSpan.FromMinutes(5),
                lockoutDuration: TimeSpan.FromMinutes(5),
                now: now.AddMilliseconds(i));
        }

        store.BucketCount.Should().Be(SqlOSInMemoryRateLimitStore.MaximumBuckets);
        (await store.GetAsync("test", "client-0", now.AddMinutes(1), TimeSpan.FromMinutes(5)))
            .Should().BeNull();
    }

    [TestMethod]
    public async Task Fallback_WhenAllBucketsAreActivelyLocked_FailsClosedWithoutEvictingThem()
    {
        var store = new SqlOSInMemoryRateLimitStore();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < SqlOSInMemoryRateLimitStore.MaximumBuckets; i++)
        {
            await store.IncrementAsync(
                "test",
                $"locked-{i}",
                lockThreshold: 1,
                window: TimeSpan.FromDays(2),
                lockoutDuration: TimeSpan.FromDays(2),
                now);
        }

        var overflow = await store.IncrementAsync(
            "test",
            "overflow",
            lockThreshold: 2,
            window: TimeSpan.FromMinutes(5),
            lockoutDuration: TimeSpan.FromMinutes(5),
            now);

        overflow.LockedUntil.Should().BeAfter(now);
        store.BucketCount.Should().Be(SqlOSInMemoryRateLimitStore.MaximumBuckets);
        (await store.GetAsync("test", "locked-0", now, TimeSpan.FromDays(2)))
            .Should().NotBeNull();
    }
}
