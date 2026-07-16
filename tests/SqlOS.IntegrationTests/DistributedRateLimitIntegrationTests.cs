using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Services;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Security;

namespace SqlOS.IntegrationTests;

[TestClass]
public class DistributedRateLimitIntegrationTests
{
    [TestMethod]
    public async Task DcrLimit_IsSharedAcrossApplicationInstances()
    {
        var connectionString = GetConnectionString();
        await using var firstContext = CreateContext(connectionString);
        await using var secondContext = CreateContext(connectionString);
        var options = Options.Create(new SqlOSAuthServerOptions());
        var first = new SqlOSDynamicClientRegistrationRateLimiter(
            new SqlOSDistributedRateLimitStore(firstContext, options));
        var second = new SqlOSDynamicClientRegistrationRateLimiter(
            new SqlOSDistributedRateLimitStore(secondContext, options));
        var key = $"dcr-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IReadOnlyList<bool>> ConsumeAsync(SqlOSDynamicClientRegistrationRateLimiter limiter)
        {
            await start.Task;
            var results = new List<bool>();
            for (var i = 0; i < 5; i++)
            {
                results.Add(await limiter.TryConsumeAsync(
                    key,
                    TimeSpan.FromMinutes(5),
                    maxRegistrations: 3,
                    now.AddMilliseconds(i)));
            }

            return results;
        }

        var firstAttempts = ConsumeAsync(first);
        var secondAttempts = ConsumeAsync(second);
        start.SetResult();

        var results = (await Task.WhenAll(firstAttempts, secondAttempts)).SelectMany(x => x);
        results.Count(allowed => allowed).Should().Be(3);
        results.Count(allowed => !allowed).Should().Be(7);
    }

    [TestMethod]
    public async Task DashboardPerIpAndGlobalLockouts_AreSharedAcrossApplicationInstances()
    {
        var connectionString = GetConnectionString();
        await using var firstContext = CreateContext(connectionString);
        await using var secondContext = CreateContext(connectionString);
        var authOptions = Options.Create(new SqlOSAuthServerOptions());
        var firstStore = new SqlOSDistributedRateLimitStore(firstContext, authOptions);
        var secondStore = new SqlOSDistributedRateLimitStore(secondContext, authOptions);
        var first = new SqlOSDashboardLoginThrottlingService(firstStore);
        var second = new SqlOSDashboardLoginThrottlingService(secondStore);
        var options = new SqlOSDashboardLoginThrottlingOptions
        {
            MaxFailuresPerIp = 2,
            MaxGlobalFailures = 3,
            Window = TimeSpan.FromMinutes(5),
            LockoutDuration = TimeSpan.FromMinutes(10)
        };
        var ip = $"ip-{Guid.NewGuid():N}";
        var otherIp = $"ip-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        try
        {
            (await first.RecordFailureAsync(ip, options, now)).PerIpLocked.Should().BeFalse();
            (await second.RecordFailureAsync(ip, options, now.AddSeconds(1))).PerIpLocked.Should().BeTrue();

            var perIpRejection = await first.GetRejectionAsync(ip, options, now.AddSeconds(2));
            perIpRejection.Should().NotBeNull();
            perIpRejection!.Scope.Should().Be("ip");

            var globalResult = await first.RecordFailureAsync(otherIp, options, now.AddSeconds(3));
            globalResult.GlobalLocked.Should().BeTrue();
            var globalRejection = await second.GetRejectionAsync(
                $"ip-{Guid.NewGuid():N}",
                options,
                now.AddSeconds(4));
            globalRejection.Should().NotBeNull();
            globalRejection!.Scope.Should().Be("global");
        }
        finally
        {
            await firstStore.DeleteAsync("dashboard-ip", ip);
            await firstStore.DeleteAsync("dashboard-ip", otherIp);
            await firstStore.DeleteAsync("dashboard-global", "all");
        }
    }

    private static string GetConnectionString()
        => AspireFixture.SharedContext?.Database.GetConnectionString()
           ?? throw new InvalidOperationException("The integration database has no connection string.");

    private static TestSqlOSDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new TestSqlOSDbContext(options);
    }
}
