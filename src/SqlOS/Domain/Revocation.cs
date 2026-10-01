namespace SqlOS.Domain;

/// <summary>
/// Revocation (<c>RevokedAt</c> and, where the entity stores one, <c>RevocationReason</c>).
/// Revoking is idempotent: the first revocation's time and reason are kept.
/// </summary>
/// <remarks>
/// Entities without a reason column build the part with a <see langword="null"/> reason and store
/// only <see cref="RevokedAt"/>.
/// </remarks>
internal readonly record struct Revocation(DateTime? RevokedAt, string? Reason)
{
    public bool IsRevoked => RevokedAt is not null;

    public Revocation Revoke(string reason, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return IsRevoked ? this : new Revocation(now, reason);
    }

    public void EnsureNotRevoked()
    {
        if (IsRevoked)
        {
            throw SqlOSDomainException.Of(SqlOSDomainError.Revoked);
        }
    }
}
