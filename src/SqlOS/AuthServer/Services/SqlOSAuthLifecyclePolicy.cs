using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Calendar.Models;

namespace SqlOS.AuthServer.Services;

internal static class SqlOSAuthLifecyclePolicy
{
    internal const string DeniedEventType = "auth.lifecycle.denied";
    internal const string CalendarConnectionRevokedEventType = "calendar.connection.disconnected";
    internal const string IssuerSessionPurpose = "auth_page_session";
    private static readonly string[] SessionIssuanceTemporaryTokenPurposes =
    [
        IssuerSessionPurpose,
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

    /// <summary>
    /// Lifecycle decision for an organization-owned artifact that has no user subject,
    /// such as an organization calendar connection.
    /// </summary>
    internal static async Task<SqlOSAuthLifecycleDecision> EvaluateOrganizationAsync(
        ISqlOSAuthServerDbContext context,
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        var organizationIsActive = await context.Set<SqlOSOrganization>()
            .AsNoTracking()
            .AnyAsync(x => x.Id == organizationId && x.IsActive, cancellationToken);
        return organizationIsActive
            ? SqlOSAuthLifecycleDecision.Active
            : SqlOSAuthLifecycleDecision.Denied("organization_inactive");
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
        SqlOSAuthLifecycleRevocationScope scope = SqlOSAuthLifecycleRevocationScope.SessionsAndTokens,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) && string.IsNullOrWhiteSpace(organizationId))
        {
            throw new ArgumentException("A user or organization scope is required for lifecycle revocation.");
        }

        if (scope == SqlOSAuthLifecycleRevocationScope.Offboarding)
        {
            await RevokeCalendarConnectionsAsync(context, userId, organizationId, reason, now, cancellationToken);
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

        var issuerSessionFamiliesQuery = context.Set<SqlOSIssuerSessionFamily>()
            .Where(x => x.RevokedAt == null);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            issuerSessionFamiliesQuery = issuerSessionFamiliesQuery.Where(x => x.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            issuerSessionFamiliesQuery = issuerSessionFamiliesQuery.Where(x => x.OrganizationId == organizationId);
        }

        var issuerSessionFamilies = await issuerSessionFamiliesQuery.ToListAsync(cancellationToken);

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

        foreach (var family in issuerSessionFamilies)
        {
            family.RevokedAt = now;
            family.RevocationReason = reason;
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
                scope: SqlOSAuthLifecycleRevocationScope.Offboarding,
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
                scope: SqlOSAuthLifecycleRevocationScope.Offboarding,
                cancellationToken: cancellationToken);
        }

        return RevokeAsync(context, userId, organizationId, decision.Reason ?? "lifecycle_invalid", now, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Revokes every calendar connection owned by the user and/or organization scope and
    /// destroys the stored provider credential material. The change is only staged on the
    /// context so callers commit it together with the rest of the offboarding work; the
    /// connection's <see cref="SqlOSCalendarConnection.RevokedAt"/> concurrency token makes a
    /// racing refresh or sync fail closed instead of writing tokens back onto a revoked row.
    /// Already-revoked connections are skipped, so retries are idempotent and audit once.
    /// </summary>
    internal static async Task<IReadOnlyList<SqlOSCalendarConnection>> RevokeCalendarConnectionsAsync(
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

        var query = context.Set<SqlOSCalendarConnection>()
            .Where(x => x.RevokedAt == null);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            query = query.Where(x => x.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            query = query.Where(x => x.OrganizationId == organizationId);
        }

        var connections = await query.ToListAsync(cancellationToken);
        foreach (var connection in connections)
        {
            connection.Status = SqlOSCalendarConnectionStatus.Revoked;
            connection.AccessTokenEncrypted = null;
            connection.RefreshTokenEncrypted = null;
            connection.AccessTokenExpiresAt = null;
            connection.RevokedAt = now;
            connection.RevokedReason = reason;
            connection.UpdatedAt = now;

            // Same event type and actor shape as an explicit dashboard/service disconnect so
            // audit consumers see one stream; the reason tells offboarding apart.
            var metadataJson = JsonSerializer.Serialize(new
            {
                reason,
                provider = connection.ProviderType.ToString(),
                lifecycle = true
            });
            context.Set<SqlOSAuditEvent>().Add(new SqlOSAuditEvent
            {
                Id = $"evt_{Guid.NewGuid():N}"[..28],
                EventType = CalendarConnectionRevokedEventType,
                Source = "authserver",
                ActorType = "calendar_connection",
                ActorId = connection.Id,
                UserId = connection.UserId,
                OrganizationId = connection.OrganizationId,
                OccurredAt = now,
                IngestedAt = now,
                MetadataJson = metadataJson,
                DataJson = metadataJson
            });
        }

        return connections;
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

/// <summary>
/// How far a lifecycle revocation reaches. <see cref="SessionsAndTokens"/> is the sign-out
/// shape (logout-all, password reset): the person keeps their integrations and simply
/// re-authenticates. <see cref="Offboarding"/> is the deactivation shape: stored integration
/// credentials such as calendar connections are revoked as well and must be reconnected explicitly.
/// </summary>
internal enum SqlOSAuthLifecycleRevocationScope
{
    SessionsAndTokens = 0,
    Offboarding = 1
}

internal sealed record SqlOSAuthLifecycleDecision(bool IsActive, string? Reason)
{
    internal static SqlOSAuthLifecycleDecision Active { get; } = new(true, null);

    internal static SqlOSAuthLifecycleDecision Denied(string reason) => new(false, reason);
}
