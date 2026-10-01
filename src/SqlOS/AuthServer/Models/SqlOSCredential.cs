using SqlOS.Domain;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// A password of a <see cref="SqlOSUser"/>, stored only as the ASP.NET Core Identity PBKDF2 hash
/// (<see cref="HashedSecret"/>).
/// </summary>
/// <remarks>
/// Part of the <see cref="SqlOSUser"/> aggregate: a password is set only through
/// <see cref="SqlOSUser.SetPassword"/>, which applies the password policy on every path, and
/// revoked only by a claim of the account's address (<see cref="Domain.Revocation"/> over
/// <see cref="RevokedAt"/>; the table has no reason column).
/// </remarks>
public sealed class SqlOSCredential
{
    internal const string PasswordType = "password";

    private SqlOSCredential()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string UserId { get; private set; } = string.Empty;
    public string Type { get; private set; } = PasswordType;
    public string SecretHash { get; private set; } = string.Empty;
    public int SecretVersion { get; private set; } = 1;
    public DateTime? LastUsedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    public SqlOSUser? User { get; private set; }

    internal Revocation Revocation => new(RevokedAt, null);

    /// <summary>True until the credential is revoked.</summary>
    internal bool IsActive => !Revocation.IsRevoked;

    internal bool IsPassword => string.Equals(Type, PasswordType, StringComparison.Ordinal);

    internal HashedSecret Secret => HashedSecret.FromStored(HashedSecretScheme.Pbkdf2, SecretHash);

    /// <summary>A new password credential of <paramref name="userId"/>, holding only <paramref name="secret"/>'s hash.</summary>
    internal static SqlOSCredential Password(string userId, HashedSecret secret, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        EnsurePbkdf2(secret);
        return new SqlOSCredential
        {
            Id = SqlOSIds.New("cred"),
            UserId = userId,
            Type = PasswordType,
            SecretHash = secret.Hash,
            SecretVersion = 1,
            CreatedAt = now
        };
    }

    /// <summary>Replaces the password; the new one has not signed in yet.</summary>
    internal void Replace(HashedSecret secret)
    {
        EnsurePbkdf2(secret);
        SecretHash = secret.Hash;
        LastUsedAt = null;
    }

    internal void RecordUse(DateTime now) => LastUsedAt = now;

    internal void Revoke(string reason, DateTime now) => RevokedAt = Revocation.Revoke(reason, now).RevokedAt;

    private static void EnsurePbkdf2(HashedSecret secret)
    {
        if (secret.Scheme != HashedSecretScheme.Pbkdf2)
        {
            throw new ArgumentException("A password is stored only as its PBKDF2 hash.", nameof(secret));
        }
    }
}
