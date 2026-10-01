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
    InvalidRedirectUri = 9
}
