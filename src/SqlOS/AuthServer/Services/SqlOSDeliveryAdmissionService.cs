using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Security;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Atomically reserves every applicable delivery bucket before a marker, challenge, link, or
/// provider send: password-reset emails, phone codes, email codes, and sign-in links (#424).
/// Reservations stay charged through provider failure and timeout so concurrent replicas cannot
/// exceed the configured caps. Capacity returns when the rate-limit window expires.
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
    internal const string EmailOtpEmailScope = "email-otp-email";
    internal const string EmailOtpIpScope = "email-otp-ip";
    internal const string EmailOtpClientScope = "email-otp-client";
    internal const string MagicLinkEmailScope = "magic-link-email";
    internal const string MagicLinkIpScope = "magic-link-ip";
    internal const string MagicLinkClientScope = "magic-link-client";

    /// <summary>The email-code limits are per hour, as 7.x counted the last hour's challenges.</summary>
    internal static readonly TimeSpan EmailOtpWindow = TimeSpan.FromHours(1);

    private readonly ISqlOSRateLimitStore _store;

    public SqlOSDeliveryAdmissionService()
        : this(new SqlOSInMemoryRateLimitStore())
    {
    }

    internal SqlOSDeliveryAdmissionService(ISqlOSRateLimitStore store)
    {
        _store = store;
    }

    /// <summary>
    /// The admission a service builds when none is injected: buckets in the SqlOS database, shared
    /// by every replica, or, for a store without SQL (the EF Core in-memory provider), in memory.
    /// </summary>
    internal static SqlOSDeliveryAdmissionService For(ISqlOSAuthServerDbContext context, IOptions<SqlOSAuthServerOptions> options)
        => new(context.Database.IsRelational()
            ? new SqlOSDistributedRateLimitStore(context, options)
            : new SqlOSInMemoryRateLimitStore());

    public async Task<SqlOSDeliveryAdmissionDecision> ReservePasswordResetAsync(
        string normalizedEmail,
        string? userId,
        string? ipAddress,
        string? clientKey,
        SqlOSPasswordResetOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => (await AdmitPasswordResetAsync(normalizedEmail, userId, ipAddress, clientKey, options, now, cancellationToken)).ToDecision();

    public async Task<SqlOSDeliveryAdmissionDecision> ReservePhoneOtpAsync(
        string phoneHash,
        string? userId,
        string? ipAddress,
        string? clientApplicationId,
        SqlOSPhoneOtpOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => (await AdmitPhoneOtpAsync(phoneHash, userId, ipAddress, clientApplicationId, options, now, cancellationToken)).ToDecision();

    internal Task<DeliveryAdmission> AdmitPasswordResetAsync(
        string normalizedEmail,
        string? userId,
        string? ipAddress,
        string? clientKey,
        SqlOSPasswordResetOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
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
        return ReserveAsync(InLockOrder(requests), now, cancellationToken);
    }

    internal Task<DeliveryAdmission> AdmitPhoneOtpAsync(
        string phoneHash,
        string? userId,
        string? ipAddress,
        string? clientApplicationId,
        SqlOSPhoneOtpOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
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
        return ReserveAsync(InLockOrder(requests), now, cancellationToken);
    }

    /// <summary>
    /// Admits one email code for <paramref name="normalizedEmail"/>, or names the first of its
    /// limits that is reached, checked in the 7.x order: the address, the IP address, the client.
    /// </summary>
    internal Task<DeliveryAdmission> AdmitEmailOtpAsync(
        string normalizedEmail,
        string? ipAddress,
        string? clientApplicationId,
        SqlOSEmailOtpOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requests = new List<(string Scope, SqlOSRateLimitBucketRequest Request)>(3)
        {
            CreateRequest(EmailOtpEmailScope, HashKey(normalizedEmail), options.MaxChallengesPerHour, EmailOtpWindow)
        };
        AddOptional(requests, EmailOtpIpScope, ipAddress, options.MaxChallengesPerIpPerHour, EmailOtpWindow);
        AddOptional(requests, EmailOtpClientScope, clientApplicationId, options.MaxChallengesPerClientPerHour, EmailOtpWindow);
        return AdmitInOrderAsync(requests, now, cancellationToken);
    }

    /// <summary>
    /// Admits one sign-in link for <paramref name="normalizedEmail"/>, or names the first of its
    /// limits that is reached, checked in the 7.x order: the address, the IP address, the client.
    /// </summary>
    internal Task<DeliveryAdmission> AdmitMagicLinkAsync(
        string normalizedEmail,
        string? ipAddress,
        string? clientApplicationId,
        SqlOSMagicLinkOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requests = new List<(string Scope, SqlOSRateLimitBucketRequest Request)>(3)
        {
            CreateRequest(MagicLinkEmailScope, HashKey(normalizedEmail), options.MaxLinksPerEmailPerWindow, options.RateLimitWindow)
        };
        AddOptional(requests, MagicLinkIpScope, ipAddress, options.MaxLinksPerIpPerWindow, options.RateLimitWindow);
        AddOptional(requests, MagicLinkClientScope, clientApplicationId, options.MaxLinksPerClientPerWindow, options.RateLimitWindow);
        return AdmitInOrderAsync(requests, now, cancellationToken);
    }

    /// <summary>
    /// Gives back the capacity an admitted delivery reserved, for a delivery that was never made
    /// (a resend the cooldown refuses after admission). Each bucket gives back one reservation, and
    /// only within the window it was reserved in.
    /// </summary>
    internal Task WithdrawAsync(DeliveryAdmission admission, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return admission.Reservations.Count == 0
            ? Task.CompletedTask
            : _store.ReleaseManyAsync(admission.Reservations, now, cancellationToken);
    }

    /// <summary>
    /// Reserves <paramref name="requests"/> in the order given, so a refusal names the first limit
    /// reached in that order. A limit of zero or less admits nothing, as the 7.x count check did
    /// (a bucket would still admit its first request): it refuses without reserving, unless a limit
    /// before it is already reached.
    /// </summary>
    private async Task<DeliveryAdmission> AdmitInOrderAsync(
        IReadOnlyList<(string Scope, SqlOSRateLimitBucketRequest Request)> requests,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var closedIndex = requests.ToList().FindIndex(static x => x.Request.LockThreshold <= 0);
        if (closedIndex < 0)
        {
            return await ReserveAsync(requests, now, cancellationToken);
        }

        for (var index = 0; index < closedIndex; index++)
        {
            var (scope, request) = requests[index];
            var bucket = await _store.GetAsync(request.Scope, request.Key, now, request.Window, cancellationToken);
            if (bucket?.LockedUntil is { } lockedUntil && lockedUntil > now)
            {
                return DeliveryAdmission.Refuse(ToPublicScope(scope), lockedUntil);
            }
        }

        var closed = requests[closedIndex];
        return DeliveryAdmission.Refuse(ToPublicScope(closed.Scope), now.Add(closed.Request.LockoutDuration));
    }

    private async Task<DeliveryAdmission> ReserveAsync(
        IReadOnlyList<(string Scope, SqlOSRateLimitBucketRequest Request)> requests,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = await _store.ReserveManyAsync(
            requests.Select(static x => x.Request).ToArray(),
            now,
            cancellationToken);
        if (state.Admitted)
        {
            return DeliveryAdmission.Admit(requests
                .Select((request, index) => new SqlOSRateLimitReservationRelease(
                    request.Request.Scope,
                    request.Request.Key,
                    request.Request.LockThreshold,
                    state.Buckets[index]?.WindowStartedAt ?? now))
                .ToArray());
        }

        var rejected = requests[state.RejectedIndex!.Value];
        return DeliveryAdmission.Refuse(
            ToPublicScope(rejected.Scope),
            state.RejectedLockedUntil ?? now.Add(rejected.Request.LockoutDuration));
    }

    // Password-reset and phone-code admissions report the first reached limit in bucket order.
    private static IReadOnlyList<(string Scope, SqlOSRateLimitBucketRequest Request)> InLockOrder(
        IEnumerable<(string Scope, SqlOSRateLimitBucketRequest Request)> requests)
        => requests
            .OrderBy(static x => x.Request.Scope, StringComparer.Ordinal)
            .ThenBy(static x => x.Request.Key, StringComparer.Ordinal)
            .ToArray();

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
            PasswordResetEmailScope or EmailOtpEmailScope or MagicLinkEmailScope => "email",
            PasswordResetUserScope => "user",
            PasswordResetIpScope or PhoneOtpIpScope or EmailOtpIpScope or MagicLinkIpScope => "ip",
            PasswordResetClientScope or PhoneOtpClientScope or EmailOtpClientScope or MagicLinkClientScope => "client",
            PhoneOtpPhoneScope => "phone",
            PhoneOtpAccountScope => "account",
            _ => scope
        };

    private static string HashKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// The admission's answer to one delivery: admitted, holding the buckets it reserved (which
/// <see cref="SqlOSDeliveryAdmissionService.WithdrawAsync"/> can give back), or refused at a
/// named limit (<c>email</c>, <c>ip</c>, <c>client</c>, <c>user</c>, <c>phone</c> or <c>account</c>).
/// </summary>
internal sealed class DeliveryAdmission
{
    private DeliveryAdmission(
        bool admitted,
        string? refusedLimit,
        DateTimeOffset? retryAfter,
        IReadOnlyList<SqlOSRateLimitReservationRelease> reservations)
    {
        Admitted = admitted;
        RefusedLimit = refusedLimit;
        RetryAfter = retryAfter;
        Reservations = reservations;
    }

    public bool Admitted { get; }

    /// <summary>The limit that refused the delivery, as audit rows name it; null when admitted.</summary>
    public string? RefusedLimit { get; }

    /// <summary>When the refusing limit admits again; null when admitted.</summary>
    public DateTimeOffset? RetryAfter { get; }

    internal IReadOnlyList<SqlOSRateLimitReservationRelease> Reservations { get; }

    public static DeliveryAdmission Admit(IReadOnlyList<SqlOSRateLimitReservationRelease> reservations)
        => new(true, null, null, reservations);

    public static DeliveryAdmission Refuse(string limit, DateTimeOffset retryAfter)
        => new(false, limit, retryAfter, []);

    public SqlOSDeliveryAdmissionDecision ToDecision()
        => Admitted
            ? SqlOSDeliveryAdmissionDecision.Allow()
            : SqlOSDeliveryAdmissionDecision.Reject(RefusedLimit!, RetryAfter!.Value);
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
