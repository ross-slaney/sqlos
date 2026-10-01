using SqlOS.Domain;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// A phone number of a <see cref="SqlOSUser"/> that a phone code proved: the E.164 number, its
/// SHA-256 hash (how SqlOS looks numbers up), and the number protected with the host's data
/// protection for display.
/// </summary>
/// <remarks>
/// Part of the <see cref="SqlOSUser"/> aggregate. A number is added only verified, and removed
/// (<see cref="RemovedAt"/>, <see cref="RemovalReason"/>, the shared <see cref="Domain.Revocation"/>
/// part) only by a claim of the account's address. A number belongs to one account at a time; the
/// database's unique index on the hash of active numbers keeps that across accounts.
/// </remarks>
public sealed class SqlOSUserPhoneNumber
{
    private SqlOSUserPhoneNumber()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string UserId { get; private set; } = string.Empty;
    public string PhoneNumber { get; private set; } = string.Empty;
    public string PhoneNumberHash { get; private set; } = string.Empty;
    public string? DisplayValueEncrypted { get; private set; }
    public bool IsPrimary { get; private set; }
    public bool IsVerified { get; private set; }
    public DateTime? VerifiedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? LastUsedAt { get; private set; }
    public DateTime? RemovedAt { get; private set; }
    public string? RemovalReason { get; private set; }

    public SqlOSUser? User { get; private set; }

    internal Verification Verification => new(IsVerified, VerifiedAt);

    internal Revocation Removal => new(RemovedAt, RemovalReason);

    /// <summary>True until the number is removed from the account.</summary>
    internal bool IsActive => !Removal.IsRevoked;

    /// <summary>The hash SqlOS stores and looks a number up by (the 7.x <c>HashToken</c> of the E.164 form).</summary>
    internal static string HashOf(string e164PhoneNumber) => HashedSecret.Sha256(e164PhoneNumber).Hash;

    /// <summary>A verified number of <paramref name="userId"/>.</summary>
    internal static SqlOSUserPhoneNumber Verified(
        string userId,
        string e164PhoneNumber,
        string protectedDisplayValue,
        bool isPrimary,
        DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(e164PhoneNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedDisplayValue);
        var phone = new SqlOSUserPhoneNumber
        {
            Id = SqlOSIds.New("phn"),
            UserId = userId,
            PhoneNumber = e164PhoneNumber,
            PhoneNumberHash = HashOf(e164PhoneNumber),
            DisplayValueEncrypted = protectedDisplayValue,
            IsPrimary = isPrimary,
            CreatedAt = now,
            UpdatedAt = now
        };
        phone.Verify(now);
        return phone;
    }

    /// <summary>The number was proven again: it is verified (keeping the first time) and stored as proven.</summary>
    internal void VerifyAgain(string e164PhoneNumber, string protectedDisplayValue, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(e164PhoneNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedDisplayValue);
        Verify(now);
        PhoneNumber = e164PhoneNumber;
        DisplayValueEncrypted = protectedDisplayValue;
        UpdatedAt = now;
    }

    /// <summary>A code sent to this number signed the account in.</summary>
    internal void RecordUse(DateTime now)
    {
        LastUsedAt = now;
        UpdatedAt = now;
    }

    internal void Remove(string reason, DateTime now)
    {
        (RemovedAt, RemovalReason) = Removal.Revoke(reason, now);
        UpdatedAt = now;
    }

    private void Verify(DateTime now) => (IsVerified, VerifiedAt) = Verification.Verify(now);
}
