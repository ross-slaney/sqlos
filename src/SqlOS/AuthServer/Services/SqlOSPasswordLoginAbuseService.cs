using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSPasswordLoginAbuseService
{
    public const string PublicFailureMessage = "Invalid email or password.";
    private const int ExpiredReservationCleanupBatchSize = 100;
    private static readonly TimeSpan MaximumReservationLifetime = TimeSpan.FromMinutes(2);

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSPasswordLoginAbuseOptions _options;

    public SqlOSPasswordLoginAbuseService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _options = options.Value.PasswordLogin;
    }

    public SqlOSPasswordLoginAttempt CreateAttempt(
        string normalizedEmail,
        HttpContext? httpContext,
        string? clientKey = null,
        string? authorizationRequestId = null,
        string? surface = null,
        string? userId = null)
    {
        var userAgent = httpContext?.Request.Headers.UserAgent.ToString();
        return new SqlOSPasswordLoginAttempt(
            normalizedEmail,
            userId,
            NormalizeClientKey(clientKey),
            authorizationRequestId,
            string.IsNullOrWhiteSpace(surface) ? "unknown" : surface.Trim(),
            NormalizeIpAddress(httpContext),
            HashUserAgent(userAgent))
        {
            ReservationId = _cryptoService.GenerateId("pla")
        };
    }

    /// <summary>
    /// Atomically reserves capacity in every applicable bucket before a password hash comparison.
    /// The SQL transaction is deliberately completed before hashing begins. A reservation that is
    /// not finalized is treated as fail-closed capacity until it expires (at most two minutes).
    /// </summary>
    public async Task ReserveAsync(SqlOSPasswordLoginAttempt attempt, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var outcome = await ExecuteAtomicAsync(() => ReserveCoreAsync(attempt, cancellationToken), cancellationToken);
        if (outcome.Rejection == null)
        {
            return;
        }

        await RecordPasswordAuditAsync(
            "password.login.rate_limit_rejected",
            attempt,
            data: new
            {
                scope = outcome.Rejection.Scope,
                retryAfter = outcome.Rejection.LockedUntil,
                failureCount = outcome.Rejection.FailureCount,
                reason = outcome.Rejection.LockoutReason ?? "active_lockout"
            },
            cancellationToken);
        throw new InvalidOperationException(PublicFailureMessage);
    }

    [Obsolete("Use ReserveAsync to atomically reserve password-comparison capacity.")]
    public Task EnsureAllowedAsync(
        SqlOSPasswordLoginAttempt attempt,
        CancellationToken cancellationToken = default)
        => ReserveAsync(attempt, cancellationToken);

    public async Task RecordFailureAsync(
        SqlOSPasswordLoginAttempt attempt,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            await RecordPasswordAuditAsync(
                "password.login.failed",
                attempt,
                data: new { failureReason },
                cancellationToken);
            return;
        }

        var locked = await ExecuteAtomicAsync(() => RecordFailureCoreAsync(attempt, cancellationToken), cancellationToken);
        await RecordPasswordAuditAsync(
            "password.login.failed",
            attempt,
            data: new
            {
                failureReason,
                lockedScopes = locked.Select(static x => x.Scope).ToArray()
            },
            cancellationToken);

        foreach (var bucket in locked.Where(static x => x.Scope is "email" or "user"))
        {
            await RecordPasswordAuditAsync(
                "password.login.locked",
                attempt,
                data: new { scope = bucket.Scope, retryAfter = bucket.LockedUntil, failureCount = bucket.FailureCount },
                cancellationToken);
        }

        foreach (var bucket in locked.Where(static x => x.Scope is "ip" or "client" or "device"))
        {
            await RecordPasswordAuditAsync(
                "password.login.suspicious_pattern",
                attempt,
                data: new { scope = bucket.Scope, retryAfter = bucket.LockedUntil, failureCount = bucket.FailureCount },
                cancellationToken);
        }
    }

    /// <summary>
    /// A successful comparison clears historical account failures while preserving other in-flight
    /// account reservations. Shared IP, client, and device buckets release only this comparison.
    /// </summary>
    public async Task RecordSuccessAsync(SqlOSPasswordLoginAttempt attempt, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> resetScopes = [];
        if (_options.Enabled)
        {
            resetScopes = await ExecuteAtomicAsync(() => RecordSuccessCoreAsync(attempt, cancellationToken), cancellationToken);
        }

        await RecordPasswordAuditAsync(
            "password.login.succeeded",
            attempt,
            data: new { resetScopes },
            cancellationToken);
    }

    private async Task<ReservationOutcome> ReserveCoreAsync(
        SqlOSPasswordLoginAttempt attempt,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await CleanupExpiredReservationsAsync(now, cancellationToken);

        var priorReservation = await _context.Set<SqlOSPasswordLoginReservation>()
            .AsNoTracking()
            .AnyAsync(x => x.Id == attempt.ReservationId, cancellationToken);
        if (priorReservation)
        {
            return ReservationOutcome.Admitted;
        }

        var identities = GetBucketIdentities(attempt, includeUserBucket: true).ToArray();
        var buckets = new List<(PasswordBucketIdentity Identity, SqlOSPasswordLoginBucket? Bucket)>();
        foreach (var identity in identities)
        {
            var bucket = await FindBucketAsync(identity, cancellationToken);
            if (bucket != null)
            {
                await ResetExpiredAsync(bucket, identity.Threshold, now, cancellationToken);
                if (bucket.LockedUntil is { } lockedUntil && lockedUntil > now)
                {
                    await _context.SaveChangesAsync(cancellationToken);
                    return new ReservationOutcome(new RejectedBucket(
                        bucket.Scope,
                        bucket.FailureCount,
                        lockedUntil,
                        bucket.LockoutReason));
                }
            }
            buckets.Add((identity, bucket));
        }

        for (var index = 0; index < buckets.Count; index++)
        {
            var (identity, bucket) = buckets[index];
            buckets[index] = (identity, bucket ?? CreateBucket(identity, attempt, now));
        }

        var reservation = new SqlOSPasswordLoginReservation
        {
            Id = attempt.ReservationId,
            CreatedAt = now,
            ExpiresAt = now.Add(GetReservationLifetime())
        };
        _context.Set<SqlOSPasswordLoginReservation>().Add(reservation);

        foreach (var (identity, nullableBucket) in buckets)
        {
            var bucket = nullableBucket!;
            bucket.FailureCount++;
            bucket.WindowStartedAt ??= now;
            bucket.UpdatedAt = now;
            bucket.NormalizedEmail ??= attempt.NormalizedEmail;
            bucket.UserId ??= attempt.UserId;
            bucket.ClientKey ??= attempt.ClientKey;
            bucket.IpAddress ??= attempt.IpAddress;
            bucket.UserAgentHash ??= attempt.UserAgentHash;
            if (bucket.FailureCount >= identity.Threshold && bucket.LockedUntil == null)
            {
                bucket.LockedUntil = now.Add(_options.LockoutDuration);
                bucket.LockoutReason = "failed_attempt_threshold";
            }

            reservation.Buckets.Add(new SqlOSPasswordLoginReservationBucket
            {
                ReservationId = reservation.Id,
                BucketId = bucket.Id,
                Reservation = reservation,
                Bucket = bucket
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        return ReservationOutcome.Admitted;
    }

    private async Task<IReadOnlyList<LockedBucket>> RecordFailureCoreAsync(
        SqlOSPasswordLoginAttempt attempt,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await CleanupExpiredReservationsAsync(now, cancellationToken);
        var reservation = await _context.Set<SqlOSPasswordLoginReservation>()
            .Include(x => x.Buckets)
            .ThenInclude(x => x.Bucket)
            .SingleOrDefaultAsync(x => x.Id == attempt.ReservationId, cancellationToken);
        if (reservation == null)
        {
            return [];
        }

        var locked = new List<LockedBucket>();
        foreach (var link in reservation.Buckets)
        {
            var bucket = link.Bucket!;
            bucket.LastFailureAt = now;
            bucket.UpdatedAt = now;
            if (bucket.LockedUntil is { } lockedUntil && lockedUntil > now)
            {
                locked.Add(new LockedBucket(bucket.Scope, bucket.FailureCount, lockedUntil));
            }
        }

        _context.Set<SqlOSPasswordLoginReservation>().Remove(reservation);
        await _context.SaveChangesAsync(cancellationToken);
        return locked;
    }

    private async Task<IReadOnlyList<string>> RecordSuccessCoreAsync(
        SqlOSPasswordLoginAttempt attempt,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await CleanupExpiredReservationsAsync(now, cancellationToken);
        var reservation = await _context.Set<SqlOSPasswordLoginReservation>()
            .Include(x => x.Buckets)
            .ThenInclude(x => x.Bucket)
            .SingleOrDefaultAsync(x => x.Id == attempt.ReservationId, cancellationToken);
        if (reservation == null)
        {
            throw new InvalidOperationException("The password comparison reservation is no longer active.");
        }

        var resetScopes = new List<string>();
        foreach (var link in reservation.Buckets)
        {
            var bucket = link.Bucket!;
            if (bucket.Scope is "email" or "user")
            {
                var pendingCount = await _context.Set<SqlOSPasswordLoginReservationBucket>()
                    .CountAsync(x => x.BucketId == bucket.Id && x.ReservationId != reservation.Id, cancellationToken);
                bucket.FailureCount = pendingCount;
                bucket.WindowStartedAt = pendingCount == 0 ? null : bucket.WindowStartedAt ?? now;
                ApplyLockState(bucket, GetThreshold(bucket.Scope), now);
                resetScopes.Add(bucket.Scope);
            }
            else
            {
                ReleaseReservationFromBucket(bucket, now);
            }

            bucket.LastSuccessAt = now;
            bucket.UpdatedAt = now;
        }

        _context.Set<SqlOSPasswordLoginReservation>().Remove(reservation);
        await _context.SaveChangesAsync(cancellationToken);
        return resetScopes;
    }

    private async Task CleanupExpiredReservationsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var expired = await _context.Set<SqlOSPasswordLoginReservation>()
            .Where(x => x.ExpiresAt <= now)
            .OrderBy(x => x.ExpiresAt)
            .Take(ExpiredReservationCleanupBatchSize)
            .Include(x => x.Buckets)
            .ThenInclude(x => x.Bucket)
            .ToListAsync(cancellationToken);

        foreach (var reservation in expired)
        {
            foreach (var link in reservation.Buckets)
            {
                ReleaseReservationFromBucket(link.Bucket!, now);
            }

            _context.Set<SqlOSPasswordLoginReservation>().Remove(reservation);
        }
    }

    private void ReleaseReservationFromBucket(SqlOSPasswordLoginBucket bucket, DateTime now)
    {
        bucket.FailureCount = Math.Max(0, bucket.FailureCount - 1);
        if (bucket.FailureCount == 0)
        {
            bucket.WindowStartedAt = null;
        }

        ApplyLockState(bucket, GetThreshold(bucket.Scope), now);
        bucket.UpdatedAt = now;
    }

    private void ApplyLockState(SqlOSPasswordLoginBucket bucket, int threshold, DateTime now)
    {
        if (bucket.FailureCount >= threshold)
        {
            bucket.LockedUntil ??= now.Add(_options.LockoutDuration);
            bucket.LockoutReason ??= "failed_attempt_threshold";
            return;
        }

        bucket.LockedUntil = null;
        bucket.LockoutReason = null;
    }

    private async Task<T> ExecuteAtomicAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational())
        {
            ClearTrackedAbuseState();
            return await operation();
        }

        if (_context.Database.CurrentTransaction != null)
        {
            ClearTrackedAbuseState();
            await AcquireAdmissionLockAsync(cancellationToken);
            return await operation();
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            ClearTrackedAbuseState();
            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireAdmissionLockAsync(cancellationToken);
            var result = await operation();
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    private Task AcquireAdmissionLockAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return _context.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = N'SqlOS:PasswordLoginAdmission',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 10000;
            IF @result < 0 THROW 51000, 'Could not acquire the SqlOS password-login admission lock.', 1;
            """, cancellationToken);
    }

    private void ClearTrackedAbuseState()
    {
        if (_context is not DbContext dbContext)
        {
            return;
        }

        foreach (var entry in dbContext.ChangeTracker.Entries().Where(x =>
                     x.Entity is SqlOSPasswordLoginBucket
                         or SqlOSPasswordLoginReservation
                         or SqlOSPasswordLoginReservationBucket).ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task<SqlOSPasswordLoginBucket?> FindBucketAsync(
        PasswordBucketIdentity identity,
        CancellationToken cancellationToken)
        => await _context.Set<SqlOSPasswordLoginBucket>()
            .SingleOrDefaultAsync(x => x.Scope == identity.Scope && x.BucketKey == identity.Key, cancellationToken);

    private SqlOSPasswordLoginBucket CreateBucket(
        PasswordBucketIdentity identity,
        SqlOSPasswordLoginAttempt attempt,
        DateTime now)
    {
        var bucket = new SqlOSPasswordLoginBucket
        {
            Id = _cryptoService.GenerateId("plb"),
            Scope = identity.Scope,
            BucketKey = identity.Key,
            NormalizedEmail = attempt.NormalizedEmail,
            UserId = attempt.UserId,
            ClientKey = attempt.ClientKey,
            IpAddress = attempt.IpAddress,
            UserAgentHash = attempt.UserAgentHash,
            CreatedAt = now,
            UpdatedAt = now
        };
        _context.Set<SqlOSPasswordLoginBucket>().Add(bucket);
        return bucket;
    }

    private IEnumerable<PasswordBucketIdentity> GetBucketIdentities(
        SqlOSPasswordLoginAttempt attempt,
        bool includeUserBucket)
    {
        foreach (var identity in GetAccountBucketIdentities(attempt, includeUserBucket))
        {
            yield return identity;
        }

        if (!string.IsNullOrWhiteSpace(attempt.IpAddress) && _options.MaxFailedAttemptsPerIp > 0)
        {
            yield return new PasswordBucketIdentity("ip", attempt.IpAddress, _options.MaxFailedAttemptsPerIp);
        }

        if (!string.IsNullOrWhiteSpace(attempt.ClientKey) && _options.MaxFailedAttemptsPerClient > 0)
        {
            yield return new PasswordBucketIdentity("client", attempt.ClientKey, _options.MaxFailedAttemptsPerClient);
        }

        if (!string.IsNullOrWhiteSpace(attempt.UserAgentHash) && _options.MaxFailedAttemptsPerDevice > 0)
        {
            yield return new PasswordBucketIdentity("device", attempt.UserAgentHash, _options.MaxFailedAttemptsPerDevice);
        }
    }

    private IEnumerable<PasswordBucketIdentity> GetAccountBucketIdentities(
        SqlOSPasswordLoginAttempt attempt,
        bool includeUserBucket = true)
    {
        if (!string.IsNullOrWhiteSpace(attempt.NormalizedEmail) && _options.MaxFailedAttemptsPerAccount > 0)
        {
            yield return new PasswordBucketIdentity("email", attempt.NormalizedEmail, _options.MaxFailedAttemptsPerAccount);
        }

        if (includeUserBucket && !string.IsNullOrWhiteSpace(attempt.UserId) && _options.MaxFailedAttemptsPerAccount > 0)
        {
            yield return new PasswordBucketIdentity("user", attempt.UserId, _options.MaxFailedAttemptsPerAccount);
        }
    }

    private async Task<bool> ResetExpiredAsync(
        SqlOSPasswordLoginBucket bucket,
        int threshold,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (bucket.LockedUntil is { } lockedUntil)
        {
            if (lockedUntil > now)
            {
                return false;
            }

            await ResetBucketPreservingReservationsAsync(bucket, threshold, now, cancellationToken);
            return true;
        }

        if (bucket.WindowStartedAt is { } windowStartedAt && now - windowStartedAt >= _options.FailureWindow)
        {
            await ResetBucketPreservingReservationsAsync(bucket, threshold, now, cancellationToken);
            return true;
        }

        return false;
    }

    private async Task ResetBucketPreservingReservationsAsync(
        SqlOSPasswordLoginBucket bucket,
        int threshold,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var activeReservations = await _context.Set<SqlOSPasswordLoginReservationBucket>()
            .CountAsync(
                x => x.BucketId == bucket.Id && x.Reservation!.ExpiresAt > now,
                cancellationToken);
        bucket.FailureCount = activeReservations;
        bucket.WindowStartedAt = activeReservations == 0 ? null : now;
        bucket.LockedUntil = null;
        bucket.LockoutReason = null;
        ApplyLockState(bucket, threshold, now);
        bucket.UpdatedAt = now;
    }

    private int GetThreshold(string scope) => scope switch
    {
        "email" or "user" => _options.MaxFailedAttemptsPerAccount,
        "ip" => _options.MaxFailedAttemptsPerIp,
        "client" => _options.MaxFailedAttemptsPerClient,
        "device" => _options.MaxFailedAttemptsPerDevice,
        _ => int.MaxValue
    };

    private static TimeSpan GetReservationLifetime() => MaximumReservationLifetime;

    private async Task RecordPasswordAuditAsync(
        string eventType,
        SqlOSPasswordLoginAttempt attempt,
        object? data,
        CancellationToken cancellationToken)
        => await _adminService.RecordAuditAsync(
            eventType,
            eventType == "password.login.succeeded" && attempt.UserId != null ? "user" : "system",
            eventType == "password.login.succeeded" ? attempt.UserId : null,
            userId: attempt.UserId,
            ipAddress: attempt.IpAddress,
            data: new
            {
                attempt.NormalizedEmail,
                maskedEmail = MaskEmail(attempt.NormalizedEmail),
                attempt.ClientKey,
                attempt.AuthorizationRequestId,
                attempt.Surface,
                attempt.UserAgentHash,
                details = data
            },
            cancellationToken: cancellationToken);

    private static string? NormalizeIpAddress(HttpContext? httpContext)
        => httpContext?.Connection.RemoteIpAddress?.ToString();

    private static string? NormalizeClientKey(string? clientKey)
        => string.IsNullOrWhiteSpace(clientKey) ? null : clientKey.Trim();

    private static string? HashUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userAgent.Trim())));
    }

    private static string MaskEmail(string normalizedEmail)
    {
        var atIndex = normalizedEmail.IndexOf('@');
        if (atIndex <= 1 || atIndex == normalizedEmail.Length - 1)
        {
            return normalizedEmail;
        }

        var local = normalizedEmail[..atIndex];
        var domain = normalizedEmail[(atIndex + 1)..];
        var visibleCount = Math.Min(2, local.Length);
        return $"{local[..visibleCount]}***@{domain}";
    }

    private sealed record PasswordBucketIdentity(string Scope, string Key, int Threshold);
    private sealed record RejectedBucket(string Scope, int FailureCount, DateTime LockedUntil, string? LockoutReason);
    private sealed record LockedBucket(string Scope, int FailureCount, DateTime LockedUntil);
    private sealed record ReservationOutcome(RejectedBucket? Rejection)
    {
        public static ReservationOutcome Admitted { get; } = new((RejectedBucket?)null);
    }
}

public sealed record SqlOSPasswordLoginAttempt(
    string NormalizedEmail,
    string? UserId,
    string? ClientKey,
    string? AuthorizationRequestId,
    string Surface,
    string? IpAddress,
    string? UserAgentHash)
{
    public string ReservationId { get; init; } = $"pla_{Guid.NewGuid():N}"[..28];
}
