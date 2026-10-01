using SqlOS.Domain;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// A one-time MFA recovery code of a <see cref="SqlOSUser"/>, stored only as the SHA-256 hash of
/// its normalized form (<see cref="HashedSecret"/>).
/// </summary>
/// <remarks>
/// Part of the <see cref="SqlOSUser"/> aggregate. A code is spent once (<see cref="Domain.Consumption"/>;
/// <see cref="ConsumedAt"/> is a concurrency token, so two requests cannot both spend it) and is
/// revoked when a new set replaces it or a claim of the account's address evicts it
/// (<see cref="Domain.Revocation"/> over <see cref="RevokedAt"/>; the table has no reason column).
/// </remarks>
public sealed class SqlOSRecoveryCode
{
    private SqlOSRecoveryCode()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string UserId { get; private set; } = string.Empty;
    public string CodeHash { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public DateTime? ConsumedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    public SqlOSUser? User { get; private set; }

    internal Consumption Consumption => new(ConsumedAt);

    internal Revocation Revocation => new(RevokedAt, null);

    internal HashedSecret Code => HashedSecret.FromStored(HashedSecretScheme.Sha256, CodeHash);

    /// <summary>True while the code can still be spent: neither spent nor revoked.</summary>
    internal bool IsUsable => !Consumption.IsConsumed && !Revocation.IsRevoked;

    /// <summary>A new code of <paramref name="userId"/>; only the hash of <paramref name="normalizedCode"/> is kept.</summary>
    internal static SqlOSRecoveryCode Issue(string userId, string normalizedCode, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedCode);
        return new SqlOSRecoveryCode
        {
            Id = SqlOSIds.New("mrc"),
            UserId = userId,
            CodeHash = HashedSecret.Sha256(normalizedCode).Hash,
            CreatedAt = now
        };
    }

    /// <summary>True when <paramref name="normalizedCode"/> is this code (compared in constant time).</summary>
    internal bool Matches(string normalizedCode) => Code.Matches(normalizedCode);

    internal void Use(DateTime now)
    {
        Revocation.EnsureNotRevoked();
        ConsumedAt = Consumption.Consume(now).ConsumedAt;
    }

    internal void Revoke(string reason, DateTime now) => RevokedAt = Revocation.Revoke(reason, now).RevokedAt;
}
