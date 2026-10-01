using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// What a claiming sign-in presented, and therefore keeps, when the first proof of an unverified
/// address claims it: the credential being set by the proof (a password reset), or the credentials
/// used to sign in to the same flow (an invitation accepted after signing in).
/// </summary>
/// <remarks>
/// Everything else attached to the account before the owner proved the mailbox is evicted by
/// <see cref="SqlOSUser.ClaimWithProof"/>. Unknown or missing methods keep nothing, so a mistake
/// evicts too much, never too little.
/// </remarks>
internal sealed record PresentedCredentials
{
    /// <summary>The proof was the email itself (code, link, reset token, invitation token).</summary>
    public static PresentedCredentials None { get; } = new();

    /// <summary>The password credential being set by this proof (a password reset).</summary>
    public string? PasswordCredentialId { get; init; }

    /// <summary>The account's password was used to authenticate this same flow.</summary>
    public bool KeepPasswordCredentials { get; init; }

    public bool KeepAuthenticators { get; init; }

    public bool KeepPhoneNumbers { get; init; }

    public bool KeepOidcIdentities { get; init; }

    public bool KeepSamlIdentities { get; init; }

    /// <summary>The password whose reset proves the mailbox; it is kept and replaced.</summary>
    public static PresentedCredentials PasswordBeingReset(string credentialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        return new PresentedCredentials { PasswordCredentialId = credentialId };
    }

    /// <summary>
    /// Maps the authentication method of the flow that also presented an email proof (an
    /// invitation accepted after signing in) to the credentials it used.
    /// </summary>
    public static PresentedCredentials FromAuthenticationMethod(string? authenticationMethod)
    {
        var presented = new PresentedCredentials();
        foreach (var method in SqlOSMfaPolicyService.SplitAuthenticationMethods(authenticationMethod))
        {
            presented = method.ToLowerInvariant() switch
            {
                "password" => presented with { KeepPasswordCredentials = true },
                "phone_otp" => presented with { KeepPhoneNumbers = true },
                SqlOSMfaFactorTypes.Totp or SqlOSMfaFactorTypes.RecoveryCode => presented with { KeepAuthenticators = true },
                "google" or "microsoft" or "apple" or "github" or "oidc" => presented with { KeepOidcIdentities = true },
                "saml" => presented with { KeepSamlIdentities = true },
                _ => presented
            };
        }

        return presented;
    }

    internal bool KeepsCredential(SqlOSCredential credential)
        => KeepPasswordCredentials
            || string.Equals(credential.Id, PasswordCredentialId, StringComparison.Ordinal);

    internal bool KeepsIdentity(SqlOSExternalIdentity identity)
        => (KeepOidcIdentities && identity.OidcConnectionId != null)
            || (KeepSamlIdentities && identity.SsoConnectionId != null);
}

/// <summary>
/// What a claim revoked outside the <see cref="SqlOSUser"/> aggregate: the account's remembered
/// consent grants and its own calendar connections. Its sessions and tokens are revoked too, but
/// 7.2.1's audit row does not list them.
/// </summary>
/// <remarks>
/// A proof that the claim process (<c>ClaimEmailOwnership</c>) evicted the other aggregates'
/// grants: only it constructs one (<c>proof-producers.txt</c>), and
/// <see cref="SqlOSUser.ClaimWithProof"/> requires one, so the user-owned half of a claim can never
/// run without the half that reaches across aggregates.
/// </remarks>
internal sealed class EmailClaimEvictions : ISqlOSProof
{
    internal EmailClaimEvictions(IEnumerable<string> consentGrantIds, IEnumerable<string> calendarConnectionIds)
    {
        ArgumentNullException.ThrowIfNull(consentGrantIds);
        ArgumentNullException.ThrowIfNull(calendarConnectionIds);
        ConsentGrantIds = consentGrantIds.ToArray();
        CalendarConnectionIds = calendarConnectionIds.ToArray();
    }

    public IReadOnlyList<string> ConsentGrantIds { get; }

    public IReadOnlyList<string> CalendarConnectionIds { get; }
}

/// <summary>
/// A claim the user aggregate made: the address it verified and the external identities it
/// unlinked, whose rows the claim process deletes in the same save.
/// </summary>
internal sealed record SqlOSUserEmailClaim(SqlOSUserEmail Email, IReadOnlyList<SqlOSExternalIdentity> UnlinkedIdentities);

/// <summary>
/// The parts of a <see cref="SqlOSUser"/> loaded into memory. A rule that must see every member
/// of a part (a claim evicts every credential; one address is primary) refuses to decide unless
/// that part was loaded, so a forgotten load fails loudly instead of skipping members.
/// </summary>
/// <remarks>
/// Loading a part loads its live members: unrevoked credentials and authenticators, unspent and
/// unrevoked recovery codes, phone numbers that were not removed, every address and external
/// identity, and the MFA settings. Members that are no longer live may also be in memory (EF Core
/// fixes up rows other queries loaded); the aggregate's rules look only at live ones.
/// </remarks>
[Flags]
internal enum SqlOSUserParts
{
    None = 0,
    Emails = 1,
    PhoneNumbers = 2,
    Credentials = 4,
    Authenticators = 8,
    RecoveryCodes = 16,
    ExternalIdentities = 32,
    MfaPolicyOverride = 64,

    /// <summary>Everything a claim revokes or keeps.</summary>
    Claim = Emails | PhoneNumbers | Credentials | Authenticators | RecoveryCodes | ExternalIdentities,

    All = Claim | MfaPolicyOverride
}
