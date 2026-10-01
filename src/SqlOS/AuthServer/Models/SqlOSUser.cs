using SqlOS.AuthServer.Policies;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// A person's account: the root of the identity aggregate. It owns the account's addresses,
/// phone numbers, password, authenticator apps, recovery codes, external identities and MFA
/// settings, and every change to them goes through it (<c>docs/architecture/domain-model.md</c> §4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Rules it keeps.</b>
/// </para>
/// <list type="bullet">
/// <item>One address is primary.</item>
/// <item>An address becomes verified only with an <see cref="OwnershipProof"/> of its mailbox:
/// at registration, by the email-verification link (<see cref="VerifyEmail"/>, which confirms
/// without claiming), by a directory inside its organization's verified domains
/// (<see cref="SetPrimaryEmail"/>), or by a sign-in proof, which claims it
/// (<see cref="ClaimWithProof"/>).</item>
/// <item>The first sign-in proof of an unverified address claims it: every password,
/// authenticator, recovery code, phone number and external identity attached before it is
/// evicted, except the credential the proving flow presented, and the claim process evicts the
/// consent grants and calendar connections outside the aggregate in the same save (#420, #422,
/// #423).</item>
/// <item>An external identity joins an existing account only with its provider's proof for one of
/// the account's verified addresses.</item>
/// <item>A password is set only through <see cref="SetPassword"/>, under the
/// <see cref="PasswordPolicy"/>.</item>
/// <item>The account can be deactivated and reactivated (<see cref="Enablement"/> over
/// <see cref="IsActive"/>).</item>
/// </list>
/// <para>
/// <b>Shape.</b> Every column keeps its 7.x name and type with a private setter. The owned
/// collections are read-only views of private lists that EF Core fills through their fields, so
/// host LINQ (<c>Include(user =&gt; user.Emails)</c>, <c>user.Emails.Count</c>) reads as before.
/// <see cref="Memberships"/> and <see cref="Sessions"/> belong to other aggregates and stay plain
/// navigations. A rule that must see every member of a part refuses to decide until the part is
/// loaded (<see cref="SqlOSUserParts"/>); <c>SqlOSUsers</c> loads them. Each change raises a
/// domain event; the ones 7.2.1 audited become the same audit rows in the same save.
/// </para>
/// </remarks>
public sealed class SqlOSUser : ISqlOSAggregate
{
    /// <summary>The reason a claim records on what it revokes.</summary>
    internal const string EmailClaimedReason = "email_claimed";

    /// <summary>The reason a new enrollment records on the unconfirmed authenticator it replaces.</summary>
    internal const string ReplacedUnconfirmedReason = "replaced_unconfirmed";

    private readonly DomainEventBuffer _events = new();
    private List<SqlOSUserEmail> _emails = [];
    private List<SqlOSUserPhoneNumber> _phoneNumbers = [];
    private List<SqlOSCredential> _credentials = [];
    private List<SqlOSUserAuthenticator> _authenticators = [];
    private List<SqlOSRecoveryCode> _recoveryCodes = [];
    private List<SqlOSExternalIdentity> _externalIdentities = [];
    private SqlOSUserParts _loaded;

