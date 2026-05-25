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
            HashUserAgent(userAgent));
    }

    public async Task EnsureAllowedAsync(SqlOSPasswordLoginAttempt attempt, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var identity in GetBucketIdentities(attempt, includeUserBucket: true))
        {
            var bucket = await FindBucketAsync(identity, cancellationToken);
            if (bucket == null)
            {
                continue;
            }

            if (ResetExpired(bucket, now))
            {
                await _context.SaveChangesAsync(cancellationToken);
            }

            if (bucket.LockedUntil is { } lockedUntil && lockedUntil > now)
            {
                await RecordPasswordAuditAsync(
                    "password.login.rate_limit_rejected",
                    attempt,
                    data: new
                    {
                        scope = bucket.Scope,
                        retryAfter = lockedUntil,
                        failureCount = bucket.FailureCount,
                        reason = bucket.LockoutReason ?? "active_lockout"
                    },
                    cancellationToken);
                throw new InvalidOperationException(PublicFailureMessage);
            }
        }
    }

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

        var now = DateTime.UtcNow;
        var newlyLocked = new List<SqlOSPasswordLoginBucket>();

        foreach (var identity in GetBucketIdentities(attempt, includeUserBucket: true))
        {
            var bucket = await GetOrCreateBucketAsync(identity, attempt, now, cancellationToken);
            ResetExpired(bucket, now);

            bucket.FailureCount++;
            bucket.WindowStartedAt ??= now;
            bucket.LastFailureAt = now;
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
                newlyLocked.Add(bucket);
            }
        }

        await RecordPasswordAuditAsync(
            "password.login.failed",
            attempt,
            data: new
            {
                failureReason,
                lockedScopes = newlyLocked.Select(static x => x.Scope).ToArray()
            },
            cancellationToken);

        foreach (var bucket in newlyLocked.Where(static x => x.Scope is "email" or "user"))
        {
            await RecordPasswordAuditAsync(
                "password.login.locked",
                attempt,
                data: new
                {
                    scope = bucket.Scope,
                    retryAfter = bucket.LockedUntil,
                    failureCount = bucket.FailureCount
                },
                cancellationToken);
        }

        foreach (var bucket in newlyLocked.Where(static x => x.Scope is "ip" or "client" or "device"))
        {
            await RecordPasswordAuditAsync(
                "password.login.suspicious_pattern",
                attempt,
                data: new
                {
                    scope = bucket.Scope,
                    retryAfter = bucket.LockedUntil,
                    failureCount = bucket.FailureCount
                },
                cancellationToken);
        }
    }

    public async Task RecordSuccessAsync(SqlOSPasswordLoginAttempt attempt, CancellationToken cancellationToken = default)
    {
        if (_options.Enabled)
        {
            var now = DateTime.UtcNow;
            foreach (var identity in GetAccountBucketIdentities(attempt))
            {
                var bucket = await FindBucketAsync(identity, cancellationToken);
                if (bucket == null)
                {
                    continue;
                }

                bucket.FailureCount = 0;
                bucket.WindowStartedAt = null;
                bucket.LockedUntil = null;
                bucket.LockoutReason = null;
                bucket.LastSuccessAt = now;
                bucket.UpdatedAt = now;
            }
        }

        await RecordPasswordAuditAsync(
            "password.login.succeeded",
            attempt,
            data: new { resetScopes = GetAccountBucketIdentities(attempt).Select(static x => x.Scope).ToArray() },
            cancellationToken);
    }

    private async Task<SqlOSPasswordLoginBucket?> FindBucketAsync(
        PasswordBucketIdentity identity,
        CancellationToken cancellationToken)
        => await _context.Set<SqlOSPasswordLoginBucket>()
            .FirstOrDefaultAsync(
                x => x.Scope == identity.Scope && x.BucketKey == identity.Key,
                cancellationToken);

    private async Task<SqlOSPasswordLoginBucket> GetOrCreateBucketAsync(
        PasswordBucketIdentity identity,
        SqlOSPasswordLoginAttempt attempt,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var bucket = await FindBucketAsync(identity, cancellationToken);
        if (bucket != null)
        {
            return bucket;
        }

        bucket = new SqlOSPasswordLoginBucket
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

    private bool ResetExpired(SqlOSPasswordLoginBucket bucket, DateTime now)
    {
        if (bucket.LockedUntil is { } lockedUntil)
        {
            if (lockedUntil > now)
            {
                return false;
            }

            ResetBucket(bucket, now);
            return true;
        }

        if (bucket.WindowStartedAt is { } windowStartedAt && now - windowStartedAt >= _options.FailureWindow)
        {
            ResetBucket(bucket, now);
            return true;
        }

        return false;
    }

    private static void ResetBucket(SqlOSPasswordLoginBucket bucket, DateTime now)
    {
        bucket.FailureCount = 0;
        bucket.WindowStartedAt = null;
        bucket.LockedUntil = null;
        bucket.LockoutReason = null;
        bucket.UpdatedAt = now;
    }

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
}

public sealed record SqlOSPasswordLoginAttempt(
    string NormalizedEmail,
    string? UserId,
    string? ClientKey,
    string? AuthorizationRequestId,
    string Surface,
    string? IpAddress,
    string? UserAgentHash);
