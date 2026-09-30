using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// The single rule for an unverified email that a sign-in path has just proven: the proof claims
/// the address. Anyone could have attached that address to the account before its owner proved
/// it (a squatter's password, an upstream identity that never verified the address, an
/// authenticator), so the claim evicts everything attached before it, except the credential
/// being presented now, revokes remembered consent grants, user-owned calendar connections, and
/// every session and token, marks the email verified, and records one <c>user.email.claimed</c>
/// audit event naming what was revoked. All of it is staged on the caller's unit of work so it
/// commits atomically with the sign-in that proved the mailbox.
/// </summary>
/// <remarks>
/// The explicit "verify this email" link is the signup confirmation step and is deliberately not
/// a claim: it only marks the address verified. Verified addresses are never claimed again.
/// Organization memberships are kept: organizations control them, so an organization admin
/// reviews the memberships of a claimed account.
/// </remarks>
internal static class SqlOSEmailOwnershipClaim
{
    public const string AuditEventType = "user.email.claimed";
    public const string RevocationReason = "email_claimed";

    public static async Task<SqlOSEmailClaimOutcome> ClaimAsync(
        ISqlOSAuthServerDbContext context,
        SqlOSUserEmail email,
        string proof,
        SqlOSEmailClaimPresentation presented,
        DateTime now,
        CancellationToken cancellationToken,
        string sessionRevocationReason = RevocationReason)
    {
        if (email.IsVerified)
        {
            return SqlOSEmailClaimOutcome.NotClaimed;
        }

        var snapshot = SqlOSTrackedChangeSnapshot.Capture(context);
        var userId = email.UserId;
        var passwords = (await context.Set<SqlOSCredential>()
                .Where(x => x.UserId == userId && x.RevokedAt == null)
                .ToListAsync(cancellationToken))
            .Where(x => !presented.KeepsCredential(x))
            .ToList();
        var authenticators = presented.KeepAuthenticators
            ? []
            : await context.Set<SqlOSUserAuthenticator>()
                .Where(x => x.UserId == userId && x.RevokedAt == null)
                .ToListAsync(cancellationToken);
        var recoveryCodes = presented.KeepAuthenticators
            ? []
            : await context.Set<SqlOSRecoveryCode>()
                .Where(x => x.UserId == userId && x.RevokedAt == null && x.ConsumedAt == null)
                .ToListAsync(cancellationToken);
        var phoneNumbers = presented.KeepPhoneNumbers
            ? []
            : await context.Set<SqlOSUserPhoneNumber>()
                .Where(x => x.UserId == userId && x.RemovedAt == null)
                .ToListAsync(cancellationToken);
        var identities = (await context.Set<SqlOSExternalIdentity>()
                .Where(x => x.UserId == userId)
                .ToListAsync(cancellationToken))
            .Where(x => !presented.KeepsIdentity(x))
            .ToList();

        foreach (var password in passwords)
        {
            password.RevokedAt = now;
        }

        foreach (var authenticator in authenticators)
        {
            authenticator.RevokedAt = now;
            authenticator.RevocationReason = RevocationReason;
        }

        foreach (var recoveryCode in recoveryCodes)
        {
            recoveryCode.RevokedAt = now;
        }

        foreach (var phoneNumber in phoneNumbers)
        {
            phoneNumber.RemovedAt = now;
            phoneNumber.RemovalReason = RevocationReason;
            phoneNumber.UpdatedAt = now;
        }

        context.Set<SqlOSExternalIdentity>().RemoveRange(identities);

        // Remembered consent is an approval someone gave while signed in to this account. A
        // squatter's approval of its own client would otherwise let that client silently get
        // codes for the owner's session (prompt=none or a covering grant skips the consent screen).
        var consentGrants = await context.Set<SqlOSConsentGrant>()
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var grant in consentGrants)
        {
            grant.RevokedAt = now;
            grant.UpdatedAt = now;
            grant.RevocationReason = RevocationReason;
        }

        // A calendar connection attached before the claim may point at the squatter's own
        // calendar, so the app could sync the owner's events into it. The helper destroys the
        // stored provider tokens and writes its own calendar.connection.disconnected audit.
        var calendarConnections = await SqlOSAuthLifecyclePolicy.RevokeCalendarConnectionsAsync(
            context,
            userId,
            organizationId: null,
            RevocationReason,
            now,
            cancellationToken);

        await SqlOSAuthLifecyclePolicy.RevokeAsync(
            context,
            userId,
            organizationId: null,
            sessionRevocationReason,
            now,
            cancellationToken: cancellationToken);

        email.IsVerified = true;
        email.VerifiedAt = now;