    private SqlOSUser()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string? DefaultEmail { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public ICollection<SqlOSUserEmail> Emails => _emails.AsReadOnly();
    public ICollection<SqlOSUserPhoneNumber> PhoneNumbers => _phoneNumbers.AsReadOnly();
    public ICollection<SqlOSCredential> Credentials => _credentials.AsReadOnly();
    public ICollection<SqlOSExternalIdentity> ExternalIdentities => _externalIdentities.AsReadOnly();
    public ICollection<SqlOSUserAuthenticator> Authenticators => _authenticators.AsReadOnly();
    public ICollection<SqlOSRecoveryCode> RecoveryCodes => _recoveryCodes.AsReadOnly();
    public SqlOSUserMfaPolicyOverride? MfaPolicyOverride { get; private set; }

    public ICollection<SqlOSMembership> Memberships { get; private set; } = new List<SqlOSMembership>();
    public ICollection<SqlOSSession> Sessions { get; private set; } = new List<SqlOSSession>();

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    internal Enablement Enablement => new(IsActive);

    /// <summary>The parts loaded into memory (see <see cref="SqlOSUserParts"/>).</summary>
    internal SqlOSUserParts LoadedParts => _loaded;

    // ---------------------------------------------------------------------------------------
    // Registration
    // ---------------------------------------------------------------------------------------

    /// <summary>Registers an account without an address (phone sign-up, or a directory user before its primary email).</summary>
    internal static SqlOSUser Register(string displayName, DateTime now)
    {
        var user = new SqlOSUser
        {
            Id = SqlOSIds.New("usr"),
            DisplayName = displayName,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            // A new account holds exactly what it was created with.
            _loaded = SqlOSUserParts.All
        };
        user._events.Raise(new UserRegistered(user.Id));
        return user;
    }

    /// <summary>
    /// Registers an account whose primary address nobody has proven yet (an operator-created
    /// account, a password sign-up). It becomes the default email.
    /// </summary>
    internal static SqlOSUser Register(string displayName, EmailAddress email, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(email);
        var user = Register(displayName, now);
        user.AddEmail(SqlOSUserEmail.Unverified(user.Id, email, isPrimary: true, now));
        user.DefaultEmail = email.Address;
        return user;
    }

    /// <summary>
    /// Registers an account whose primary address <paramref name="proof"/> proved (an email-code
    /// or invitation sign-up, an organization's SAML assertion inside its verified domains). It is
    /// verified from the start and becomes the default email; a new account has nothing to claim.
    /// </summary>
    internal static SqlOSUser Register(string displayName, OwnershipProof proof, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var user = Register(displayName, now);
        user.AddEmail(SqlOSUserEmail.Proven(user.Id, proof, isPrimary: true, now));
        user.DefaultEmail = proof.Address.Address;
        return user;
    }

    /// <summary>
    /// Registers an account for an upstream identity's first sign-in, with the address the provider
    /// asserted but did not verify (a custom OpenID provider may not).
    /// </summary>
    internal static SqlOSUser RegisterFromExternalIdentity(
        string displayName,
        EmailAddress unverifiedEmail,
        ExternalIdentityLink identity,
        DateTime now)
    {
        var user = Register(displayName, unverifiedEmail, now);
        user.LinkFoundingIdentity(identity, now);
        return user;
    }

    /// <summary>
    /// Registers an account for an upstream identity's first sign-in, with the address its
    /// provider proved.
    /// </summary>
    internal static SqlOSUser RegisterFromExternalIdentity(
        string displayName,
        OwnershipProof proof,
        ExternalIdentityLink identity,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.IsProvenBy(proof))
        {
            throw SqlOSDomainException.Of(
                SqlOSDomainError.OwnershipProofMismatch,
                $"An {identity.KindName} identity is registered only with its own provider's proof.");
        }

        var user = Register(displayName, proof, now);
        user.LinkFoundingIdentity(identity, now);
        return user;
    }

    // ---------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------

    /// <summary>The account's address with <paramref name="emailId"/>, or null.</summary>
    internal SqlOSUserEmail? FindEmail(string? emailId)
    {
        Require(SqlOSUserParts.Emails);
        return emailId is null ? null : _emails.FirstOrDefault(email => string.Equals(email.Id, emailId, StringComparison.Ordinal));
    }

    /// <summary>
    /// The account's address <paramref name="proof"/> proves. Fails when it proves none of them:
    /// a proof for another mailbox never touches this account.
    /// </summary>
    internal SqlOSUserEmail EmailProvenBy(OwnershipProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        Require(SqlOSUserParts.Emails);
        return _emails
                .Where(email => email.IsCoveredBy(proof))
                .OrderByDescending(email => email.IsPrimary)
                .ThenBy(email => email.CreatedAt)
                .ThenBy(email => email.Id, StringComparer.Ordinal)
                .FirstOrDefault()
            ?? throw SqlOSDomainException.Of(SqlOSDomainError.OwnershipProofMismatch);
    }

