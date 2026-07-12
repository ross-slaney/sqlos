namespace SqlOS.AuthServer.Interfaces;

/// <summary>
/// Internal boundary for access-token signing-key operations. Production uses the built-in
/// ASP.NET Core Data Protection implementation; tests replace it to exercise failure behavior.
/// </summary>
internal interface ISqlOSSigningKeyCustody
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

internal sealed record SqlOSSigningKeyCreationResult(
    string Algorithm,
    string PublicKeyPem,
    string KeyReference);

internal sealed record SqlOSSigningKeyDescriptor(
    string Kid,
    string Algorithm,
    string PublicKeyPem,
    string KeyReference,
    string CustodyProvider);
