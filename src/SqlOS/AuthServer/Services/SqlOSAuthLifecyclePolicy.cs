using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

internal static class SqlOSAuthLifecyclePolicy
{
    internal const string DeniedEventType = "auth.lifecycle.denied";
    internal const string AuthPageSessionPurpose = "auth_page_session";

    internal static async Task<SqlOSAuthLifecycleDecision> EvaluateAsync(
        ISqlOSAuthServerDbContext context,
        string userId,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        var userIsActive = await context.Set<SqlOSUser>()
            .AsNoTracking()
            .AnyAsync(x => x.Id == userId && x.IsActive, cancellationToken);
        if (!userIsActive)
        {
            return SqlOSAuthLifecycleDecision.Denied("user_inactive");
        }

        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return SqlOSAuthLifecycleDecision.Active;
        }

        var organizationIsActive = await context.Set<SqlOSOrganization>()
            .AsNoTracking()
            .AnyAsync(x => x.Id == organizationId && x.IsActive, cancellationToken);
        if (!organizationIsActive)
        {
            return SqlOSAuthLifecycleDecision.Denied("organization_inactive");
        }

        var membershipIsActive = await context.Set<SqlOSMembership>()
            .AsNoTracking()
            .AnyAsync(
                x => x.UserId == userId
                    && x.OrganizationId == organizationId
                    && x.IsActive,
                cancellationToken);
        return membershipIsActive
            ? SqlOSAuthLifecycleDecision.Active
            : SqlOSAuthLifecycleDecision.Denied("membership_inactive");
    }

    internal static void AddDeniedAudit(
        ISqlOSAuthServerDbContext context,
        string auditId,
        string boundary,
        SqlOSAuthLifecycleDecision decision,
        string? userId,
        string? organizationId,
        string? sessionId = null)
    {
        context.Set<SqlOSAuditEvent>().Add(new SqlOSAuditEvent
        {
            Id = auditId,
            EventType = DeniedEventType,
            Source = "authserver",
            ActorType = "system",
            UserId = userId,
            OrganizationId = organizationId,
            SessionId = sessionId,
            OccurredAt = DateTime.UtcNow,
            DataJson = JsonSerializer.Serialize(new
            {
                boundary,
                reason = decision.Reason
            })
        });
    }

    internal static async Task<SqlOSAuthLifecycleRevocationResult> RevokeAsync(
        ISqlOSAuthServerDbContext context,
        string? userId,
        string? organizationId,
        string reason,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) && string.IsNullOrWhiteSpace(organizationId))
        {
            throw new ArgumentException("A user or organization scope is required for lifecycle revocation.");
        }

        var sessionsQuery = context.Set<SqlOSSession>()
            .Where(x => x.RevokedAt == null);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            sessionsQuery = sessionsQuery.Where(x => x.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            sessionsQuery = sessionsQuery.Where(x => x.OrganizationId == organizationId);
        }

        var sessions = await sessionsQuery.ToListAsync(cancellationToken);
        var sessionIds = sessions.Select(x => x.Id).ToList();
        var refreshTokens = sessionIds.Count == 0
            ? []
            : await context.Set<SqlOSRefreshToken>()
                .Where(x => sessionIds.Contains(x.SessionId) && x.RevokedAt == null)
                .ToListAsync(cancellationToken);

        var authPageSessionsQuery = context.Set<SqlOSTemporaryToken>()
            .Where(x => x.Purpose == AuthPageSessionPurpose && x.ConsumedAt == null);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            authPageSessionsQuery = authPageSessionsQuery.Where(x => x.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            authPageSessionsQuery = authPageSessionsQuery.Where(x => x.OrganizationId == organizationId);
        }

        var authPageSessions = await authPageSessionsQuery.ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.RevokedAt = now;
            session.RevocationReason = reason;
        }

        foreach (var refreshToken in refreshTokens)
        {
            refreshToken.RevokedAt = now;
        }

        foreach (var authPageSession in authPageSessions)
        {
            authPageSession.ConsumedAt = now;
        }

        return new SqlOSAuthLifecycleRevocationResult(
            sessions.Count,
            refreshTokens.Count,
            authPageSessions.Count);
    }

    internal static Task<SqlOSAuthLifecycleRevocationResult> RevokeForDenialAsync(
        ISqlOSAuthServerDbContext context,
        string? userId,
        string? organizationId,
        SqlOSAuthLifecycleDecision decision,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(decision.Reason, "user_inactive", StringComparison.Ordinal))
        {
            return RevokeAsync(
                context,
                userId: userId,
                organizationId: null,
                reason: decision.Reason!,
                now: now,
                cancellationToken: cancellationToken);
        }

        if (string.Equals(decision.Reason, "organization_inactive", StringComparison.Ordinal))
        {
            return RevokeAsync(
                context,
                userId: null,
                organizationId: organizationId,
                reason: decision.Reason!,
                now: now,
                cancellationToken: cancellationToken);
        }

        return RevokeAsync(context, userId, organizationId, decision.Reason ?? "lifecycle_invalid", now, cancellationToken);
    }

    internal static async Task RevokeSessionAsync(
        ISqlOSAuthServerDbContext context,
        SqlOSSession session,
        string reason,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        session.RevokedAt = now;
        session.RevocationReason = reason;

        var refreshTokens = await context.Set<SqlOSRefreshToken>()
            .Where(x => x.SessionId == session.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var refreshToken in refreshTokens)
        {
            refreshToken.RevokedAt = now;
        }
    }
}

internal sealed record SqlOSAuthLifecycleDecision(bool IsActive, string? Reason)
{
    internal static SqlOSAuthLifecycleDecision Active { get; } = new(true, null);

    internal static SqlOSAuthLifecycleDecision Denied(string reason) => new(false, reason);
}

internal sealed record SqlOSAuthLifecycleRevocationResult(
    int SessionCount,
    int RefreshTokenCount,
    int AuthPageSessionCount);
