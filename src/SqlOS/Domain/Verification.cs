namespace SqlOS.Domain;

/// <summary>
/// Verified ownership (<c>IsVerified</c> and <c>VerifiedAt</c>) of an email address, a phone
/// number or a domain claim. <see cref="IsVerified"/> is the source of truth; hosts and queries
/// filter on it.
/// </summary>
/// <remarks>
/// Verifying is idempotent and keeps the time ownership was first proven: an address that is
/// already verified keeps its <see cref="VerifiedAt"/>, and only fills it in when an older row has
/// none (the 7.x <c>VerifiedAt ??= now</c> rule). Verifying an unverified item records
/// <c>now</c>. Which proof may verify what is the aggregate's rule, not this part's.
/// </remarks>
internal readonly record struct Verification(bool IsVerified, DateTime? VerifiedAt)
{
    public Verification Verify(DateTime now)
        => IsVerified
            ? new Verification(true, VerifiedAt ?? now)
            : new Verification(true, now);
}
