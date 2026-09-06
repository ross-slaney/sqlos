using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Database;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSMfaAttemptAdmissionService
{
    private const int ExpiredReservationCleanupBatchSize = 100;
    private static readonly TimeSpan ReservationTtl = TimeSpan.FromMinutes(2);

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSTotpMfaOptions _options;

    public SqlOSMfaAttemptAdmissionService(
        ISqlOSAuthServerDbContext context,
        SqlOSCryptoService cryptoService,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _cryptoService = cryptoService;
        _options = options.Value.Mfa.Totp;
    }

    /// <summary>
    /// Reserves comparison capacity in every applicable MFA bucket before TOTP or recovery-code
    /// verification. The SQL transaction completes before the factor is compared.
    /// </summary>
    public async Task<string> ReserveAsync(
        SqlOSTemporaryToken challenge,
        HttpContext? httpContext,
        string? authorizationRequestId = null,
        CancellationToken cancellationToken = default)
    {
        var reservationId = _cryptoService.GenerateId("mfa");
        var admitted = await ExecuteAtomicAsync(
            () => ReserveCoreAsync(reservationId, challenge, httpContext, authorizationRequestId, cancellationToken),
            cancellationToken);
        if (admitted)
        {
            return reservationId;
        }

        throw new InvalidOperationException(SqlOSAuthService.MfaChallengeFailureMessage);
    }

    public Task RecordFailureAsync(string reservationId, CancellationToken cancellationToken = default)
        => ExecuteAtomicAsync(() => FinalizeAsync(reservationId, release: false, cancellationToken), cancellationToken);

    public Task RecordSuccessAsync(string reservationId, CancellationToken cancellationToken = default)
        => ExecuteAtomicAsync(() => FinalizeAsync(reservationId, release: true, cancellationToken), cancellationToken);

    public async Task<bool> IsUserCapacityExhaustedAsync(string userId, CancellationToken cancellationToken = default)
    {
        var identity = new MfaBucketIdentity("user", userId, _options.MaxFailedAttemptsPerUser);
        return await ExecuteAtomicAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var bucket = await FindBucketAsync(identity, cancellationToken);
            if (bucket == null)
            {
                return false;
            }

            await CleanupExpiredReservationsForBucketsAsync([bucket.Id], now, cancellationToken);
            await RebaseIfWindowExpiredAsync(bucket, now, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            return bucket.AttemptCount >= identity.Threshold;
        }, cancellationToken);
    }

    private async Task<bool> ReserveCoreAsync(
        string reservationId,
        SqlOSTemporaryToken challenge,
        HttpContext? httpContext,
        string? authorizationRequestId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var identities = GetBucketIdentities(challenge, httpContext, authorizationRequestId).ToArray();
        var existing = new List<(MfaBucketIdentity Identity, SqlOSMfaAttemptBucket Bucket)>();
        var missing = new List<MfaBucketIdentity>();
        foreach (var identity in identities)
        {
            var bucket = await FindBucketAsync(identity, cancellationToken);
            if (bucket == null)
            {
                missing.Add(identity);
                continue;
            }

            existing.Add((identity, bucket));
        }

        await CleanupExpiredReservationsForBucketsAsync(
            existing.Select(static x => x.Bucket.Id).ToArray(),
            now,
            cancellationToken);

        foreach (var (identity, bucket) in existing)
        {
            await RebaseIfWindowExpiredAsync(bucket, now, cancellationToken);
            if (bucket.AttemptCount >= identity.Threshold)
            {
                await _context.SaveChangesAsync(cancellationToken);
                return false;
            }
        }

        var buckets = new List<(MfaBucketIdentity Identity, SqlOSMfaAttemptBucket Bucket)>(identities.Length);
        buckets.AddRange(existing);
        foreach (var identity in missing)
        {
            buckets.Add((identity, CreateBucket(identity, now)));
        }

        var reservation = new SqlOSMfaAttemptReservation
        {
            Id = reservationId,
            CreatedAt = now,
            ExpiresAt = now.Add(ReservationTtl)
        };
        _context.Set<SqlOSMfaAttemptReservation>().Add(reservation);

        foreach (var (_, bucket) in buckets)
        {
            bucket.AttemptCount++;
            bucket.WindowStartedAt ??= now;
            bucket.UpdatedAt = now;
            reservation.Buckets.Add(new SqlOSMfaAttemptReservationBucket
            {
                ReservationId = reservation.Id,
                BucketId = bucket.Id,
                Reservation = reservation,
                Bucket = bucket
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> FinalizeAsync(string reservationId, bool release, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var reservation = await _context.Set<SqlOSMfaAttemptReservation>()
            .Include(x => x.Buckets)
            .ThenInclude(x => x.Bucket)
            .SingleOrDefaultAsync(x => x.Id == reservationId, cancellationToken);
        if (reservation == null)
        {
            return true;
        }

        if (release)
        {
            foreach (var link in reservation.Buckets.ToArray())
            {
                var bucket = link.Bucket!;
                bucket.AttemptCount = Math.Max(0, bucket.AttemptCount - 1);
                bucket.UpdatedAt = now;
                var otherActive = await _context.Set<SqlOSMfaAttemptReservationBucket>()
                    .AnyAsync(x => x.BucketId == bucket.Id && x.ReservationId != reservation.Id, cancellationToken);
                if (bucket.AttemptCount == 0 && !otherActive)
                {
                    _context.Set<SqlOSMfaAttemptBucket>().Remove(bucket);
                    continue;
                }

                if (bucket.AttemptCount == 0)
                {
                    bucket.WindowStartedAt = null;
                }
            }
        }

        _context.Set<SqlOSMfaAttemptReservation>().Remove(reservation);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task CleanupExpiredReservationsForBucketsAsync(
        IReadOnlyCollection<string> bucketIds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (bucketIds.Count == 0)
        {
            return;
        }

        while (true)
        {
            var expired = await _context.Set<SqlOSMfaAttemptReservation>()
                .Where(x => x.ExpiresAt <= now && x.Buckets.Any(b => bucketIds.Contains(b.BucketId)))
                .OrderBy(x => x.ExpiresAt)
                .Take(ExpiredReservationCleanupBatchSize)
                .Include(x => x.Buckets)
                .ThenInclude(x => x.Bucket)
                .ToListAsync(cancellationToken);
            if (expired.Count == 0)
            {
                break;
            }

            foreach (var reservation in expired)
            {
                foreach (var link in reservation.Buckets)
                {
                    var bucket = link.Bucket!;
                    bucket.AttemptCount = Math.Max(0, bucket.AttemptCount - 1);
                    if (bucket.AttemptCount == 0)
                    {
                        bucket.WindowStartedAt = null;
                    }

                    bucket.UpdatedAt = now;
                }

                _context.Set<SqlOSMfaAttemptReservation>().Remove(reservation);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task RebaseIfWindowExpiredAsync(
        SqlOSMfaAttemptBucket bucket,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (bucket.WindowStartedAt is not { } startedAt || now - startedAt < _options.FailedAttemptWindow)
        {
            return;
        }

        var activeCount = await _context.Set<SqlOSMfaAttemptReservationBucket>()
            .CountAsync(x => x.BucketId == bucket.Id && x.Reservation!.ExpiresAt > now, cancellationToken);
        bucket.AttemptCount = activeCount;
        bucket.WindowStartedAt = activeCount == 0 ? null : now;
        bucket.UpdatedAt = now;
    }

    private async Task<T> ExecuteAtomicAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational())
        {
            ClearTrackedState();
            return await operation();
        }

        if (_context.Database.CurrentTransaction != null)
        {
            throw new InvalidOperationException(
                "MFA attempt admission cannot run inside a host-managed database transaction.");
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            ClearTrackedState();
            await using var transaction = await _context.Database.BeginTransactionAsync(
                SqlOSDatabase.ExclusiveWorkIsolationLevel(_context.Database),
                cancellationToken);
            await AcquireAdmissionLockAsync(cancellationToken);
            var result = await operation();
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    private Task AcquireAdmissionLockAsync(CancellationToken cancellationToken)
        => SqlOSDatabase.AcquireExclusiveTransactionLockAsync(
            _context.Database,
            "SqlOS:MfaAttemptAdmission",
            TimeSpan.FromSeconds(10),
            "Could not acquire the SqlOS MFA attempt admission lock.",
            cancellationToken);

    private void ClearTrackedState()
    {
        if (_context is not DbContext dbContext)
        {
            return;
        }

        foreach (var entry in dbContext.ChangeTracker.Entries().Where(x =>
                     x.Entity is SqlOSMfaAttemptBucket
                         or SqlOSMfaAttemptReservation
                         or SqlOSMfaAttemptReservationBucket).ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    private Task<SqlOSMfaAttemptBucket?> FindBucketAsync(
        MfaBucketIdentity identity,
        CancellationToken cancellationToken)
        => _context.Set<SqlOSMfaAttemptBucket>()
            .SingleOrDefaultAsync(x => x.Scope == identity.Scope && x.BucketKey == identity.Key, cancellationToken);

    private SqlOSMfaAttemptBucket CreateBucket(MfaBucketIdentity identity, DateTime now)
    {
        var bucket = new SqlOSMfaAttemptBucket
        {
            Id = _cryptoService.GenerateId("mfb"),
            Scope = identity.Scope,
            BucketKey = identity.Key,
            CreatedAt = now,
            UpdatedAt = now
        };
        _context.Set<SqlOSMfaAttemptBucket>().Add(bucket);
        return bucket;
    }

    private IEnumerable<MfaBucketIdentity> GetBucketIdentities(
        SqlOSTemporaryToken challenge,
        HttpContext? httpContext,
        string? authorizationRequestId)
    {
        yield return new MfaBucketIdentity("challenge", challenge.Id, _options.MaxFailedAttemptsPerChallenge);
        if (!string.IsNullOrWhiteSpace(challenge.UserId))
        {
            yield return new MfaBucketIdentity("user", challenge.UserId, _options.MaxFailedAttemptsPerUser);
        }

        if (!string.IsNullOrWhiteSpace(challenge.ClientApplicationId))
        {
            yield return new MfaBucketIdentity(
                "client",
                challenge.ClientApplicationId,
                _options.MaxFailedAttemptsPerClient);
        }

        var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            yield return new MfaBucketIdentity("ip", ipAddress, _options.MaxFailedAttemptsPerIp);
        }

        var userAgent = httpContext?.Request.Headers.UserAgent.ToString();
        if (!string.IsNullOrWhiteSpace(userAgent) && !string.IsNullOrWhiteSpace(challenge.UserId))
        {
            yield return new MfaBucketIdentity(
                "device",
                BoundKey($"{challenge.UserId}\u001F{challenge.ClientApplicationId}\u001F{userAgent.Trim()}"),
                _options.MaxFailedAttemptsPerDevice);
        }

        if (!string.IsNullOrWhiteSpace(authorizationRequestId))
        {
            yield return new MfaBucketIdentity(
                "authorization_request",
                authorizationRequestId,
                _options.MaxFailedAttemptsPerAuthorizationRequest);
        }
    }

    private static string BoundKey(string key)
    {
        if (key.Length <= 512)
        {
            return key;
        }

        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private sealed record MfaBucketIdentity(string Scope, string Key, int Threshold);
}
