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

    [TestMethod]
    public async Task ReservePair_WhenStoreIsFullOfLockedBuckets_FailsClosedWithoutThrowing()
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

        var result = await store.ReservePairAsync(
            new SqlOSRateLimitBucketRequest("dashboard-global", "all", 10, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)),
            new SqlOSRateLimitBucketRequest("dashboard-ip", "203.0.113.50", 2, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)),
            now);

        result.Admitted.Should().BeFalse();
        result.RejectedIndex.Should().Be(0);
        result.RejectedLockedUntil.Should().NotBeNull();
        store.BucketCount.Should().Be(SqlOSInMemoryRateLimitStore.MaximumBuckets);
    }

    [TestMethod]
    public async Task ReserveMany_RejectsWithoutChargingEarlierBuckets()
    {
        var store = new SqlOSInMemoryRateLimitStore();
        var now = DateTimeOffset.UtcNow;
        var email = new SqlOSRateLimitBucketRequest(
            "password-reset-email", "user@example.com", 5, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        var ip = new SqlOSRateLimitBucketRequest(
            "password-reset-ip", "203.0.113.10", 1, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        var first = await store.ReserveManyAsync([email, ip], now);
        first.Admitted.Should().BeTrue();

        var second = await store.ReserveManyAsync(
            [
                new SqlOSRateLimitBucketRequest(
                    "password-reset-email", "other@example.com", 5, TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
                ip
            ],
            now.AddSeconds(1));

        second.Admitted.Should().BeFalse();
        second.RejectedIndex.Should().Be(1);
        (await store.GetAsync(email.Scope, email.Key, now.AddSeconds(2), email.Window))!.Count.Should().Be(1);
        (await store.GetAsync("password-reset-email", "other@example.com", now.AddSeconds(2), email.Window))
            .Should().BeNull();
    }

    [TestMethod]
    public async Task ReserveMany_ReleaseRestoresCapacityInTheSameWindow()
    {
        var store = new SqlOSInMemoryRateLimitStore();
        var now = DateTimeOffset.UtcNow;
        var request = new SqlOSRateLimitBucketRequest(
            "phone-otp-phone", "+12025550100", 1, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        var reserved = await store.ReserveManyAsync([request], now);
        reserved.Admitted.Should().BeTrue();
        (await store.ReserveManyAsync([request], now.AddSeconds(1))).Admitted.Should().BeFalse();

        await store.ReleaseManyAsync(
            [
                new SqlOSRateLimitReservationRelease(
                    request.Scope,
                    request.Key,
                    request.LockThreshold,
                    reserved.Buckets[0]!.WindowStartedAt!.Value)
            ],
            now.AddSeconds(2));

        (await store.ReserveManyAsync([request], now.AddSeconds(3))).Admitted.Should().BeTrue();
    }
}
