using SqlOS.Domain;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// An address of a <see cref="SqlOSUser"/>: the form SqlOS stores and shows (<see cref="Email"/>),
/// the canonical key that decides whose account it is (<see cref="NormalizedEmail"/>, #422), and
/// whether its owner has proven the mailbox (<see cref="IsVerified"/>).
/// </summary>
/// <remarks>
/// Part of the <see cref="SqlOSUser"/> aggregate: it is created and changed only through its
/// user, which decides when an address may be verified (only with an <see cref="OwnershipProof"/>)
/// and keeps one primary address. Verification is the shared <see cref="Domain.Verification"/>
/// part over the <see cref="IsVerified"/> and <see cref="VerifiedAt"/> columns.
/// </remarks>
public sealed class SqlOSUserEmail
{
    private SqlOSUserEmail()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string UserId { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string NormalizedEmail { get; private set; } = string.Empty;
    public bool IsPrimary { get; private set; }
    public bool IsVerified { get; private set; }
    public DateTime? VerifiedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public SqlOSUser? User { get; private set; }

    internal Verification Verification => new(IsVerified, VerifiedAt);

    /// <summary>A new, unverified address of <paramref name="userId"/>.</summary>
    internal static SqlOSUserEmail Unverified(string userId, EmailAddress address, bool isPrimary, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(address);
        return new SqlOSUserEmail
        {
            Id = SqlOSIds.New("eml"),
            UserId = userId,
            Email = address.Address,
            NormalizedEmail = address.Canonical,
            IsPrimary = isPrimary,
            CreatedAt = now
        };
    }

    /// <summary>A new address of <paramref name="userId"/>, verified by the proof of its mailbox.</summary>
    internal static SqlOSUserEmail Proven(string userId, OwnershipProof proof, bool isPrimary, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var email = Unverified(userId, proof.Address, isPrimary, now);
        email.Verify(now);
        return email;
    }

    /// <summary>True when <paramref name="proof"/> proves this stored mailbox.</summary>
    internal bool IsCoveredBy(OwnershipProof proof) => proof.Covers(Email);

    /// <summary>Records that the owner proved the mailbox. An address verified before keeps the time it was first proven.</summary>
    internal void Verify(DateTime now) => (IsVerified, VerifiedAt) = Verification.Verify(now);

    /// <summary>Stores <paramref name="address"/>, the same mailbox spelled as a directory sent it, under its canonical key.</summary>
    internal void Respell(EmailAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        Email = address.Address;
        NormalizedEmail = address.Canonical;
    }

    internal void MakePrimary() => IsPrimary = true;

    internal void Demote() => IsPrimary = false;
}
