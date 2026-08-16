using System.Security.Cryptography;
using System.Text;
using SqlOS.AuthServer.Configuration;
using SqlOS.Security;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Atomically reserves every applicable password-reset or phone-OTP delivery bucket before a
/// marker, challenge, or provider send. Reservations stay charged through provider failure and
/// timeout so concurrent replicas cannot exceed the configured caps. Capacity returns when the
/// rate-limit window expires.
/// </summary>
public sealed class SqlOSDeliveryAdmissionService
{
    internal const string PasswordResetEmailScope = "password-reset-email";
    internal const string PasswordResetUserScope = "password-reset-user";
    internal const string PasswordResetIpScope = "password-reset-ip";
    internal const string PasswordResetClientScope = "password-reset-client";
    internal const string PhoneOtpPhoneScope = "phone-otp-phone";
    internal const string PhoneOtpAccountScope = "phone-otp-account";
    internal const string PhoneOtpIpScope = "phone-otp-ip";
    internal const string PhoneOtpClientScope = "phone-otp-client";

    private readonly ISqlOSRateLimitStore _store;

    public SqlOSDeliveryAdmissionService()
        : this(new SqlOSInMemoryRateLimitStore())
    {
    }

    internal SqlOSDeliveryAdmissionService(ISqlOSRateLimitStore store)
    {
        _store = store;
    }

    public Task<SqlOSDeliveryAdmissionDecision> ReservePasswordResetAsync(
        string normalizedEmail,
        string? userId,
        string? ipAddress,
        string? clientKey,
        SqlOSPasswordResetOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var requests = new List<(string Scope, SqlOSRateLimitBucketRequest Request)>(4)
        {
            CreateRequest(
                PasswordResetEmailScope,
                HashKey(normalizedEmail),
                options.MaxRequestsPerEmailPerWindow,
                options.RateLimitWindow)
        };
        AddOptional(
            requests,
            PasswordResetUserScope,
            userId,
            options.MaxRequestsPerEmailPerWindow,
            options.RateLimitWindow);
        AddOptional(
            requests,
            PasswordResetIpScope,
            ipAddress,
            options.MaxRequestsPerIpPerWindow,
            options.RateLimitWindow);
        AddOptional(
            requests,
            PasswordResetClientScope,
            clientKey,
            options.MaxRequestsPerClientPerWindow,
            options.RateLimitWindow);
        return ReserveAsync(requests, now, cancellationToken);
    }

    public Task<SqlOSDeliveryAdmissionDecision> ReservePhoneOtpAsync(
        string phoneHash,
        string? userId,
        string? ipAddress,
        string? clientApplicationId,
        SqlOSPhoneOtpOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var requests = new List<(string Scope, SqlOSRateLimitBucketRequest Request)>(4)
        {
            CreateRequest(
                PhoneOtpPhoneScope,
                phoneHash,
                options.MaxSendsPerPhone,
                options.RateLimitWindow)
        };
        AddOptional(
            requests,
            PhoneOtpAccountScope,
            userId,
            options.MaxSendsPerAccount,
            options.RateLimitWindow);
        AddOptional(
            requests,
            PhoneOtpIpScope,
            ipAddress,
            options.MaxSendsPerIp,
            options.RateLimitWindow);
        AddOptional(
            requests,
            PhoneOtpClientScope,
            clientApplicationId,
            options.MaxSendsPerClient,
            options.RateLimitWindow);
        return ReserveAsync(requests, now, cancellationToken);
    }

    private async Task<SqlOSDeliveryAdmissionDecision> ReserveAsync(
        IReadOnlyList<(string Scope, SqlOSRateLimitBucketRequest Request)> requests,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ordered = requests
            .OrderBy(static x => x.Request.Scope, StringComparer.Ordinal)
            .ThenBy(static x => x.Request.Key, StringComparer.Ordinal)
            .ToArray();
        var state = await _store.ReserveManyAsync(
            ordered.Select(static x => x.Request).ToArray(),
            now,
            cancellationToken);
        if (state.Admitted)
        {
            return SqlOSDeliveryAdmissionDecision.Allow();
        }

        var rejected = ordered[state.RejectedIndex!.Value];
        return SqlOSDeliveryAdmissionDecision.Reject(
            ToPublicScope(rejected.Scope),
            state.RejectedLockedUntil ?? now.Add(rejected.Request.LockoutDuration));
    }

    private static void AddOptional(
        List<(string Scope, SqlOSRateLimitBucketRequest Request)> requests,
        string scope,
        string? key,
        int threshold,
        TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        requests.Add(CreateRequest(scope, key.Trim(), threshold, window));
    }

    private static (string Scope, SqlOSRateLimitBucketRequest Request) CreateRequest(
        string scope,
        string key,
        int threshold,
        TimeSpan window)
        => (scope, new SqlOSRateLimitBucketRequest(scope, key, threshold, window, window));

    private static string ToPublicScope(string scope)
        => scope switch
        {
            PasswordResetEmailScope => "email",
            PasswordResetUserScope => "user",
            PasswordResetIpScope or PhoneOtpIpScope => "ip",
            PasswordResetClientScope or PhoneOtpClientScope => "client",
            PhoneOtpPhoneScope => "phone",
            PhoneOtpAccountScope => "account",
            _ => scope
        };

    private static string HashKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record SqlOSDeliveryAdmissionDecision(
    bool Admitted,
    string? RejectedScope,
    DateTimeOffset? RetryAfter)
{
    public static SqlOSDeliveryAdmissionDecision Allow()
        => new(true, null, null);

    public static SqlOSDeliveryAdmissionDecision Reject(string scope, DateTimeOffset retryAfter)
        => new(false, scope, retryAfter);
}
