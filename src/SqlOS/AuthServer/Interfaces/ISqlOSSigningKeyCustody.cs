namespace SqlOS.AuthServer.Interfaces;

/// <summary>
/// Owns access-token signing private keys. Implementations may wrap ASP.NET Core Data Protection,
/// Azure Key Vault, AWS KMS, Vault Transit, an HSM, or another service that can create RSA keys and
/// produce RS256 signatures without exposing private key material to SqlOS.
/// </summary>
public interface ISqlOSSigningKeyCustody
{
    /// <summary>A stable identifier persisted with every key and used to prevent provider substitution.</summary>
    string ProviderId { get; }

    /// <summary>Creates a key and returns public validation material plus an opaque provider reference.</summary>
    Task<SqlOSSigningKeyCreationResult> CreateKeyAsync(
        string kid,
        string algorithm,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs the exact JWT signing input. For RS256, return an RSA PKCS#1 v1.5 SHA-256 signature.
    /// </summary>
    Task<byte[]> SignAsync(
        SqlOSSigningKeyDescriptor key,
        ReadOnlyMemory<byte> signingInput,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes or schedules destruction of provider-owned private material after SqlOS' retired-key
    /// cleanup window. Implementations must be idempotent because an external deletion and the
    /// corresponding database update cannot be committed atomically. Implementations may no-op when
    /// deleting the database reference is sufficient.
    /// </summary>
    Task DeleteKeyAsync(
        SqlOSSigningKeyDescriptor key,
        CancellationToken cancellationToken = default);
}

public sealed record SqlOSSigningKeyCreationResult(
    string Algorithm,
    string PublicKeyPem,
    string KeyReference);

public sealed record SqlOSSigningKeyDescriptor(
    string Kid,
    string Algorithm,
    string PublicKeyPem,
    string KeyReference,
    string CustodyProvider);
