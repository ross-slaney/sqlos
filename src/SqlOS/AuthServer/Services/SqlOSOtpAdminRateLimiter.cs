using SqlOS.Security;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSOtpAdminRateLimiter
{
    private readonly ISqlOSRateLimitStore _store;

    internal SqlOSOtpAdminRateLimiter(ISqlOSRateLimitStore store)
    {
        _store = store;
    }

    public async Task<bool> TryConsumeAsync(string key, DateTimeOffset now, int maxAttempts = 3, CancellationToken cancellationToken = default)
    {
        var state = await _store.IncrementAsync(
            "otp_admin_test",
            key,
            lockThreshold: checked(maxAttempts + 1),
            window: TimeSpan.FromHours(1),
            lockoutDuration: TimeSpan.FromHours(1),
            now,
            cancellationToken);
        return state.LockedUntil is null;
    }
}