        var data = JsonSerializer.Serialize(new
        {
            emailId = email.Id,
            proof,
            revoked = new
            {
                passwordCredentialIds = passwords.Select(x => x.Id).ToArray(),
                authenticatorIds = authenticators.Select(x => x.Id).ToArray(),
                recoveryCodes = recoveryCodes.Count,
                phoneNumberIds = phoneNumbers.Select(x => x.Id).ToArray(),
                externalIdentities = identities
                    .Select(x => new
                    {
                        id = x.Id,
                        kind = x.OidcConnectionId != null ? "oidc" : "saml",
                        connectionId = x.OidcConnectionId ?? x.SsoConnectionId
                    })
                    .ToArray(),
                consentGrantIds = consentGrants.Select(x => x.Id).ToArray(),
                calendarConnectionIds = calendarConnections.Select(x => x.Id).ToArray()
            }
        });
        context.Set<SqlOSAuditEvent>().Add(new SqlOSAuditEvent
        {
            Id = $"evt_{Guid.NewGuid():N}"[..28],
            EventType = AuditEventType,
            Source = "authserver",
            ActorType = "user",
            ActorId = userId,
            UserId = userId,
            TargetsJson = JsonSerializer.Serialize(new[] { new { type = "user", id = userId } }),
            OccurredAt = now,
            IngestedAt = now,
            MetadataJson = data,
            DataJson = data
        });

        return new SqlOSEmailClaimOutcome(true, snapshot);
    }
}

/// <summary>What a claiming sign-in presents, and therefore keeps, when it claims an email.</summary>
internal sealed record SqlOSEmailClaimPresentation
{
    /// <summary>The proof was the email itself (code, link, reset token, invitation token).</summary>
    public static SqlOSEmailClaimPresentation None { get; } = new();

    /// <summary>The password credential being set by this proof (password reset).</summary>
    public string? PasswordCredentialId { get; init; }

    /// <summary>The account's password was used to authenticate this same flow.</summary>
    public bool KeepPasswordCredentials { get; init; }

    public bool KeepAuthenticators { get; init; }

    public bool KeepPhoneNumbers { get; init; }

    public bool KeepOidcIdentities { get; init; }

    public bool KeepSamlIdentities { get; init; }

    /// <summary>
    /// Maps the authentication method of the flow that also presented an email proof (an
    /// invitation accepted after signing in) to the credentials it used. Unknown or missing
    /// methods keep nothing.
    /// </summary>
    public static SqlOSEmailClaimPresentation FromAuthenticationMethod(string? authenticationMethod)
    {
        var presentation = new SqlOSEmailClaimPresentation();
        foreach (var method in SqlOSMfaPolicyService.SplitAuthenticationMethods(authenticationMethod))
        {
            presentation = method.ToLowerInvariant() switch
            {
                "password" => presentation with { KeepPasswordCredentials = true },
                "phone_otp" => presentation with { KeepPhoneNumbers = true },
                SqlOSMfaFactorTypes.Totp or SqlOSMfaFactorTypes.RecoveryCode => presentation with { KeepAuthenticators = true },
                "google" or "microsoft" or "apple" or "github" or "oidc" => presentation with { KeepOidcIdentities = true },
                "saml" => presentation with { KeepSamlIdentities = true },
                _ => presentation
            };
        }

        return presentation;
    }

    internal bool KeepsCredential(SqlOSCredential credential)
        => KeepPasswordCredentials
            || string.Equals(credential.Id, PasswordCredentialId, StringComparison.Ordinal);

    internal bool KeepsIdentity(SqlOSExternalIdentity identity)
        => (KeepOidcIdentities && identity.OidcConnectionId != null)
            || (KeepSamlIdentities && identity.SsoConnectionId != null);
}

internal sealed record SqlOSEmailClaimOutcome(bool Claimed, SqlOSTrackedChangeSnapshot? Snapshot)
{
    public static SqlOSEmailClaimOutcome NotClaimed { get; } = new(false, null);

    /// <summary>
    /// Discards the staged claim when the sign-in that proved the mailbox cannot complete, so a
    /// later save on the same unit of work never persists a half-applied claim.
    /// </summary>
    public void Revert(ISqlOSAuthServerDbContext context) => Snapshot?.Revert(context);
}

/// <summary>
/// Records the change-tracker state before an operation so everything staged after it can be
/// discarded later without touching changes the caller had already staged before it.
/// </summary>
internal sealed class SqlOSTrackedChangeSnapshot
{
    private readonly HashSet<object> _tracked;
    private readonly HashSet<object> _pending;

    private SqlOSTrackedChangeSnapshot(HashSet<object> tracked, HashSet<object> pending)
    {
        _tracked = tracked;
        _pending = pending;
    }

    public static SqlOSTrackedChangeSnapshot? Capture(ISqlOSAuthServerDbContext context)
    {
        if (context is not DbContext dbContext)
        {
            return null;
        }

        var tracked = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var entry in dbContext.ChangeTracker.Entries())
        {
            tracked.Add(entry.Entity);
            if (entry.State != EntityState.Unchanged)
            {
                pending.Add(entry.Entity);
            }
        }

        return new SqlOSTrackedChangeSnapshot(tracked, pending);
    }

    public void Revert(ISqlOSAuthServerDbContext context)
    {
        if (context is not DbContext dbContext)
        {
            return;
        }

        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            if (_pending.Contains(entry.Entity))
            {
                continue;
            }

            if (entry.State == EntityState.Added && !_tracked.Contains(entry.Entity))
            {
                entry.State = EntityState.Detached;
            }
            else if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
            }
        }
    }
}
