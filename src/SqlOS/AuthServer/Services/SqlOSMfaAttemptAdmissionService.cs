using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSMfaAttemptAdmissionService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSMfaAttemptAdmissionService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _options = options.Value;
    }

    internal async Task<SqlOSMfaAttemptAdmissionResult> TryReserveAsync(
        SqlOSTemporaryToken challenge,
        SqlOSMfaChallengePayload payload,
        HttpContext? httpContext,
        CancellationToken cancellationToken = default)
    {
        var identities = CreateBucketIdentities(challenge, payload, httpContext)
            .OrderBy(static x => x.Scope, StringComparer.Ordinal)
            .ThenBy(static x => x.BucketKey, StringComparer.Ordinal)
            .ToArray();

        if (_context.Database.IsSqlServer())
        {
            return await ReserveSqlServerAsync(identities, cancellationToken);
        }

        return await ReserveProviderFallbackAsync(identities, cancellationToken);
    }

    internal async Task ReleaseAsync(
        SqlOSMfaAttemptReservation reservation,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsSqlServer())
        {
            await ReleaseSqlServerAsync(reservation.Buckets, cancellationToken);
            return;
        }

        await ReleaseProviderFallbackAsync(reservation.Buckets, cancellationToken);
    }

    internal async Task<bool> IsUserCapacityExhaustedAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var identity = CreateIdentity("user", userId, _options.Mfa.Totp.MaxFailedAttemptsPerUser);
        var cutoff = DateTime.UtcNow.Subtract(_options.Mfa.Totp.FailedAttemptWindow);
        var bucket = await _context.Set<SqlOSMfaAttemptBucket>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Scope == identity.Scope && x.BucketKey == identity.BucketKey,
                cancellationToken);
        return bucket is { WindowStartedAt: var startedAt }
            && startedAt >= cutoff
            && bucket.AttemptCount >= identity.Threshold;
    }

    private async Task<SqlOSMfaAttemptAdmissionResult> ReserveSqlServerAsync(
        IReadOnlyList<MfaBucketIdentity> identities,
        CancellationToken cancellationToken)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var cutoff = now.Subtract(_options.Mfa.Totp.FailedAttemptWindow);
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var connection = _context.Database.GetDbConnection();
            var schema = EscapeIdentifier(_options.Schema);
            var reservations = new List<MfaBucketReservation>(identities.Count);

            foreach (var identity in identities)
            {
                var current = await ReadBucketForUpdateAsync(
                    connection,
                    transaction,
                    schema,
                    identity,
                    cancellationToken);
                if (current is { WindowStartedAt: var startedAt }
                    && startedAt >= cutoff
                    && current.AttemptCount >= identity.Threshold)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new SqlOSMfaAttemptAdmissionResult(false, identity.Scope, null);
                }

                var windowStartedAt = current is { WindowStartedAt: var activeWindowStartedAt }
                    && activeWindowStartedAt >= cutoff
                    ? activeWindowStartedAt
                    : now;

                await UpsertReservationAsync(
                    connection,
                    transaction,
                    schema,
                    identity,
                    current,
                    now,
                    cutoff,
                    cancellationToken);
                reservations.Add(new MfaBucketReservation(identity, windowStartedAt));
            }

            await transaction.CommitAsync(cancellationToken);
            return new SqlOSMfaAttemptAdmissionResult(
                true,
                null,
                new SqlOSMfaAttemptReservation(reservations));
        });
    }

    private async Task<SqlOSMfaAttemptAdmissionResult> ReserveProviderFallbackAsync(
        IReadOnlyList<MfaBucketIdentity> identities,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.Subtract(_options.Mfa.Totp.FailedAttemptWindow);
        var pending = new List<(MfaBucketIdentity Identity, SqlOSMfaAttemptBucket? Bucket)>();
        foreach (var identity in identities)
        {
            var bucket = await _context.Set<SqlOSMfaAttemptBucket>()
                .FirstOrDefaultAsync(
                    x => x.Scope == identity.Scope && x.BucketKey == identity.BucketKey,
                    cancellationToken);
            if (bucket is { WindowStartedAt: var startedAt }
                && startedAt >= cutoff
                && bucket.AttemptCount >= identity.Threshold)
            {
                return new SqlOSMfaAttemptAdmissionResult(false, identity.Scope, null);
            }

            pending.Add((identity, bucket));
        }

        foreach (var (identity, bucket) in pending)
        {
            if (bucket == null)
            {
                _context.Set<SqlOSMfaAttemptBucket>().Add(new SqlOSMfaAttemptBucket
                {
                    Id = $"mab_{Guid.NewGuid():N}",
                    Scope = identity.Scope,
                    BucketKey = identity.BucketKey,
                    AttemptCount = 1,
                    WindowStartedAt = now,
                    LastAttemptAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                continue;
            }

            if (bucket.WindowStartedAt < cutoff)
            {
                bucket.AttemptCount = 1;
                bucket.WindowStartedAt = now;
            }
            else
            {
                bucket.AttemptCount++;
            }

            bucket.LastAttemptAt = now;
            bucket.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new SqlOSMfaAttemptAdmissionResult(
            true,
            null,
            new SqlOSMfaAttemptReservation(pending.Select(x => new MfaBucketReservation(
                x.Identity,
                x.Bucket is { WindowStartedAt: var startedAt } && startedAt >= cutoff ? startedAt : now)).ToArray()));
    }

    private async Task ReleaseSqlServerAsync(
        IReadOnlyList<MfaBucketReservation> reservations,
        CancellationToken cancellationToken)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var connection = _context.Database.GetDbConnection();
            var schema = EscapeIdentifier(_options.Schema);
            foreach (var reservation in reservations)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = $"""
                    UPDATE [{schema}].[SqlOSMfaAttemptBuckets] WITH (UPDLOCK, HOLDLOCK)
                    SET [AttemptCount] = [AttemptCount] - 1,
                        [UpdatedAt] = @now
                    WHERE [Scope] = @scope
                      AND [BucketKey] = @bucketKey
                      AND [WindowStartedAt] = @windowStartedAt
                      AND [AttemptCount] > 0;
                    """;
                AddParameter(command, "@scope", reservation.Identity.Scope);
                AddParameter(command, "@bucketKey", reservation.Identity.BucketKey);
                AddParameter(command, "@windowStartedAt", reservation.WindowStartedAt);
                AddParameter(command, "@now", DateTime.UtcNow);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task ReleaseProviderFallbackAsync(
        IReadOnlyList<MfaBucketReservation> reservations,
        CancellationToken cancellationToken)
    {
        foreach (var reservation in reservations)
        {
            var bucket = await _context.Set<SqlOSMfaAttemptBucket>()
                .FirstOrDefaultAsync(
                    x => x.Scope == reservation.Identity.Scope
                        && x.BucketKey == reservation.Identity.BucketKey,
                    cancellationToken);
            if (bucket == null
                || bucket.WindowStartedAt != reservation.WindowStartedAt
                || bucket.AttemptCount <= 0)
            {
                continue;
            }

            bucket.AttemptCount--;
            bucket.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private IEnumerable<MfaBucketIdentity> CreateBucketIdentities(
        SqlOSTemporaryToken challenge,
        SqlOSMfaChallengePayload payload,
        HttpContext? httpContext)
    {
        var totp = _options.Mfa.Totp;
        yield return CreateIdentity("challenge", challenge.Id, totp.MaxFailedAttemptsPerChallenge);
        yield return CreateIdentity("user", challenge.UserId!, totp.MaxFailedAttemptsPerUser);
        yield return CreateIdentity("client", challenge.ClientApplicationId!, totp.MaxFailedAttemptsPerClient);

        var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            yield return CreateIdentity("ip", ipAddress, totp.MaxFailedAttemptsPerIp);
        }

        var userAgent = httpContext?.Request.Headers.UserAgent.ToString();
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            // User-Agent is only a coarse client fingerprint. Bind it to the
            // authenticated user and client so a common or spoofed header
            // cannot create a global cross-account denial-of-service bucket.
            yield return CreateIdentity(
                "device",
                $"{challenge.UserId}\0{challenge.ClientApplicationId}\0{userAgent.Trim()}",
                totp.MaxFailedAttemptsPerDevice);
        }

        if (!string.IsNullOrWhiteSpace(payload.AuthorizationRequestId))
        {
            yield return CreateIdentity(
                "authorization_request",
                payload.AuthorizationRequestId,
                totp.MaxFailedAttemptsPerAuthorizationRequest);
        }
    }

    private static MfaBucketIdentity CreateIdentity(string scope, string value, int threshold)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}\0{value}"));
        return new MfaBucketIdentity(scope, Convert.ToHexString(bytes), threshold);
    }

    private static async Task<BucketState?> ReadBucketForUpdateAsync(
        System.Data.Common.DbConnection connection,
        IDbContextTransaction transaction,
        string schema,
        MfaBucketIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = $"""
            SELECT [AttemptCount], [WindowStartedAt]
            FROM [{schema}].[SqlOSMfaAttemptBuckets] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Scope] = @scope AND [BucketKey] = @bucketKey;
            """;
        AddParameter(command, "@scope", identity.Scope);
        AddParameter(command, "@bucketKey", identity.BucketKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BucketState(reader.GetInt32(0), reader.GetDateTime(1));
    }

    private static async Task UpsertReservationAsync(
        System.Data.Common.DbConnection connection,
        IDbContextTransaction transaction,
        string schema,
        MfaBucketIdentity identity,
        BucketState? current,
        DateTime now,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        if (current == null)
        {
            command.CommandText = $"""
                INSERT INTO [{schema}].[SqlOSMfaAttemptBuckets]
                    ([Id], [Scope], [BucketKey], [AttemptCount], [WindowStartedAt], [LastAttemptAt], [CreatedAt], [UpdatedAt])
                VALUES (@id, @scope, @bucketKey, 1, @now, @now, @now, @now);
                """;
            AddParameter(command, "@id", $"mab_{Guid.NewGuid():N}");
        }
        else
        {
            command.CommandText = $"""
                UPDATE [{schema}].[SqlOSMfaAttemptBuckets]
                SET [AttemptCount] = CASE WHEN [WindowStartedAt] < @cutoff THEN 1 ELSE [AttemptCount] + 1 END,
                    [WindowStartedAt] = CASE WHEN [WindowStartedAt] < @cutoff THEN @now ELSE [WindowStartedAt] END,
                    [LastAttemptAt] = @now,
                    [UpdatedAt] = @now
                WHERE [Scope] = @scope AND [BucketKey] = @bucketKey;
                """;
            AddParameter(command, "@cutoff", cutoff);
        }

        AddParameter(command, "@scope", identity.Scope);
        AddParameter(command, "@bucketKey", identity.BucketKey);
        AddParameter(command, "@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(
        System.Data.Common.DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string EscapeIdentifier(string value) => value.Replace("]", "]]", StringComparison.Ordinal);

    internal sealed record MfaBucketIdentity(string Scope, string BucketKey, int Threshold);
    internal sealed record MfaBucketReservation(MfaBucketIdentity Identity, DateTime WindowStartedAt);
    private sealed record BucketState(int AttemptCount, DateTime WindowStartedAt);
}

internal sealed record SqlOSMfaAttemptAdmissionResult(
    bool Admitted,
    string? RejectedScope,
    SqlOSMfaAttemptReservation? Reservation);

internal sealed record SqlOSMfaAttemptReservation(
    IReadOnlyList<SqlOSMfaAttemptAdmissionService.MfaBucketReservation> Buckets);
