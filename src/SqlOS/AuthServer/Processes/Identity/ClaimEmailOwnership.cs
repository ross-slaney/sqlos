using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// The claim of an unverified address, as a step of every identity process that proves a mailbox
/// (email code, sign-in link, password reset, invitation, upstream OpenID, SAML). The
/// <see cref="SqlOSUser"/> aggregate revokes what the account owned before the proof
/// (<see cref="SqlOSUser.ClaimWithProof"/>); this step revokes what reaches across aggregates in
/// the same unit of work: the account's remembered consent grants, its own calendar connections,
/// and every session and token (through <see cref="SqlOSAuthLifecyclePolicy"/> until layer 3 owns
/// revocation).
/// </summary>
/// <remarks>
/// <para>
/// Everything is staged on the caller's unit of work, so the claim commits atomically with the
/// sign-in that proved the mailbox, and one <c>user.email.claimed</c> audit row (7.2.1's row,
/// byte for byte) names what was revoked. A proof for another mailbox is refused before anything is
/// staged, and a verified address is never claimed again.
/// </para>
/// <para>
/// A consent grant is an approval someone gave while signed in to the account: a squatter's
/// approval of its own client would otherwise let that client silently get codes for the owner's
/// session. A calendar connection attached before the claim may point at the squatter's calendar.
/// Organization memberships are kept: organizations control them, so an organization admin reviews
/// the memberships of a claimed account.
/// </para>
/// </remarks>
internal static class ClaimEmailOwnership
{
    /// <summary>The audit event 7.2.1 writes for a claim.</summary>
    public const string AuditEventType = "user.email.claimed";

    /// <summary>The reason a claim records on what it revokes.</summary>
    public const string RevocationReason = SqlOSUser.EmailClaimedReason;

    /// <summary>
    /// Stages the claim of the address <paramref name="proof"/> proves on <paramref name="user"/>,
    /// when that address is unverified. Loads whatever parts of the account the claim needs.
    /// </summary>
    /// <param name="context">The unit of work the caller saves.</param>
    /// <param name="user">The tracked account.</param>
    /// <param name="proof">A sign-in's proof of the mailbox.</param>
    /// <param name="presented">The credential the proving flow presented, which the claim keeps.</param>
    /// <param name="now">When the claim happens.</param>
    /// <param name="cancellationToken">Cancels the loads.</param>
    /// <param name="sessionRevocationReason">The reason recorded on the revoked sessions (a reset records <c>password_reset</c>).</param>
    public static async Task<EmailClaimOutcome> StageAsync(
        ISqlOSAuthServerDbContext context,
        SqlOSUser user,
        OwnershipProof proof,
        PresentedCredentials presented,
        DateTime now,
        CancellationToken cancellationToken,
        string sessionRevocationReason = RevocationReason)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(presented);

        await context.LoadUserPartsAsync(user, SqlOSUserParts.Emails, cancellationToken);
        if (!user.IsClaimableWith(proof))
        {
            return EmailClaimOutcome.NotClaimed;
        }

        var snapshot = SqlOSTrackedChangeSnapshot.Capture(context);
        await context.LoadUserPartsAsync(user, SqlOSUserParts.Claim, cancellationToken);

        var consentGrants = await context.Set<SqlOSConsentGrant>()
            .Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var grant in consentGrants)
        {
            grant.Revoke(RevocationReason, now);
        }

        // Destroys the stored provider tokens and writes the calendar.connection.disconnected audit.
        var calendarConnections = await SqlOSAuthLifecyclePolicy.RevokeCalendarConnectionsAsync(
            context,
            user.Id,
            organizationId: null,
            RevocationReason,
            now,
            cancellationToken);

        await SqlOSAuthLifecyclePolicy.RevokeAsync(
            context,
            user.Id,
            organizationId: null,
            sessionRevocationReason,
            now,
            cancellationToken: cancellationToken);

        var claim = user.ClaimWithProof(
            proof,
            presented,
            new EmailClaimEvictions(consentGrants.Select(x => x.Id), calendarConnections.Select(x => x.Id)),
            now);

        // The aggregate unlinked these identities; their rows go with the claim.
        context.Set<SqlOSExternalIdentity>().RemoveRange(claim.UnlinkedIdentities);
        return new EmailClaimOutcome(true, snapshot);
    }
}

/// <summary>Whether a claim was staged, and how to discard it if the flow cannot complete.</summary>
internal sealed record EmailClaimOutcome(bool Claimed, SqlOSTrackedChangeSnapshot? Snapshot)
{
    public static EmailClaimOutcome NotClaimed { get; } = new(false, null);

    /// <summary>
    /// Discards the staged claim when the sign-in that proved the mailbox cannot complete, so a
    /// later save on the same unit of work never persists a half-applied claim or its audit row.
    /// </summary>
    public void Revert(ISqlOSAuthServerDbContext context) => Snapshot?.Revert(context);
}