    /// <summary>True when the account has a password that can sign it in.</summary>
    internal bool HasPassword
    {
        get
        {
            Require(SqlOSUserParts.Credentials);
            return ActivePassword is not null;
        }
    }

    /// <summary>The active password credential, or null.</summary>
    internal SqlOSCredential? Password
    {
        get
        {
            Require(SqlOSUserParts.Credentials);
            return ActivePassword;
        }
    }

    /// <summary>The active authenticator with <paramref name="authenticatorId"/>, or null.</summary>
    internal SqlOSUserAuthenticator? FindAuthenticator(string authenticatorId)
    {
        Require(SqlOSUserParts.Authenticators);
        return _authenticators.FirstOrDefault(authenticator => authenticator.IsActive
            && string.Equals(authenticator.Id, authenticatorId, StringComparison.Ordinal));
    }

    /// <summary>The confirmed, active authenticator apps, newest first (the order codes are tried in).</summary>
    internal IReadOnlyList<SqlOSUserAuthenticator> ConfirmedTotpAuthenticators
    {
        get
        {
            Require(SqlOSUserParts.Authenticators);
            return _authenticators
                .Where(authenticator => authenticator.IsActive && authenticator.IsTotp && authenticator.IsConfirmed)
                .OrderByDescending(authenticator => authenticator.CreatedAt)
                .ToList();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Profile and lifecycle
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The directory that owns the person's lifecycle set the display name and default email
    /// (SCIM keeps the address exactly as the directory sent it).
    /// </summary>
    internal void UpdateProfile(string displayName, string? defaultEmail, DateTime now)
    {
        var changed = !string.Equals(DisplayName, displayName, StringComparison.Ordinal)
            || !string.Equals(DefaultEmail, defaultEmail, StringComparison.Ordinal);
        DisplayName = displayName;
        DefaultEmail = defaultEmail;
        UpdatedAt = now;
        if (changed)
        {
            _events.Raise(new UserProfileChanged(Id));
        }
    }

    /// <summary>Deactivates the account for <paramref name="reason"/>; it can no longer sign in.</summary>
    internal void Deactivate(string reason, DateTime now)
    {
        var wasActive = IsActive;
        IsActive = Enablement.Disable(reason, now).IsActive;
        UpdatedAt = now;
        if (wasActive)
        {
            _events.Raise(new UserDeactivated(Id, reason));
        }
    }

    /// <summary>Reactivates the account.</summary>
    internal void Reactivate(DateTime now)
    {
        var wasActive = IsActive;
        IsActive = Enablement.Enable().IsActive;
        UpdatedAt = now;
        if (!wasActive)
        {
            _events.Raise(new UserReactivated(Id));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Addresses
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The owner confirmed an address with the email-verification link: it is verified and becomes
    /// the default email. The link confirms the address an account registered with, so it is not a
    /// claim and evicts nothing; only that link's proof may verify without claiming.
    /// </summary>
    internal void VerifyEmail(OwnershipProof proof, DateTime now)
    {
        RequireMethod(proof, OwnershipProofMethod.EmailVerification);
        var email = EmailProvenBy(proof);
        email.Verify(now);
        SetDefaultEmail(email.Email);
        UpdatedAt = now;
        _events.Raise(new UserEmailVerified(Id, email.Id));
    }

    /// <summary>
    /// The person signed in by proving the mailbox of one of the account's addresses: that address
    /// becomes the default email.
    /// </summary>
    internal void MakeDefaultEmail(OwnershipProof proof, DateTime now)
    {
        var email = EmailProvenBy(proof);
        SetDefaultEmail(email.Email);
        UpdatedAt = now;
    }

    /// <summary>
    /// A directory set the person's primary address inside a domain its organization verified: the
    /// address is added or respelled as sent, verified, and made the one primary address. A
    /// directory never claims an address.
    /// </summary>
    internal SqlOSUserEmail SetPrimaryEmail(OwnershipProof proof, DateTime now)
    {
        RequireMethod(proof, OwnershipProofMethod.Directory);
        Require(SqlOSUserParts.Emails);
        var email = _emails.FirstOrDefault(candidate => candidate.IsCoveredBy(proof));
        if (email is null)
        {
            email = SqlOSUserEmail.Proven(Id, proof, isPrimary: true, now);
            AddEmail(email);
        }

        foreach (var other in _emails.Where(other => other.IsPrimary && !ReferenceEquals(other, email)))
        {
            other.Demote();
        }

        email.Respell(proof.Address);
        email.MakePrimary();
        email.Verify(now);
        _events.Raise(new UserPrimaryEmailSet(Id, email.Id));
        return email;
    }

    /// <summary>
    /// True when <paramref name="proof"/> would claim one of the account's addresses: it proves an
    /// address nobody has verified yet. Fails when it proves none of them.
    /// </summary>
    internal bool IsClaimableWith(OwnershipProof proof) => !EmailProvenBy(proof).IsVerified;

    /// <summary>
    /// The first proof of an unverified address claims it. Anyone could have attached the address
    /// to the account before its owner proved it (a squatter's password, an upstream identity that
    /// never verified it, an authenticator), so every password, authenticator, recovery code,
    /// phone number and external identity attached before the proof is evicted, except what
    /// <paramref name="presented"/> names, and the address is verified. Memberships are kept:
    /// organizations control them.
    /// </summary>
    /// <param name="proof">A sign-in's proof of the mailbox. The verification link and directories never claim.</param>
    /// <param name="presented">The credential the proving flow presented, which is kept.</param>
    /// <param name="evictions">The consent grants and calendar connections the claim process evicted outside the aggregate.</param>
    /// <param name="now">When the claim happened.</param>
    /// <returns>
    /// The claimed address and the identities unlinked. An unlinked identity's row is deleted by
    /// the claim process in the same save, which also takes it out of <see cref="ExternalIdentities"/>;
    /// until then it stays listed, so a claim the unit of work discards leaves the account as it was.
    /// </returns>
    internal SqlOSUserEmailClaim ClaimWithProof(
        OwnershipProof proof,
        PresentedCredentials presented,
        EmailClaimEvictions evictions,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentNullException.ThrowIfNull(evictions);
        if (proof.Method is OwnershipProofMethod.EmailVerification or OwnershipProofMethod.Directory)
        {
            throw SqlOSDomainException.Of(
                SqlOSDomainError.OwnershipProofMismatch,
                $"A {proof.MethodName} proof verifies an address but never claims one.");
        }

        Require(SqlOSUserParts.Claim);
        var email = EmailProvenBy(proof);
        if (email.IsVerified)
        {
            throw SqlOSDomainException.Of(SqlOSDomainError.InvalidMemberState, "A verified address is never claimed again.");
        }

        var passwords = _credentials.Where(credential => credential.IsActive && !presented.KeepsCredential(credential)).ToList();
        var authenticators = presented.KeepAuthenticators
            ? []
            : _authenticators.Where(authenticator => authenticator.IsActive).ToList();
        var recoveryCodes = presented.KeepAuthenticators
            ? []
            : _recoveryCodes.Where(code => code.IsUsable).ToList();
        var phoneNumbers = presented.KeepPhoneNumbers
            ? []
            : _phoneNumbers.Where(phone => phone.IsActive).ToList();
        var identities = _externalIdentities.Where(identity => !presented.KeepsIdentity(identity)).ToList();

        foreach (var password in passwords)
        {
            password.Revoke(EmailClaimedReason, now);
        }

        foreach (var authenticator in authenticators)
        {
            authenticator.Revoke(EmailClaimedReason, now);
        }

        foreach (var recoveryCode in recoveryCodes)
        {
            recoveryCode.Revoke(EmailClaimedReason, now);
        }

        foreach (var phoneNumber in phoneNumbers)
        {
            phoneNumber.Remove(EmailClaimedReason, now);
        }

        email.Verify(now);
        _events.Raise(new UserEmailClaimed(
            Id,
            email.Id,
            proof.MethodName,
            passwords.ConvertAll(static password => password.Id),
            authenticators.ConvertAll(static authenticator => authenticator.Id),
            recoveryCodes.Count,
            phoneNumbers.ConvertAll(static phone => phone.Id),
            identities.ConvertAll(static identity => new UnlinkedExternalIdentity(identity.Id, identity.Kind, identity.ConnectionId)),
            evictions.ConsentGrantIds,
            evictions.CalendarConnectionIds,
            now));
        return new SqlOSUserEmailClaim(email, identities);
    }

    // ---------------------------------------------------------------------------------------
    // Password
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Sets the account's password, the one way a password is stored: <paramref name="policy"/>
    /// decides whether <paramref name="password"/> is acceptable, then the active password is
    /// replaced, or one is added. Only its PBKDF2 hash is kept.
    /// </summary>
    internal SqlOSCredential SetPassword(string password, PasswordPolicy policy, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Enforce(password);
        Require(SqlOSUserParts.Credentials);
        var secret = HashedSecret.Pbkdf2(password);
        var credential = ActivePassword;
        if (credential is null)
        {
            credential = SqlOSCredential.Password(Id, secret, now);
            _credentials.Add(credential);
        }
        else
        {
            credential.Replace(secret);
        }

        _events.Raise(new UserPasswordSet(Id, credential.Id));
        return credential;
    }

    /// <summary>The password credential <paramref name="credentialId"/> signed the account in.</summary>
    internal void RecordPasswordSignIn(string credentialId, DateTime now)
    {
        Require(SqlOSUserParts.Credentials);
        var credential = _credentials.FirstOrDefault(candidate => candidate.IsActive
            && string.Equals(candidate.Id, credentialId, StringComparison.Ordinal));
        if (credential is null)
        {
            // Revoked between the password check and now (a concurrent claim): nothing to record.
            return;
        }

        credential.RecordUse(now);
        _events.Raise(new UserPasswordUsed(Id, credential.Id));
    }

    // ---------------------------------------------------------------------------------------
    // Phone numbers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A phone code proved <paramref name="e164PhoneNumber"/> for this account: the number is added
    /// verified (primary when the account has no other), or verified again when the account already
    /// has it. That no other account holds the number is the caller's check and the database's
    /// unique index.
    /// </summary>
    internal SqlOSUserPhoneNumber AddVerifiedPhone(string e164PhoneNumber, string protectedDisplayValue, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(e164PhoneNumber);
        Require(SqlOSUserParts.PhoneNumbers);
        var hash = SqlOSUserPhoneNumber.HashOf(e164PhoneNumber);
        var phone = _phoneNumbers.FirstOrDefault(candidate => candidate.IsActive
            && string.Equals(candidate.PhoneNumberHash, hash, StringComparison.Ordinal));
        if (phone is null)
        {
            phone = SqlOSUserPhoneNumber.Verified(
                Id,
                e164PhoneNumber,
                protectedDisplayValue,
                isPrimary: !_phoneNumbers.Any(candidate => candidate.IsActive),
                now);
            _phoneNumbers.Add(phone);
        }
        else
        {
            phone.VerifyAgain(e164PhoneNumber, protectedDisplayValue, now);
        }

        _events.Raise(new UserPhoneNumberVerified(Id, phone.Id));
        return phone;
    }

    /// <summary>A phone code signed the account in, through <paramref name="phoneNumberId"/> when the code went to a stored number.</summary>
    internal void RecordPhoneSignIn(string? phoneNumberId, DateTime now)
    {
        if (phoneNumberId is not null)
        {
            Require(SqlOSUserParts.PhoneNumbers);
            if (_phoneNumbers.FirstOrDefault(phone => string.Equals(phone.Id, phoneNumberId, StringComparison.Ordinal)) is { } phone)
            {
                phone.RecordUse(now);
                _events.Raise(new UserPhoneNumberUsed(Id, phone.Id));
            }
        }

        UpdatedAt = now;
    }

    // ---------------------------------------------------------------------------------------
    // Authenticator apps and recovery codes
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Starts an authenticator-app enrollment: a new, unconfirmed authenticator holding the
    /// protected secret. An earlier enrollment that was never confirmed is revoked.
    /// </summary>
    internal SqlOSUserAuthenticator EnrollTotp(string protectedSecret, string? displayName, TotpParameters parameters, DateTime now)
    {
        Require(SqlOSUserParts.Authenticators);
        foreach (var stale in _authenticators.Where(authenticator => authenticator.IsActive && authenticator.IsTotp && !authenticator.IsConfirmed).ToList())
        {
            stale.Revoke(ReplacedUnconfirmedReason, now);
            _events.Raise(new UserAuthenticatorRevoked(Id, stale.Id, ReplacedUnconfirmedReason));
        }

        var enrolled = SqlOSUserAuthenticator.EnrollTotp(Id, protectedSecret, displayName, parameters, now);
        _authenticators.Add(enrolled);
        _events.Raise(new UserTotpEnrollmentStarted(Id, enrolled.Id));
        return enrolled;
    }

    /// <summary>
    /// The authenticator's first code, for <paramref name="acceptedTimeStep"/>, confirmed it. An
    /// account that confirms an authenticator opts into MFA unless it already chose.
    /// </summary>
    internal void ConfirmTotp(string authenticatorId, long acceptedTimeStep, DateTime now)
    {
        Require(SqlOSUserParts.Authenticators | SqlOSUserParts.MfaPolicyOverride);
        var authenticator = FindAuthenticator(authenticatorId) is { IsTotp: true } found
            ? found
            : throw SqlOSDomainException.Of(SqlOSDomainError.UnknownMember, "The account has no such authenticator app.");
        authenticator.Confirm(acceptedTimeStep, now);
        _events.Raise(new UserTotpConfirmed(Id, authenticator.Id));
        OptIntoMfa(now);
    }

    /// <summary>
    /// Accepts the authenticator's code for <paramref name="timeStep"/>. A step at or before the
    /// last accepted one is a replay, refused with false.
    /// </summary>
    internal bool AcceptTotpCode(string authenticatorId, long timeStep, DateTime now)
    {
        var authenticator = FindAuthenticator(authenticatorId)
            ?? throw SqlOSDomainException.Of(SqlOSDomainError.UnknownMember, "The account has no such authenticator app.");
        if (!authenticator.AcceptCode(timeStep, now))
        {
            return false;
        }

        _events.Raise(new UserTotpCodeAccepted(Id, authenticator.Id));
        return true;
    }

    /// <summary>Revokes the authenticator for <paramref name="reason"/>.</summary>
    internal void RevokeAuthenticator(string authenticatorId, string reason, DateTime now)
    {
        var authenticator = FindAuthenticator(authenticatorId)
            ?? throw SqlOSDomainException.Of(SqlOSDomainError.UnknownMember, "The account has no such authenticator.");
        authenticator.Revoke(reason, now);
        _events.Raise(new UserAuthenticatorRevoked(Id, authenticator.Id, reason));
    }

    /// <summary>
    /// Replaces the account's unused recovery codes with <paramref name="normalizedCodes"/>; only
    /// their hashes are kept.
    /// </summary>
    internal void IssueRecoveryCodes(IReadOnlyCollection<string> normalizedCodes, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(normalizedCodes);
        Require(SqlOSUserParts.RecoveryCodes);
        foreach (var unused in _recoveryCodes.Where(code => code.IsUsable).ToList())
        {
            unused.Revoke("replaced", now);
        }

        foreach (var code in normalizedCodes)
        {
            _recoveryCodes.Add(SqlOSRecoveryCode.Issue(Id, code, now));
        }

        _events.Raise(new UserRecoveryCodesIssued(Id, normalizedCodes.Count));
    }

    /// <summary>Spends the unused recovery code <paramref name="normalizedCode"/>; false when the account has none.</summary>
    internal bool UseRecoveryCode(string normalizedCode, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedCode);
        Require(SqlOSUserParts.RecoveryCodes);
        var code = _recoveryCodes.FirstOrDefault(candidate => candidate.IsUsable && candidate.Matches(normalizedCode));
        if (code is null)
        {
            return false;
        }

        code.Use(now);
        _events.Raise(new UserRecoveryCodeUsed(Id, code.Id));
        return true;
    }

    // ---------------------------------------------------------------------------------------
    // External identities
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Links an upstream identity to this existing account. Its provider must have proven one of
    /// the account's addresses (<paramref name="proof"/>, from the same kind of provider), and the
    /// address must be verified: an unverified one is claimed first, so nothing a squatter attached
    /// survives the link (#423).
    /// </summary>
    internal SqlOSExternalIdentity LinkExternalIdentity(ExternalIdentityLink identity, OwnershipProof proof, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(proof);
        if (!identity.IsProvenBy(proof))
        {
            throw SqlOSDomainException.Of(
                SqlOSDomainError.OwnershipProofMismatch,
                $"An {identity.KindName} identity is linked only with its own provider's proof.");
        }

        Require(SqlOSUserParts.ExternalIdentities);
        if (!EmailProvenBy(proof).IsVerified)
        {
            throw SqlOSDomainException.Of(
                SqlOSDomainError.InvalidMemberState,
                "An identity is linked to an unverified address only after the address is claimed.");
        }

        return AddIdentity(identity, now);
    }

    // ---------------------------------------------------------------------------------------
    // Loading
    // ---------------------------------------------------------------------------------------

    /// <summary>Records that <paramref name="parts"/> are loaded. Only the loader (<c>SqlOSUsers</c>) calls this.</summary>
    internal void MarkLoaded(SqlOSUserParts parts) => _loaded |= parts;

    /// <summary>Fails unless every part in <paramref name="parts"/> is loaded.</summary>
    internal void Require(SqlOSUserParts parts)
    {
        var missing = parts & ~_loaded;
        if (missing != SqlOSUserParts.None)
        {
            throw SqlOSDomainException.Of(
                SqlOSDomainError.AggregatePartNotLoaded,
                $"The user's {missing} must be loaded before this change.");
        }
    }

    private SqlOSCredential? ActivePassword
        => _credentials.FirstOrDefault(credential => credential.IsActive && credential.IsPassword);

    private void AddEmail(SqlOSUserEmail email)
    {
        _emails.Add(email);
        _events.Raise(new UserEmailAdded(Id, email.Id, email.IsVerified));
    }

    private void SetDefaultEmail(string? address)
    {
        if (!string.Equals(DefaultEmail, address, StringComparison.Ordinal))
        {
            DefaultEmail = address;
            _events.Raise(new UserProfileChanged(Id));
        }
    }

    private void LinkFoundingIdentity(ExternalIdentityLink identity, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        AddIdentity(identity, now);
        if (identity.Kind == ExternalIdentityKind.Oidc)
        {
            _events.Raise(new UserProvisionedFromOidc(Id, identity.Provider!, identity.ConnectionId));
        }
    }

    private SqlOSExternalIdentity AddIdentity(ExternalIdentityLink identity, DateTime now)
    {
        var linked = SqlOSExternalIdentity.Link(Id, identity, now);
        _externalIdentities.Add(linked);
        _events.Raise(new UserExternalIdentityLinked(Id, linked.Id, identity.KindName, identity.ConnectionId));
        return linked;
    }

    private void OptIntoMfa(DateTime now)
    {
        if (MfaPolicyOverride is null)
        {
            MfaPolicyOverride = SqlOSUserMfaPolicyOverride.OptedIn(Id, now);
            _events.Raise(new UserOptedIntoMfa(Id));
        }
        else if (MfaPolicyOverride.OptIn(now))
        {
            _events.Raise(new UserOptedIntoMfa(Id));
        }
    }

    private static void RequireMethod(OwnershipProof proof, OwnershipProofMethod method)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.Method != method)
        {
            throw SqlOSDomainException.Of(
                SqlOSDomainError.OwnershipProofMismatch,
                $"This change takes a {OwnershipProof.NameOf(method)} proof, not a {proof.MethodName} proof.");
        }
    }
}
