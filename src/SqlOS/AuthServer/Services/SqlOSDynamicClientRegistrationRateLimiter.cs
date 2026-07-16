using SqlOS.Security;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSDynamicClientRegistrationRateLimiter
{
    private const string Scope = "dcr";
    private readonly ISqlOSRateLimitStore _store;

    public SqlOSDynamicClientRegistrationRateLimiter()
        : this(new SqlOSInMemoryRateLimitStore())
    {
    }

    internal SqlOSDynamicClientRegistrationRateLimiter(ISqlOSRateLimitStore store)
    {
        _store = store;
    }

    public async Task<bool> TryConsumeAsync(
        string key,
        TimeSpan window,
        int maxRegistrations,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var state = await _store.IncrementAsync(
            Scope,
            key,
            checked(maxRegistrations + 1),
            window,
            window,
            now,
            cancellationToken);

        return state.LockedUntil is null;
    }
}
