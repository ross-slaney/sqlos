namespace SqlOS.Domain;

/// <summary>
/// The stable code of a broken domain invariant, carried by <see cref="SqlOSDomainException"/>.
/// </summary>
/// <remarks>
/// Codes are part of the logs, so never renumber or reuse one; add new codes at the end. Expected
/// results such as a wrong code or a refusal are outcome cases of a process, not errors.
/// </remarks>
internal enum SqlOSDomainError
{
    /// <summary>The expiry has passed (<see cref="Expiry"/>).</summary>
    Expired = 1,

    /// <summary>A single-use item was used again (<see cref="Consumption"/>).</summary>
    AlreadyConsumed = 2,

    /// <summary>The item has been revoked (<see cref="Revocation"/>).</summary>
    Revoked = 3,

    /// <summary>No attempt remains in the budget (<see cref="AttemptBudget"/>).</summary>
    AttemptsExhausted = 4,

    /// <summary>The item is disabled (<see cref="Enablement"/>).</summary>
    Disabled = 5,

    /// <summary>The input is not a valid email address (<see cref="EmailAddress"/>).</summary>
    InvalidEmailAddress = 6,

    /// <summary>The input is not a domain an organization can claim (<see cref="DomainName"/>).</summary>
    InvalidDomainName = 7,

    /// <summary>The input is not a valid phone number (<see cref="PhoneNumber"/>).</summary>
    InvalidPhoneNumber = 8,

    /// <summary>The input is not an absolute URI (<see cref="RedirectUri"/>).</summary>
    InvalidRedirectUri = 9,

    /// <summary>The password policy refused a password (<c>PasswordPolicy</c>).</summary>
    PasswordRejected = 10,

    /// <summary>
    /// An ownership proof does not cover the address it was presented for: it proves another
    /// mailbox (<see cref="OwnershipProof"/>).
    /// </summary>
    OwnershipProofMismatch = 11,

    /// <summary>
    /// An aggregate was asked to decide with a part of it that was not loaded, so it cannot see
    /// everything its rule covers.
    /// </summary>
    AggregatePartNotLoaded = 12,

    /// <summary>The aggregate has no member with the given identity (for example an authenticator).</summary>
    UnknownMember = 13,

    /// <summary>The member is not in a state that allows the change (for example an authenticator already confirmed).</summary>
    InvalidMemberState = 14
}
