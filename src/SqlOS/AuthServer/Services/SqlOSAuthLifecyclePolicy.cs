using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

internal static class SqlOSAuthLifecyclePolicy
{
    internal const string DeniedEventType = "auth.lifecycle.denied";
    internal const string AuthPageSessionPurpose = "auth_page_session";
    private static readonly string[] SessionIssuanceTemporaryTokenPurposes =
    [
        AuthPageSessionPurpose,
        "auth_code",
        "auth_page_pending",
        "mfa_challenge",
        "oidc_browser_code",
        "pending_auth"
    ];

    internal static async Task<SqlOSAuthLifecycleDecision> EvaluateAsync(
        ISqlOSAuthServerDbContext context,
        string userId,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            var userIsActive = await context.Set<SqlOSUser>()
                .AsNoTracking()
                .AnyAsync(x => x.Id == userId && x.IsActive, cancellationToken);
            return userIsActive
                ? SqlOSAuthLifecycleDecision.Active
                : SqlOSAuthLifecycleDecision.Denied("user_inactive");
        }

        // Keep the user, organization, and membership decision in one SQL
        // statement. Besides reducing round trips, this prevents a decision
        // from being assembled from lifecycle states observed at three
        // different points during concurrent offboarding.
        var snapshot = await context.Set<SqlOSUser>()
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(user => new
            {
                UserIsActive = user.IsActive,
                OrganizationIsActive = context.Set<SqlOSOrganization>()
                    .Any(organization => organization.Id == organizationId && organization.IsActive),
                MembershipIsActive = context.Set<SqlOSMembership>()
                    .Any(membership => membership.UserId == userId
                        && membership.OrganizationId == organizationId
                        && membership.IsActive)
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null || !snapshot.UserIsActive)
        {
            return SqlOSAuthLifecycleDecision.Denied("user_inactive");
        }

        if (!snapshot.OrganizationIsActive)
        {
            return SqlOSAuthLifecycleDecision.Denied("organization_inactive");
        }

        return snapshot.MembershipIsActive
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

    internal static async Task RevokeAsync(
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

        var temporaryTokensQuery = context.Set<SqlOSTemporaryToken>()
            .Where(x => SessionIssuanceTemporaryTokenPurposes.Contains(x.Purpose)
                && x.ConsumedAt == null);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            temporaryTokensQuery = temporaryTokensQuery.Where(x => x.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            temporaryTokensQuery = temporaryTokensQuery.Where(x => x.OrganizationId == organizationId);
        }

        var temporaryTokens = await temporaryTokensQuery.ToListAsync(cancellationToken);

        var authorizationCodesQuery = context.Set<SqlOSAuthorizationCode>()
            .Where(x => x.ConsumedAt == null);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            authorizationCodesQuery = authorizationCodesQuery.Where(x => x.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            authorizationCodesQuery = authorizationCodesQuery.Where(x => x.OrganizationId == organizationId);
        }

        var authorizationCodes = await authorizationCodesQuery.ToListAsync(cancellationToken);

        var deviceAuthorizationsQuery = context.Set<SqlOSDeviceAuthorization>()
            .Where(x => x.ConsumedAt == null && x.Status == SqlOSDeviceAuthorizationService.ApprovedStatus);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            deviceAuthorizationsQuery = deviceAuthorizationsQuery.Where(x => x.ApprovedUserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            deviceAuthorizationsQuery = deviceAuthorizationsQuery.Where(x => x.ApprovedOrganizationId == organizationId);
        }

        var deviceAuthorizations = await deviceAuthorizationsQuery.ToListAsync(cancellationToken);

        var emailOtpChallengesQuery = context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.ConsumedAt == null && x.InvalidatedAt == null);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            emailOtpChallengesQuery = emailOtpChallengesQuery.Where(x => x.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            emailOtpChallengesQuery = emailOtpChallengesQuery.Where(x => x.RequestedOrganizationId == organizationId);
        }

        var emailOtpChallenges = await emailOtpChallengesQuery.ToListAsync(cancellationToken);

        var phoneOtpChallengesQuery = context.Set<SqlOSPhoneOtpChallenge>()
            .Where(x => x.ConsumedAt == null && x.InvalidatedAt == null);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            phoneOtpChallengesQuery = phoneOtpChallengesQuery.Where(x => x.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            phoneOtpChallengesQuery = phoneOtpChallengesQuery.Where(x => x.RequestedOrganizationId == organizationId);
        }

        var phoneOtpChallenges = await phoneOtpChallengesQuery.ToListAsync(cancellationToken);

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

        foreach (var temporaryToken in temporaryTokens)
        {
            temporaryToken.ConsumedAt = now;
        }

        foreach (var authorizationCode in authorizationCodes)
        {
            authorizationCode.ConsumedAt = now;
        }

        foreach (var deviceAuthorization in deviceAuthorizations)
        {
            deviceAuthorization.Status = SqlOSDeviceAuthorizationService.DeniedStatus;
            deviceAuthorization.DeniedAt = now;
        }

        foreach (var emailOtpChallenge in emailOtpChallenges)
        {
            emailOtpChallenge.InvalidatedAt = now;
            emailOtpChallenge.InvalidatedReason = reason;
        }

        foreach (var phoneOtpChallenge in phoneOtpChallenges)
        {
            phoneOtpChallenge.InvalidatedAt = now;
            phoneOtpChallenge.InvalidatedReason = reason;
        }
    }

    internal static Task RevokeForDenialAsync(
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
            refreshToken.ReplacementTokenResponse = null;
            refreshToken.ReplacementOrganizationId = null;
            refreshToken.ReplacementAccessTokenExpiresAt = null;
        }
    }
}

internal sealed record SqlOSAuthLifecycleDecision(bool IsActive, string? Reason)
{
    internal static SqlOSAuthLifecycleDecision Active { get; } = new(true, null);

    internal static SqlOSAuthLifecycleDecision Denied(string reason) => new(false, reason);
}
