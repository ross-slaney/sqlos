namespace SqlOS.Domain.Events;

// The User aggregate (SqlOSUser) and what it owns. Three of these actions were audited in 7.2.1,
// and their events carry exactly what those rows recorded: the claim of an unverified address
// (user.email.claimed), the email-verification link (user.email-verified) and an account
// provisioned by an upstream OpenID provider (user.login.oidc.provisioned). The other events are
// unaudited, as their actions were in 7.2.1, until #415 adds rows for them in layer 5.

/// <summary>An account was registered.</summary>
internal sealed record UserRegistered(string UserId) : ISqlOSDomainEvent;

/// <summary>
/// An upstream OpenID provider's first sign-in created the account
/// (<c>user.login.oidc.provisioned</c>).
/// </summary>
internal sealed record UserProvisionedFromOidc(string UserId, string Provider, string OidcConnectionId) : ISqlOSDomainEvent;

/// <summary>The account's display name or default email changed.</summary>
internal sealed record UserProfileChanged(string UserId) : ISqlOSDomainEvent;

/// <summary>The account was deactivated: it can no longer sign in or use its sessions.</summary>
internal sealed record UserDeactivated(string UserId, string Reason) : ISqlOSDomainEvent;

/// <summary>The account was reactivated.</summary>
internal sealed record UserReactivated(string UserId) : ISqlOSDomainEvent;

/// <summary>An address was added to the account, already verified when <paramref name="Verified"/>.</summary>
internal sealed record UserEmailAdded(string UserId, string EmailId, bool Verified) : ISqlOSDomainEvent;

/// <summary>The account's owner confirmed an address with the email-verification link (<c>user.email-verified</c>).</summary>
internal sealed record UserEmailVerified(string UserId, string EmailId) : ISqlOSDomainEvent;

/// <summary>A directory set the account's primary, verified address.</summary>
internal sealed record UserPrimaryEmailSet(string UserId, string EmailId) : ISqlOSDomainEvent;

/// <summary>
/// The first proof of an unverified address claimed it, evicting everything attached to the
/// account before the owner proved the mailbox (<c>user.email.claimed</c>). It names what was
/// revoked inside the account and, through the claim process, the consent grants and calendar
/// connections outside it. <paramref name="ClaimedAt"/> is the instant 7.2.1 recorded the claim.
/// </summary>
internal sealed record UserEmailClaimed(
    string UserId,
    string EmailId,
    string Proof,
    IReadOnlyList<string> PasswordCredentialIds,
    IReadOnlyList<string> AuthenticatorIds,
    int RecoveryCodes,
    IReadOnlyList<string> PhoneNumberIds,
    IReadOnlyList<UnlinkedExternalIdentity> ExternalIdentities,
    IReadOnlyList<string> ConsentGrantIds,
    IReadOnlyList<string> CalendarConnectionIds,
    DateTime ClaimedAt) : ISqlOSDomainEvent;

/// <summary>An external identity a claim unlinked: its id, <c>oidc</c> or <c>saml</c>, and its connection.</summary>
internal sealed record UnlinkedExternalIdentity(string Id, string Kind, string? ConnectionId);

/// <summary>A password was set on the account (at registration or by a reset).</summary>
internal sealed record UserPasswordSet(string UserId, string CredentialId) : ISqlOSDomainEvent;

/// <summary>The account's password signed it in.</summary>
internal sealed record UserPasswordUsed(string UserId, string CredentialId) : ISqlOSDomainEvent;

/// <summary>A phone number was verified for the account (added, or verified again).</summary>
internal sealed record UserPhoneNumberVerified(string UserId, string PhoneNumberId) : ISqlOSDomainEvent;

/// <summary>A phone code sent to the account's number signed it in.</summary>
internal sealed record UserPhoneNumberUsed(string UserId, string PhoneNumberId) : ISqlOSDomainEvent;

/// <summary>An authenticator-app enrollment started: the authenticator waits for its first code.</summary>
internal sealed record UserTotpEnrollmentStarted(string UserId, string AuthenticatorId) : ISqlOSDomainEvent;

/// <summary>An authenticator's first code confirmed it.</summary>
internal sealed record UserTotpConfirmed(string UserId, string AuthenticatorId) : ISqlOSDomainEvent;

/// <summary>An authenticator's code was accepted for a time step not used before.</summary>
internal sealed record UserTotpCodeAccepted(string UserId, string AuthenticatorId) : ISqlOSDomainEvent;

/// <summary>An authenticator was revoked for <paramref name="Reason"/>.</summary>
internal sealed record UserAuthenticatorRevoked(string UserId, string AuthenticatorId, string Reason) : ISqlOSDomainEvent;

/// <summary>A new set of recovery codes replaced the unused ones.</summary>
internal sealed record UserRecoveryCodesIssued(string UserId, int Count) : ISqlOSDomainEvent;

/// <summary>A recovery code was spent.</summary>
internal sealed record UserRecoveryCodeUsed(string UserId, string RecoveryCodeId) : ISqlOSDomainEvent;

/// <summary>The account opted into MFA by confirming an authenticator.</summary>
internal sealed record UserOptedIntoMfa(string UserId) : ISqlOSDomainEvent;

/// <summary>An external identity was linked to the account; <paramref name="Kind"/> is <c>oidc</c> or <c>saml</c>.</summary>
internal sealed record UserExternalIdentityLinked(string UserId, string ExternalIdentityId, string Kind, string ConnectionId) : ISqlOSDomainEvent;
