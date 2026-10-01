namespace SqlOS.Domain;

/// <summary>
/// A broken domain invariant, identified by a stable <see cref="SqlOSDomainError"/> code.
/// </summary>
/// <remarks>
/// <para>
/// It derives from <see cref="InvalidOperationException"/> because SqlOS 7.x reported validation
/// failures as <see cref="InvalidOperationException"/>: code (and hosts) that caught those keep
/// catching these. A value object rule throws with the exact 7.x message for that rule, so a
/// message-based mapping such as <c>SqlOSPublicAuthErrorMapper</c> maps it as before. Lifecycle
/// codes carry a short diagnostic message; adapters map them to the public error of the flow.
/// </para>
/// <para>
/// The exception type name is observable wherever 7.x recorded one (the <c>failureType</c> of
/// <c>auth.public_error.mapped</c>), so an adapter must not let this exception reach that audit
/// where 7.x threw a plain <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
internal sealed class SqlOSDomainException : InvalidOperationException
{
    private SqlOSDomainException(SqlOSDomainError error, string message)
        : base(message)
    {
        Error = error;
    }

    public SqlOSDomainError Error { get; }

    /// <summary>The error with its default message.</summary>
    public static SqlOSDomainException Of(SqlOSDomainError error)
        => new(error, DefaultMessage(error));

    /// <summary>The error with a specific message, such as the 7.x message of a validation rule.</summary>
    public static SqlOSDomainException Of(SqlOSDomainError error, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new SqlOSDomainException(error, message);
    }

    internal static string DefaultMessage(SqlOSDomainError error)
        => error switch
        {
            SqlOSDomainError.Expired => "It has expired.",
            SqlOSDomainError.AlreadyConsumed => "It has already been used.",
            SqlOSDomainError.Revoked => "It has been revoked.",
            SqlOSDomainError.AttemptsExhausted => "No attempts remain.",
            SqlOSDomainError.Disabled => "It is disabled.",
            SqlOSDomainError.InvalidEmailAddress => EmailAddress.InvalidMessage,
            SqlOSDomainError.InvalidDomainName => DomainName.InvalidDnsNameMessage,
            SqlOSDomainError.InvalidPhoneNumber => PhoneNumber.InvalidMessage,
            SqlOSDomainError.InvalidRedirectUri => RedirectUri.InvalidMessage,
            SqlOSDomainError.PasswordRejected => "The password does not meet the password policy.",
            SqlOSDomainError.OwnershipProofMismatch => OwnershipProof.MismatchMessage,
            SqlOSDomainError.AggregatePartNotLoaded => "A part of the aggregate the change needs was not loaded.",
            SqlOSDomainError.UnknownMember => "The aggregate has no such member.",
            SqlOSDomainError.InvalidMemberState => "The member's state does not allow this change.",
            _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Unknown domain error.")
        };
}
