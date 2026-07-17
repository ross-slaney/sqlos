using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Authorized control-plane session revocation. This is deliberately an imperative
/// administrative workflow and is not part of startup configuration reconciliation.
/// </summary>
public sealed class SqlOSSessionRevocationService
{
    private const int MaxMatches = 10_000;
    private const int MaxIdentifierLength = 256;
    private const int MaxReasonLength = 256;
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly ISqlOSAuditLogService _auditLogs;

    public SqlOSSessionRevocationService(ISqlOSAuthServerDbContext context, ISqlOSAuditLogService auditLogs)
    {
        _context = context;
        _auditLogs = auditLogs;
    }

    public Task<SqlOSAdminSessionRevocationResult> PreviewAsync(
        SqlOSAdminSessionRevocationRequest request,
        CancellationToken cancellationToken = default)
        => ProcessAsync(request with { Confirm = false }, execute: false, cancellationToken);

    public Task<SqlOSAdminSessionRevocationResult> RevokeAsync(
        SqlOSAdminSessionRevocationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Confirm)
        {
            throw new ArgumentException("Explicit confirmation is required.", nameof(request));
        }

        return ProcessAsync(request, execute: true, cancellationToken);
    }

    private async Task<SqlOSAdminSessionRevocationResult> ProcessAsync(
        SqlOSAdminSessionRevocationRequest request,
        bool execute,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(request);
        var operationId = normalized.OperationId ?? $"session-revocation-{Guid.NewGuid():N}";

        if (execute && _context.Database.IsRelational())
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            var attempt = 0;
            return await strategy.ExecuteAsync(async () =>
            {
                if (attempt++ > 0 && _context is DbContext retryContext)
                {
                    retryContext.ChangeTracker.Clear();
                }

                return await ProcessCoreAsync(normalized, execute, operationId, cancellationToken);
            });
        }

        return await ProcessCoreAsync(normalized, execute, operationId, cancellationToken);
    }

    private async Task<SqlOSAdminSessionRevocationResult> ProcessCoreAsync(
        SqlOSAdminSessionRevocationRequest normalized,
        bool execute,
        string operationId,
        CancellationToken cancellationToken)
    {

        await using var transaction = execute && _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        if (execute && string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            const string resource = "SqlOS:admin-session-revocation";
            await _context.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = {resource},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 30000;
                IF @result < 0 THROW 51000, 'Could not acquire the session revocation lock.', 1;
                """, cancellationToken);
        }

        var selectorFingerprint = BuildSelectorFingerprint(normalized);
        if (execute)
        {
            var priorOperation = await _context.Set<SqlOSAuditEvent>()
                .AsNoTracking()
                .Where(x => x.EventType == "session.admin-revoked" && x.CorrelationId == operationId)
                .Select(x => x.DataJson)
                .FirstOrDefaultAsync(cancellationToken);
            if (priorOperation != null && !HasSelectorFingerprint(priorOperation, selectorFingerprint))
            {
                throw new InvalidOperationException("Operation ID has already been used for a different revocation scope.");
            }
        }

        var query = ApplySelectors(_context.Set<SqlOSSession>().AsQueryable(), normalized);
        var matched = await query.CountAsync(cancellationToken);
        if (matched > MaxMatches)
        {
            throw new InvalidOperationException($"The revocation matches more than {MaxMatches:N0} sessions. Add a narrower filter.");
        }
        if (execute && normalized.ExpectedMatchedSessions.HasValue && normalized.ExpectedMatchedSessions.Value != matched)
        {
            throw new InvalidOperationException("The revocation scope changed after preview. Preview the operation again before confirming.");
        }
        if (execute && normalized.SessionId == null && !normalized.ExpectedMatchedSessions.HasValue)
        {
            throw new ArgumentException("Broad revocation requires the matched-session count returned by preview.");
        }

        var alreadyRevoked = await query.CountAsync(x => x.RevokedAt != null, cancellationToken);
        var sessionIds = await query.Select(x => x.Id).ToListAsync(cancellationToken);
        var activeRefreshTokens = sessionIds.Count == 0
            ? 0
            : await _context.Set<SqlOSRefreshToken>()
                .CountAsync(x => sessionIds.Contains(x.SessionId) && x.RevokedAt == null, cancellationToken);

        if (!execute)
        {
            return new SqlOSAdminSessionRevocationResult(
                true, operationId, matched, 0, alreadyRevoked, activeRefreshTokens, 0, null);
        }

        if (matched == 0)
        {
            return new SqlOSAdminSessionRevocationResult(
                false, operationId, 0, 0, 0, 0, 0, null);
        }

        var now = DateTime.UtcNow;
        var reason = normalized.Reason!;
        var sessions = await query.Where(x => x.RevokedAt == null).ToListAsync(cancellationToken);
        var refreshTokens = sessionIds.Count == 0
            ? []
            : await _context.Set<SqlOSRefreshToken>()
                .Where(x => sessionIds.Contains(x.SessionId) && x.RevokedAt == null)
                .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.RevokedAt = now;
            session.RevocationReason = reason;
        }

        foreach (var refreshToken in refreshTokens)
        {
            refreshToken.RevokedAt = now;
            refreshToken.ReplacementTokenResponse = null;
            refreshToken.ReplacementOrganizationId = null;
            refreshToken.ReplacementAccessTokenExpiresAt = null;
        }

        await _context.SaveChangesAsync(cancellationToken);

        var targets = new List<SqlOSAuditTarget>();
        if (normalized.SessionId != null) targets.Add(new("session", normalized.SessionId));
        if (normalized.UserId != null) targets.Add(new("user", normalized.UserId));
        if (normalized.OrganizationId != null) targets.Add(new("organization", normalized.OrganizationId));
        if (normalized.ClientApplicationId != null) targets.Add(new("client", normalized.ClientApplicationId));
        var audit = await _auditLogs.RecordAsync(
            new SqlOSAuditLogRecordRequest(
                Action: "session.admin-revoked",
                OrganizationId: normalized.OrganizationId,
                UserId: normalized.UserId,
                ApplicationKey: normalized.ClientApplicationId,
                Source: "authserver",
                Actor: new SqlOSAuditActor("admin"),
                Targets: targets,
                Context: new SqlOSAuditContext(SessionId: normalized.SessionId, CorrelationId: operationId),
                IdempotencyKey: $"admin-session-revocation:{operationId}",
                Metadata: new Dictionary<string, object?>
                {
                    ["operation_id"] = operationId,
                    ["reason"] = reason,
                    ["selector_fingerprint"] = selectorFingerprint,
                    ["matched_sessions"] = matched,
                    ["newly_revoked_sessions"] = sessions.Count,
                    ["already_revoked_sessions"] = alreadyRevoked,
                    ["newly_revoked_refresh_credentials"] = refreshTokens.Count
                }),
            cancellationToken);

        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new SqlOSAdminSessionRevocationResult(
            false, operationId, matched, sessions.Count, alreadyRevoked,
            activeRefreshTokens, refreshTokens.Count, now, audit.EventId);
    }

    private static IQueryable<SqlOSSession> ApplySelectors(
        IQueryable<SqlOSSession> query,
        SqlOSAdminSessionRevocationRequest request)
    {
        if (request.SessionId != null) query = query.Where(x => x.Id == request.SessionId);
        if (request.UserId != null) query = query.Where(x => x.UserId == request.UserId);
        if (request.OrganizationId != null) query = query.Where(x => x.OrganizationId == request.OrganizationId);
        if (request.ClientApplicationId != null)
        {
            query = query.Where(x => x.ClientApplicationId == request.ClientApplicationId
                || x.ClientApplication!.ClientId == request.ClientApplicationId);
        }
        return query;
    }

    private static SqlOSAdminSessionRevocationRequest Normalize(SqlOSAdminSessionRevocationRequest request)
    {
        static string? Field(string? value, string name)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (normalized?.Length > MaxIdentifierLength) throw new ArgumentException($"{name} is too long.");
            return normalized;
        }

        var normalized = request with
        {
            SessionId = Field(request.SessionId, nameof(request.SessionId)),
            UserId = Field(request.UserId, nameof(request.UserId)),
            OrganizationId = Field(request.OrganizationId, nameof(request.OrganizationId)),
            ClientApplicationId = Field(request.ClientApplicationId, nameof(request.ClientApplicationId)),
            OperationId = Field(request.OperationId, nameof(request.OperationId)),
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? "admin_revoked" : request.Reason.Trim()
        };

        if (normalized.Reason!.Length > MaxReasonLength) throw new ArgumentException("Reason is too long.");
        if (normalized.OperationId?.Length > 128) throw new ArgumentException("OperationId is too long.");
        if (normalized.ExpectedMatchedSessions < 0 || normalized.ExpectedMatchedSessions > MaxMatches)
        {
            throw new ArgumentException($"ExpectedMatchedSessions must be between 0 and {MaxMatches:N0}.");
        }
        if (normalized.SessionId == null && normalized.UserId == null && normalized.OrganizationId == null && normalized.ClientApplicationId == null)
        {
            throw new ArgumentException("At least one session, user, organization, or client selector is required.");
        }

        return normalized;
    }

    private static string BuildSelectorFingerprint(SqlOSAdminSessionRevocationRequest request)
    {
        var canonical = string.Join('\n',
            request.SessionId ?? string.Empty,
            request.UserId ?? string.Empty,
            request.OrganizationId ?? string.Empty,
            request.ClientApplicationId ?? string.Empty,
            request.Reason ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool HasSelectorFingerprint(string metadataJson, string expected)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty("selector_fingerprint", out var value)
                && string.Equals(value.GetString(), expected, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
