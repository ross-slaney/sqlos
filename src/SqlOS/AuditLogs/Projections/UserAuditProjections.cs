using System.Text.Json;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuditLogs.Projections;

/// <summary>
/// The audit rows of the User aggregate. 7.2.1 audited three of its actions; their rows are
/// reproduced exactly. Every other user event is registered unaudited, as its action was in 7.2.1,
/// until #415 adds rows for them in layer 5.
/// </summary>
internal static class UserAuditProjections
{
    public static SqlOSAuditProjectionBuilder AddUserEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            .Unaudited<UserRegistered>()
            .Unaudited<UserProfileChanged>()
            .Unaudited<UserDeactivated>()
            .Unaudited<UserReactivated>()
            .Unaudited<UserEmailAdded>()
            .Unaudited<UserPrimaryEmailSet>()
            .Unaudited<UserPasswordSet>()
            .Unaudited<UserPasswordUsed>()
            .Unaudited<UserPhoneNumberVerified>()
            .Unaudited<UserPhoneNumberUsed>()
            .Unaudited<UserTotpEnrollmentStarted>()
            .Unaudited<UserTotpConfirmed>()
            .Unaudited<UserTotpCodeAccepted>()
            .Unaudited<UserAuthenticatorRevoked>()
            .Unaudited<UserRecoveryCodesIssued>()
            .Unaudited<UserRecoveryCodeUsed>()
            .Unaudited<UserOptedIntoMfa>()
            .Unaudited<UserExternalIdentityLinked>()
            // SqlOSAdminService.RecordAuditAsync("user.email-verified", "user", userId, userId: userId)
            .Audit<UserEmailVerified>(static (verified, context) => AuthServerAuditRows.User(
                "user.email-verified",
                verified.UserId,
                context))
            // SqlOSAdminService.RecordAuditAsync("user.login.oidc.provisioned", "user", userId, userId: userId, data: { provider, oidcConnectionId })
            .Audit<UserProvisionedFromOidc>(static (provisioned, context) => AuthServerAuditRows.User(
                "user.login.oidc.provisioned",
                provisioned.UserId,
                context,
                data: new
                {
                    provider = provisioned.Provider,
                    oidcConnectionId = provisioned.OidcConnectionId
                }))
            .Audit<UserEmailClaimed>(static (claimed, _) => ClaimRow(claimed));

    /// <summary>
    /// The row 7.2.1's <c>SqlOSEmailOwnershipClaim</c> added for a claim, built the way it built
    /// it: default JSON options, a <c>user</c> target, and both timestamps at the instant of the
    /// claim, so the row reads and sorts exactly as before. It does not go through the shared
    /// metadata sanitizer, which never touched it: the sanitizer would redact
    /// <c>passwordCredentialIds</c>.
    /// </summary>
    private static SqlOSAuditEvent ClaimRow(UserEmailClaimed claimed)
    {
        var data = JsonSerializer.Serialize(new
        {
            emailId = claimed.EmailId,
            proof = claimed.Proof,
            revoked = new
            {
                passwordCredentialIds = claimed.PasswordCredentialIds.ToArray(),
                authenticatorIds = claimed.AuthenticatorIds.ToArray(),
                recoveryCodes = claimed.RecoveryCodes,
                phoneNumberIds = claimed.PhoneNumberIds.ToArray(),
                externalIdentities = claimed.ExternalIdentities
                    .Select(identity => new
                    {
                        id = identity.Id,
                        kind = identity.Kind,
                        connectionId = identity.ConnectionId
                    })
                    .ToArray(),
                consentGrantIds = claimed.ConsentGrantIds.ToArray(),
                calendarConnectionIds = claimed.CalendarConnectionIds.ToArray()
            }
        });
        return new SqlOSAuditEvent
        {
            Id = SqlOSIds.New("evt"),
            EventType = "user.email.claimed",
            Source = "authserver",
            ActorType = "user",
            ActorId = claimed.UserId,
            UserId = claimed.UserId,
            TargetsJson = JsonSerializer.Serialize(new[] { new { type = "user", id = claimed.UserId } }),
            OccurredAt = claimed.ClaimedAt,
            IngestedAt = claimed.ClaimedAt,
            MetadataJson = data,
            DataJson = data
        };
    }
}
